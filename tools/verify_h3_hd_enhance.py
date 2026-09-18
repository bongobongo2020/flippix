#!/usr/bin/env python3
"""
Submits workflow/video/h3-minimax/h3-hd-detailer.json to the live ComfyUI and reports whether the
Enhance HD tab's graph actually renders a file.

The patching here mirrors VideoEnhanceViewModel.BuildH3HdWorkflowAsync: the ResolutionSelector is
replaced by two literals computed from the source aspect, and the authored reference loaders are
rebuilt one per reference.

With --total-frames it also mirrors the tab's segmenting. H3 samples a clip as one sequence with no
context window, so past a certain length the run is a hard OOM rather than a slow render; the tab
cuts the clip into evenly spread chunks of a length H3 accepts (5 + 17k), renders each on its own
with skip_first_frames, then trims the overlaps and concatenates. This runs the same plan, the same
per-segment submits and the same ffmpeg join, which is the only way to check the frame arithmetic
and the seams without driving the WPF app.

The clip and the reference images have to already be in the server's input folder.

Run:  python tools/verify_h3_hd_enhance.py --video MiniMaxFFLF_20260822_094732.mp4 \
          --ref eroktales_test_019_by_igbattles_dmcgtaj-414w-2x.jpg

      python tools/verify_h3_hd_enhance.py --video long.mp4 --ref face.png \
          --source-size 1344x768 --total-frames 100 --chunk 39 --out joined.mp4
"""
import argparse
import json
import math
import os
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORKFLOW = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-hd-detailer.json")
SERVER = "http://10.0.0.10:8188"

RESOLUTION_NODE = "707"
AUTHORED_REFERENCE_NODES = ("683", "682")


def post(url, payload):
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=data,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read().decode("utf-8"))


def get(url):
    with urllib.request.urlopen(url, timeout=120) as r:
        return json.loads(r.read().decode("utf-8"))


def plan_canvas(width, height, megapixels):
    """Source aspect at the requested megapixels, both sides on a multiple of 32."""
    aspect = width / height
    h = math.sqrt(megapixels * 1048576 / aspect)
    w = h * aspect
    align = lambda v: max(32, int(round(v / 32.0)) * 32)
    return align(w), align(h)


def retarget(wf, source_id, slot, new_id):
    """Repoint every link reading (source_id, slot) at slot 0 of new_id."""
    for node in wf.values():
        for name, val in list(node["inputs"].items()):
            if isinstance(val, list) and len(val) == 2 and val[0] == source_id and val[1] == slot:
                node["inputs"][name] = [new_id, 0]


def attach_references(wf, names):
    for nid in AUTHORED_REFERENCE_NODES:
        wf.pop(nid, None)
    inputs = wf["609"]["inputs"]
    for key in [k for k in inputs if k.startswith("ref_images.ref_image_")]:
        del inputs[key]
    for i, name in enumerate(names):
        nid = "h3hd_ref_%d" % i
        wf[nid] = {
            "inputs": {"image": name, "resize": True, "width": 1344, "height": 1344,
                       "repeat": 1, "keep_proportion": True, "divisible_by": 32,
                       "mask_channel": "alpha", "background_color": ""},
            "class_type": "LoadAndResizeImage",
            "_meta": {"title": "H3 REF IMAGE %d" % (i + 1)},
        }
        inputs["ref_images.ref_image_%d" % i] = [nid, 0]


def valid_h3_length(frames):
    """Largest 5 + 17k not longer than `frames` - the lengths H3 accepts."""
    return 5 if frames < 22 else 5 + (frames - 5) // 17 * 17


def plan_segments(total_frames, chunk_frames):
    """Evenly spread full-length chunks; returns (start, frames, overlap_frames)."""
    if total_frames <= chunk_frames:
        return [(0, valid_h3_length(total_frames), 0)]
    count = math.ceil(total_frames / chunk_frames)
    stride = (total_frames - chunk_frames) / (count - 1)
    segments, previous = [], 0
    for i in range(count):
        start = int(round(i * stride))
        segments.append((start, chunk_frames, 0 if i == 0 else chunk_frames - (start - previous)))
        previous = start
    return segments


