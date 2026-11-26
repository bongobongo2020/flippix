"""Scores the FFLF bake-off renders in results/: face likeness to the opening image (ArcFace), how often
each person's face is found at all, sharpness, and speed. Also writes a contact sheet per config.

Needs onnxruntime + opencv + numpy (the Rope conda env has them) and InsightFace's buffalo_l models
(det_10g.onnx = SCRFD detector, w600k_r50.onnx = ArcFace).

Usage:  <rope python> score.py [--models <buffalo_l dir>]
"""

import glob
import json
import os
import sys

import cv2
import numpy as np
import onnxruntime as ort

HERE = os.path.dirname(os.path.abspath(__file__))
RESULTS = os.path.join(HERE, 'results')
MODELS = sys.argv[sys.argv.index('--models') + 1] if '--models' in sys.argv else r'E:\aimodels\comfyui\models\insightface\models\buffalo_l'
SETTINGS = os.path.expandvars(r'%APPDATA%\FlipPix\settings.json')
SAMPLE_FPS = 4
PROVIDERS = ['CPUExecutionProvider']

ARC_DST = np.array([[38.2946, 51.6963], [73.5318, 51.5014], [56.0252, 71.7366], [41.5493, 92.3655], [70.7299, 92.2041]], np.float32)


class Scrfd:
    def __init__(self, path):
        self.s = ort.InferenceSession(path, providers=PROVIDERS)
        self.inp = self.s.get_inputs()[0].name
        self.size = 640

    def detect(self, img, thresh=0.5):
        h, w = img.shape[:2]
        scale = self.size / max(h, w)
        nh, nw = int(round(h * scale)), int(round(w * scale))
        canvas = np.zeros((self.size, self.size, 3), np.uint8)
        canvas[:nh, :nw] = cv2.resize(img, (nw, nh))
        blob = cv2.dnn.blobFromImage(canvas, 1.0 / 128, (self.size, self.size), (127.5, 127.5, 127.5), swapRB=True)
        outs = self.s.run(None, {self.inp: blob})
        boxes, scores, kpss = [], [], []
        for idx, stride in enumerate((8, 16, 32)):
            sc, bb, kp = outs[idx][..., 0].reshape(-1), outs[idx + 3].reshape(-1, 4) * stride, outs[idx + 6].reshape(-1, 10) * stride
            fh = fw = self.size // stride
            centers = np.stack(np.mgrid[:fh, :fw][::-1], axis=-1).astype(np.float32).reshape(-1, 2) * stride
            centers = np.repeat(centers, 2, axis=0)
            keep = np.where(sc >= thresh)[0]
            c = centers[keep]
            b = bb[keep]
            boxes.append(np.stack([c[:, 0] - b[:, 0], c[:, 1] - b[:, 1], c[:, 0] + b[:, 2], c[:, 1] + b[:, 3]], -1))
            k = kp[keep].reshape(-1, 5, 2) + c[:, None, :]
            kpss.append(k)
            scores.append(sc[keep])
        boxes, scores, kpss = np.concatenate(boxes) / scale, np.concatenate(scores), np.concatenate(kpss) / scale
        order = scores.argsort()[::-1]
        keep = []
        while order.size:
            i = order[0]
            keep.append(i)
            xx1 = np.maximum(boxes[i, 0], boxes[order[1:], 0]); yy1 = np.maximum(boxes[i, 1], boxes[order[1:], 1])
            xx2 = np.minimum(boxes[i, 2], boxes[order[1:], 2]); yy2 = np.minimum(boxes[i, 3], boxes[order[1:], 3])
            inter = np.maximum(0, xx2 - xx1) * np.maximum(0, yy2 - yy1)
            area = lambda j: (boxes[j, 2] - boxes[j, 0]) * (boxes[j, 3] - boxes[j, 1])
            iou = inter / (area(i) + area(order[1:]) - inter + 1e-9)
            order = order[1:][iou < 0.4]
        return [(boxes[i], scores[i], kpss[i]) for i in keep]


class ArcFace:
    def __init__(self, path):
        self.s = ort.InferenceSession(path, providers=PROVIDERS)
        self.inp = self.s.get_inputs()[0].name

    def embed(self, img, kps):
        m, _ = cv2.estimateAffinePartial2D(kps.astype(np.float32), ARC_DST, method=cv2.LMEDS)
        crop = cv2.warpAffine(img, m, (112, 112), borderValue=0)
        blob = cv2.dnn.blobFromImage(crop, 1.0 / 127.5, (112, 112), (127.5, 127.5, 127.5), swapRB=True)
        e = self.s.run(None, {self.inp: blob})[0][0]
        return e / np.linalg.norm(e), crop


