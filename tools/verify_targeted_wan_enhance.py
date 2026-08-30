#!/usr/bin/env python3
"""
Submits workflow/video/wan/targeted-wan-enhance.json to the live ComfyUI and reports whether the
targeted enhance actually renders a file.

The graph is the expensive kind - SAM3 tracks every frame, then three WanVideo passes sample it -
so this patches the canvases down to something that finishes in a few minutes and points the
loader at a short clip already sitting in the server's input folder.

Run:  python tools/verify_targeted_wan_enhance.py --video flippix_enhance_test.mp4
"""
import argparse
import json
import os
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORKFLOW = os.path.join(ROOT, "workflow", "video", "wan", "targeted-wan-enhance.json")
SERVER = "http://10.0.0.10:8188"


def post(url, payload):
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=data,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read().decode("utf-8"))


def get(url):
    with urllib.request.urlopen(url, timeout=120) as r:
        return json.loads(r.read().decode("utf-8"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--video", default="flippix_enhance_test.mp4")
    ap.add_argument("--server", default=SERVER)
    ap.add_argument("--targets", default="woman")
    ap.add_argument("--timeout", type=int, default=3600)
    args = ap.parse_args()

    with open(WORKFLOW, encoding="utf-8") as f:
        wf = json.load(f)

    wf["168"]["inputs"]["video"] = args.video
    wf["217"]["inputs"]["text_prompt"] = args.targets
    # Small canvases: this is a smoke test, not a render.
    wf["174"]["inputs"]["value"] = 240
    wf["175"]["inputs"]["value"] = 400
    wf["239"]["inputs"]["width"], wf["239"]["inputs"]["height"] = 288, 480
    wf["243"]["inputs"]["width"], wf["243"]["inputs"]["height"] = 336, 560
    wf["182"]["inputs"]["string"] = "Ultra high detail video, 8K, UHD, ultra realistic."
    wf["276"]["inputs"]["filename_prefix"] = "TargetedEnhance/verify"

    try:
        res = post(f"{args.server}/prompt", {"prompt": wf, "client_id": "flippix-verify"})
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        print("SUBMIT REJECTED", e.code)
        try:
            err = json.loads(body)
            print(json.dumps(err, indent=2, ensure_ascii=False)[:6000])
        except ValueError:
            print(body[:6000])
        return 1

    pid = res["prompt_id"]
    print("submitted", pid, "node_errors:", res.get("node_errors"))

    deadline = time.time() + args.timeout
    while time.time() < deadline:
        time.sleep(10)
        hist = get(f"{args.server}/history/{pid}")
        if pid not in hist:
            continue
        entry = hist[pid]
        status = entry.get("status", {})
        print("status:", status.get("status_str"), "completed:", status.get("completed"))
        if status.get("status_str") == "error":
            for m in status.get("messages", []):
                print("  ", json.dumps(m)[:2000])
            return 1
        if status.get("completed"):
            for nid, out in entry.get("outputs", {}).items():
                for key in ("gifs", "videos", "images", "audio", "files"):
                    for f in out.get(key, []):
                        print(f"  OUTPUT {nid} {key}: {f.get('subfolder')}/{f.get('filename')}")
            return 0
    print("TIMED OUT")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