def download(server, item, dest):
    q = urllib.parse.urlencode({"filename": item["filename"],
                                "subfolder": item.get("subfolder", ""),
                                "type": item.get("type", "output")})
    with urllib.request.urlopen("%s/view?%s" % (server, q), timeout=600) as r:
        with open(dest, "wb") as f:
            shutil.copyfileobj(r, f)
    return dest


def run_ffmpeg(args):
    p = subprocess.run(["ffmpeg", *args], capture_output=True, text=True)
    if p.returncode != 0:
        raise SystemExit("ffmpeg failed: " + p.stderr[-800:])


ENCODE = ["-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
          "-c:a", "aac", "-b:a", "192k", "-pix_fmt", "yuv420p"]


def join_segments(paths, segments, out_path):
    """The tab's join: trim each overlapping segment's head, then concat-demux the lot."""
    tmp = tempfile.mkdtemp(prefix="h3hd_join_")
    parts = []
    for path, (_, _, overlap) in zip(paths, segments):
        if overlap <= 0:
            parts.append(path)
            continue
        trimmed = os.path.join(tmp, "trim_%d.mp4" % len(parts))
        # The sink always writes 24 fps, so the overlap converts straight to seconds.
        run_ffmpeg(["-y", "-ss", "%.3f" % (overlap / 24.0), "-i", path] + ENCODE + [trimmed])
        parts.append(trimmed)

    list_path = os.path.join(tmp, "list.txt")
    with open(list_path, "w", encoding="utf-8") as f:
        for part in parts:
            # The concat demuxer reads a backslash as an escape and a quote as the delimiter.
            safe = part.replace("\\", "/").replace("'", "'\\''")
            f.write("file '" + safe + "'\n")
    run_ffmpeg(["-y", "-f", "concat", "-safe", "0", "-i", list_path] + ENCODE + [out_path])
    return out_path


def build(wf_template, args, base, segment, prompt):
    """One segment's API graph."""
    wf = json.loads(wf_template)
    start, frames, _ = segment

    wf["657"]["inputs"]["video"] = args.video
    wf["657"]["inputs"]["skip_first_frames"] = start
    wf["657"]["inputs"]["frame_load_cap"] = frames

    wf["h3hd_canvas_w"] = {"inputs": {"value": base[0]}, "class_type": "PrimitiveInt",
                           "_meta": {"title": "Enhance HD canvas width"}}
    wf["h3hd_canvas_h"] = {"inputs": {"value": base[1]}, "class_type": "PrimitiveInt",
                           "_meta": {"title": "Enhance HD canvas height"}}
    retarget(wf, RESOLUTION_NODE, 0, "h3hd_canvas_w")
    retarget(wf, RESOLUTION_NODE, 1, "h3hd_canvas_h")
    del wf[RESOLUTION_NODE]

    wf["772"]["inputs"]["value"] = args.detail_mp
    wf["641"]["inputs"]["value"] = prompt
    wf["669"]["inputs"]["steps"] = args.steps
    wf["669"]["inputs"]["denoise"] = args.denoise
    # One seed for the whole job: at partial denoise it picks the noise mixed into the source
    # latent, so holding it fixed keeps consecutive segments from drifting apart across a cut.
    wf["713"]["inputs"]["seed"] = args.seed
    wf["389"]["inputs"]["filename_prefix"] = "H3HDEnhance/verify_s%02d" % segment[0]
    attach_references(wf, args.ref)
    return wf