def frames(path, fps=SAMPLE_FPS):
    cap = cv2.VideoCapture(path)
    src = cap.get(cv2.CAP_PROP_FPS) or 24
    step = max(1, int(round(src / fps)))
    i, out = 0, []
    while True:
        ok, f = cap.read()
        if not ok:
            break
        if i % step == 0:
            out.append(f)
        i += 1
    cap.release()
    return out


def motion(path, width=480, gap=2):
    """Subject motion and morphing, from Farneback optical flow between frames `gap` apart (12/s at 24 fps).
    motion: mean flow magnitude after the camera's own move (the median flow) is taken out, in px at 480 wide.
    warp_err: how much of the next frame the flow cannot explain (mean abs grey diff after warping, 0-255) —
    high when limbs smear, melt or pop rather than move."""
    cap = cv2.VideoCapture(path)
    grays = []
    while True:
        ok, f = cap.read()
        if not ok:
            break
        h, w = f.shape[:2]
        grays.append(cv2.cvtColor(cv2.resize(f, (width, int(h * width / w))), cv2.COLOR_BGR2GRAY))
    cap.release()
    mags, errs, cams = [], [], []
    if not grays:
        return [0.0], [0.0], [0.0]
    hh, ww = grays[0].shape
    gx, gy = np.meshgrid(np.arange(ww, dtype=np.float32), np.arange(hh, dtype=np.float32))
    for i in range(0, len(grays) - gap, gap):
        a, b = grays[i], grays[i + gap]
        flow = cv2.calcOpticalFlowFarneback(a, b, None, 0.5, 4, 21, 3, 7, 1.5, 0)
        cam = np.median(flow.reshape(-1, 2), axis=0)
        rel = flow - cam
        mags.append(float(np.linalg.norm(rel, axis=2).mean()))
        cams.append(float(np.linalg.norm(cam)))
        warped = cv2.remap(b, gx + flow[..., 0], gy + flow[..., 1], cv2.INTER_LINEAR)
        errs.append(float(np.abs(warped.astype(np.float32) - a.astype(np.float32))[8:-8, 8:-8].mean()))
    return mags, errs, cams


def sharpness(gray):
    return float(cv2.Laplacian(gray, cv2.CV_64F).var())


