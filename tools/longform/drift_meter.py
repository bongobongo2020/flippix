"""Drift meter for long H3 videos.

Samples a video every `--step` seconds and reports, per sample, against the first
`--anchor` seconds of the video (or an explicit --ref image for identity):
  dE    CIE76 distance of the frame's mean LAB colour from the anchor's
  sat   mean HSV saturation (0..1)
  sharp Laplacian variance relative to the anchor (1.00 = same)
  faceX ArcFace cosine, per reference face (left to right A, B..), best match in the frame (CPU only)

Usage: python drift_meter.py video.mp4 [--step 2] [--ref opening.png] [--json out.json]
"""
import argparse, json, sys
import cv2
import numpy as np


def frames_every(path, step):
    cap = cv2.VideoCapture(path)
    fps = cap.get(cv2.CAP_PROP_FPS) or 24
    n = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    every = max(1, int(round(fps * step)))
    for i in range(0, n, every):
        cap.set(cv2.CAP_PROP_POS_FRAMES, i)
        ok, f = cap.read()
        if ok:
            yield i / fps, f
    cap.release()


def stats(bgr):
    lab = cv2.cvtColor(bgr, cv2.COLOR_BGR2LAB).astype(np.float32)
    L = lab[..., 0] * 100 / 255
    a = lab[..., 1] - 128
    b = lab[..., 2] - 128
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    return dict(L=float(L.mean()), a=float(a.mean()), b=float(b.mean()),
                sat=float(hsv[..., 1].mean() / 255),
                sharp=float(cv2.Laplacian(gray, cv2.CV_64F).var()))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("video")
    ap.add_argument("--step", type=float, default=2.0)
    ap.add_argument("--anchor", type=float, default=1.0)
    ap.add_argument("--ref")
    ap.add_argument("--json")
    a = ap.parse_args()

    from insightface.app import FaceAnalysis
    fa = FaceAnalysis(name="buffalo_l", providers=["CPUExecutionProvider"],
                      allowed_modules=["detection", "recognition"])
    fa.prepare(ctx_id=-1, det_size=(640, 640))

    def face_embs(bgr):
        return [f.normed_embedding for f in fa.get(bgr)]

    def face_emb(bgr):  # every face in the frame; identity is scored per reference face
        e = face_embs(bgr)
        return e or None

    rows = [dict(t=t, **stats(f), emb=face_emb(f)) for t, f in frames_every(a.video, a.step)]
    if not rows:
        sys.exit("no frames")
    anc = [r for r in rows if r["t"] <= a.anchor] or rows[:1]
    ref = {k: float(np.mean([r[k] for r in anc])) for k in ("L", "a", "b", "sat", "sharp")}
    if a.ref:
        ref_img = cv2.imread(a.ref)
        refs = sorted(fa.get(ref_img), key=lambda f: f.bbox[0])  # left to right: A, B, ...
        ref_embs = [f.normed_embedding for f in refs]
    else:
        ref_embs = next((r["emb"] for r in anc if r["emb"] is not None), None) or []
    names = [chr(65 + i) for i in range(len(ref_embs))]
    print("reference faces:", names)

    out = []
    for r in rows:
        dE = float(np.sqrt((r["L"] - ref["L"]) ** 2 + (r["a"] - ref["a"]) ** 2 + (r["b"] - ref["b"]) ** 2))
        per = {}
        for n, re_ in zip(names, ref_embs):
            per[n] = None if not r["emb"] else round(float(max(np.dot(e, re_) for e in r["emb"])), 3)
        out.append(dict(t=round(r["t"], 1), dE=round(dE, 2), L=round(r["L"], 1), a=round(r["a"], 2),
                        b=round(r["b"], 2), sat=round(r["sat"], 3), sharp=round(r["sharp"] / ref["sharp"], 2),
                        **{f"face{n}": v for n, v in per.items()}))

    print(f"{a.video}  ({len(out)} samples, every {a.step}s)")
    fk = [f"face{n}" for n in names]
    print(f"{'t':>6} {'dE':>6} {'L':>6} {'a':>6} {'b':>6} {'sat':>6} {'sharp':>6} " + " ".join(f"{k:>6}" for k in fk))
    for o in out:
        print(f"{o['t']:>6} {o['dE']:>6} {o['L']:>6} {o['a']:>6} {o['b']:>6} {o['sat']:>6} {o['sharp']:>6} "
              + " ".join(f"{'-' if o[k] is None else o[k]:>6}" for k in fk))

    def window(lo, hi):
        w = [o for o in out if lo <= o["t"] < hi]
        res = dict(dE=round(float(np.mean([o["dE"] for o in w])), 2) if w else None,
                   sat=round(float(np.mean([o["sat"] for o in w])), 3) if w else None,
                   sharp=round(float(np.mean([o["sharp"] for o in w])), 2) if w else None)
        for k in fk:
            fs = [o[k] for o in w if o[k] is not None]
            res[k] = round(float(np.mean(fs)), 3) if fs else None
        return res
    dur = out[-1]["t"]
    summary = {f"{lo}-{hi}s": window(lo, hi) for lo, hi in
               [(0, 10), (10, 30), (30, 60), (60, 90), (90, 10_000)] if lo <= dur}
    print("summary", json.dumps(summary))
    if a.json:
        json.dump(dict(video=a.video, samples=out, summary=summary), open(a.json, "w"), indent=1)


if __name__ == "__main__":
    main()