def submit_and_wait(server, wf, timeout):
    """Returns the output file descriptors, or raises SystemExit with what went wrong."""
    try:
        res = post("%s/prompt" % server, {"prompt": wf, "client_id": "flippix-verify"})
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        print("SUBMIT REJECTED", e.code)
        try:
            print(json.dumps(json.loads(body), indent=2, ensure_ascii=False)[:6000])
        except ValueError:
            print(body[:6000])
        raise SystemExit(1)

    pid = res["prompt_id"]
    print("  submitted", pid, "node_errors:", res.get("node_errors"))

    deadline = time.time() + timeout
    while time.time() < deadline:
        time.sleep(10)
        hist = get("%s/history/%s" % (server, pid))
        if pid not in hist:
            continue
        entry = hist[pid]
        status = entry.get("status", {})
        if status.get("status_str") == "error":
            for m in status.get("messages", []):
                print("   ", json.dumps(m)[:3000])
            raise SystemExit(1)
        if status.get("completed"):
            files = []
            for nid, out in entry.get("outputs", {}).items():
                for key in ("gifs", "videos", "images", "audio", "files"):
                    for f in out.get(key, []):
                        if f.get("filename", "").endswith(".mp4"):
                            files.append(f)
            return files
    raise SystemExit("TIMED OUT")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--video", default="MiniMaxFFLF_20260822_094732.mp4")
    ap.add_argument("--ref", action="append", default=[],
                    help="reference image already in the server's input folder; repeatable")
    ap.add_argument("--source-size", default="576x320",
                    help="WxH of the clip, used to keep the canvas on its aspect")
    ap.add_argument("--base-mp", type=float, default=0.4)
    ap.add_argument("--detail-mp", type=float, default=0.8)
    ap.add_argument("--frames", type=int, default=39,
                    help="single-pass frame_load_cap, when --total-frames is not given")
    ap.add_argument("--total-frames", type=int, default=0,
                    help="frames of the clip to cover; turns on the tab's segment-and-rejoin path")
    ap.add_argument("--chunk", type=int, default=39, help="frames per segment; must be 5 + 17k")
    ap.add_argument("--out", default=None, help="where to write the rejoined mp4")
    ap.add_argument("--denoise", type=float, default=0.45)
    ap.add_argument("--steps", type=int, default=4)
    ap.add_argument("--seed", type=int, default=987713038727449)
    ap.add_argument("--prompt", default="r34l1sm\nthe woman walks toward the camera in soft lateral light")
    ap.add_argument("--server", default=SERVER)
    ap.add_argument("--timeout", type=int, default=3600)
    args = ap.parse_args()

    with open(WORKFLOW, encoding="utf-8") as f:
        wf_template = f.read()

    sw, sh = (int(v) for v in args.source_size.lower().split("x"))
    base = plan_canvas(sw, sh, args.base_mp)
    final = plan_canvas(sw, sh, args.detail_mp)

    if args.total_frames > 0:
        segments = plan_segments(args.total_frames, args.chunk)
    else:
        segments = [(0, args.frames, 0)]

    print("canvas %dx%d -> %dx%d, %d refs" % (base[0], base[1], final[0], final[1], len(args.ref)))
    print("plan: %d segment(s) %s" % (len(segments), segments))

    tmp = tempfile.mkdtemp(prefix="h3hd_seg_")
    rendered = []
    for i, segment in enumerate(segments):
        print("segment %d/%d: frames %d-%d (overlap %d)"
              % (i + 1, len(segments), segment[0], segment[0] + segment[1] - 1, segment[2]))
        files = submit_and_wait(args.server, build(wf_template, args, base, segment, args.prompt),
                                args.timeout)
        if not files:
            raise SystemExit("segment %d produced no mp4" % (i + 1))
        # VHS writes both a silent and an -audio mp4; the one with the soundtrack is the output.
        pick = next((f for f in files if "-audio" in f["filename"]), files[0])
        print("  OUTPUT %s/%s" % (pick.get("subfolder"), pick["filename"]))
        rendered.append(download(args.server, pick, os.path.join(tmp, "seg_%02d.mp4" % i)))

    if len(rendered) == 1 and not args.out:
        return 0

    out_path = args.out or os.path.join(tmp, "joined.mp4")
    join_segments(rendered, segments, out_path)
    print("JOINED ->", out_path)
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-count_frames",
         "-show_entries", "stream=width,height,nb_read_frames", "-of", "csv=p=0", out_path],
        capture_output=True, text=True)
    print("joined video:", probe.stdout.strip(), "(expected %d frames)" % args.total_frames)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