def main():
    det = Scrfd(os.path.join(MODELS, 'det_10g.onnx'))
    arc = ArcFace(os.path.join(MODELS, 'w600k_r50.onnx'))
    st = json.load(open(SETTINGS, encoding='utf-8-sig'))['H3Fflf']

    # The cast: faces in the opening image, left to right = A, B (as the description letters them).
    opening = cv2.imread(st['InitialImagePath'])
    faces = sorted(det.detect(opening, 0.5), key=lambda f: f[0][0])
    refs = {}
    for letter, (box, score, kps) in zip('AB', faces):
        refs[letter] = arc.embed(opening, kps)[0]
    # The character sheets: one more view per person, averaged in.
    for s in st.get('CharacterSheetList', []):
        if s['Letter'] in refs and os.path.exists(s['Path']):
            img = cv2.imread(s['Path'])
            embs = [arc.embed(img, k)[0] for b, sc, k in det.detect(img, 0.6) if (b[2] - b[0]) >= 48]
            if embs:
                sims = [float(e @ refs[s['Letter']]) for e in embs]
                print(f"sheet {s['Letter']}: {len(embs)} faces, sim to opening face {np.mean(sims):.2f}")
    print('reference faces:', list(refs), ' A·B similarity', round(float(refs['A'] @ refs['B']), 2) if len(refs) == 2 else '')

    rows = []
    for meta_path in sorted(glob.glob(os.path.join(RESULTS, '*', 'meta.json'))):
        meta = json.load(open(meta_path))
        name = meta['name']
        d = os.path.dirname(meta_path)
        if not meta.get('ok'):
            rows.append({'name': name, 'error': meta.get('error', '')[:200]})
            continue
        row = {'name': name, 'note': meta.get('note', ''), 'exec_s': sum(b['exec_s'] for b in meta['beats']),
               'beat_exec_s': [b['exec_s'] for b in meta['beats']], 'clip_s': sum(b['seconds'] for b in meta['beats'])}
        sims = {k: [] for k in refs}
        found = {k: 0 for k in refs}
        last_sims = {}
        sharp, face_sharp, n = [], [], 0
        thumbs, action = [], []
        mags, errs, cams = [], [], []
        for bi in (1, 2):
            m, e, c = motion(os.path.join(d, f'beat_{bi:02d}.mp4'))
            mags += m; errs += e; cams += c
            fr = frames(os.path.join(d, f'beat_{bi:02d}.mp4'))
            for fi, f in enumerate(fr):
                n += 1
                sharp.append(sharpness(cv2.cvtColor(f, cv2.COLOR_BGR2GRAY)))
                best = {k: -1.0 for k in refs}
                for box, sc, kps in det.detect(f, 0.5):
                    if (box[2] - box[0]) < 32:
                        continue
                    e, crop = arc.embed(f, kps)
                    for k in refs:
                        best[k] = max(best[k], float(e @ refs[k]))
                    x1, y1, x2, y2 = [int(v) for v in box]
                    fc = f[max(0, y1):y2, max(0, x1):x2]
                    if fc.size:
                        face_sharp.append(sharpness(cv2.cvtColor(cv2.resize(fc, (128, 128)), cv2.COLOR_BGR2GRAY)))
                for k in refs:
                    if best[k] > 0.25:   # this person is on screen and recognisable
                        found[k] += 1
                        sims[k].append(best[k])
                if bi == 2 and fi == len(fr) - 1:
                    last_sims = {k: round(best[k], 3) for k in refs}
            # Contact sheet: six frames per beat.
            pick = np.linspace(0, len(fr) - 1, 6).astype(int)
            thumbs.append(np.hstack([cv2.resize(fr[i], (320, 180)) for i in pick]))
            # Action sheet: every sampled frame (4/s), for judging the moves.
            row_imgs = [cv2.resize(f, (256, 144)) for f in fr]
            for j in range(0, len(row_imgs), 8):
                chunk = row_imgs[j:j + 8] + [np.zeros((144, 256, 3), np.uint8)] * (8 - len(row_imgs[j:j + 8]))
                action.append(np.hstack(chunk))
        sheet = np.vstack(thumbs)
        cv2.putText(sheet, name, (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2)
        cv2.imwrite(os.path.join(d, 'contact.jpg'), sheet, [cv2.IMWRITE_JPEG_QUALITY, 88])
        act = np.vstack(action)
        cv2.putText(act, name, (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2)
        cv2.imwrite(os.path.join(d, 'action.jpg'), act, [cv2.IMWRITE_JPEG_QUALITY, 85])

        for k in refs:
            row[f'like_{k}'] = round(float(np.mean(sims[k])), 3) if sims[k] else 0.0
            row[f'p10_{k}'] = round(float(np.percentile(sims[k], 10)), 3) if sims[k] else 0.0
            row[f'seen_{k}'] = round(found[k] / max(1, n), 2)
        row['like'] = round(float(np.mean([row[f'like_{k}'] for k in refs])), 3)
        row['last_frame'] = last_sims
        row['sharp'] = round(float(np.median(sharp)), 1)
        row['face_sharp'] = round(float(np.median(face_sharp)), 1) if face_sharp else 0.0
        row['s_per_clip_s'] = round(row['exec_s'] / row['clip_s'], 1)
        row['motion'] = round(float(np.mean(mags)), 2)
        row['motion_p90'] = round(float(np.percentile(mags, 90)), 2)
        row['camera'] = round(float(np.mean(cams)), 2)
        row['warp_err'] = round(float(np.mean(errs)), 2)
        rows.append(row)

    json.dump(rows, open(os.path.join(RESULTS, 'scores.json'), 'w'), indent=1)
    ok = [r for r in rows if 'error' not in r]
    ok.sort(key=lambda r: -r['like'])
    hdr = f"{'config':28} {'like':>5} {'A':>5} {'B':>5} {'p10A':>5} {'p10B':>5} {'seenA':>5} {'seenB':>5} {'lastA':>5} {'lastB':>5} {'sharp':>6} {'fsharp':>6} {'exec':>5} {'s/s':>5} {'move':>5} {'mv90':>5} {'cam':>5} {'warp':>5}"
    print(hdr)
    for r in ok:
        print(f"{r['name']:28} {r['like']:5.2f} {r['like_A']:5.2f} {r['like_B']:5.2f} {r['p10_A']:5.2f} {r['p10_B']:5.2f} "
              f"{r['seen_A']:5.2f} {r['seen_B']:5.2f} {r['last_frame'].get('A', 0):5.2f} {r['last_frame'].get('B', 0):5.2f} "
              f"{r['sharp']:6.0f} {r['face_sharp']:6.0f} {r['exec_s']:5.0f} {r['s_per_clip_s']:5.1f} "
              f"{r['motion']:5.2f} {r['motion_p90']:5.2f} {r['camera']:5.2f} {r['warp_err']:5.2f}")
    for r in rows:
        if 'error' in r:
            print('FAILED', r['name'], r['error'])


if __name__ == '__main__':
    main()
