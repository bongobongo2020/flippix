"""Time the 🌀 MiniMax I2V graph with one setting changed at a time.

Reuses tools/verify_h3i2v.build so the graphs are exactly what the tab submits, holds the image,
prompt and seed fixed, and reports wall-clock from ComfyUI's own /history timestamps.

Usage:  python tools/bench_h3i2v.py <image-name-on-server> [server]
"""

import io
import json
import sys
import time
import urllib.request

sys.path.insert(0, "tools")
from verify_h3i2v import build, WORKFLOW  # noqa: E402

SERVER = "http://10.0.0.10:8188"
DEFAULTS = dict(detail=True, rtx=False, audio=True, max_fidelity=False, overlap=22)


def submit(server, graph):
    req = urllib.request.Request(
        f"{server}/prompt",
        data=json.dumps({"prompt": graph, "client_id": "flippix-bench"}).encode(),
        headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.loads(r.read().decode())["prompt_id"], None
    except urllib.error.HTTPError as e:
        return None, e.read().decode()[:1500]


def wait(server, pid, timeout=3600):
    deadline = time.time() + timeout
    while time.time() < deadline:
        with urllib.request.urlopen(f"{server}/history/{pid}", timeout=30) as r:
            h = json.loads(r.read().decode())
        if pid in h:
            e = h[pid]
            msgs = {m[0]: m[1] for m in e["status"]["messages"]}
            st = msgs.get("execution_start", {}).get("timestamp")
            en = (msgs.get("execution_success", {}) or msgs.get("execution_error", {})).get("timestamp")
            return e["status"]["status_str"], (en - st) / 1000 if st and en else None, e
        time.sleep(5)
    return "timeout", None, None


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    image = sys.argv[1]
    server = sys.argv[2] if len(sys.argv) > 2 else SERVER
    base = json.load(io.open(WORKFLOW, encoding="utf-8"))
    prompt = base["56"]["inputs"]["value"]

    cases = [
        ("10s, dense (SLA off)",   [image], [], dict(DEFAULTS, sla=False)),
        ("10s, SLA 0.85",          [image], [], dict(DEFAULTS, sla=True, sla_sparsity=0.85)),
        ("10s, SLA 0.90",          [image], [], dict(DEFAULTS, sla=True, sla_sparsity=0.90)),
    ]

    results = []
    for label, refs, conts, opts in cases:
        seconds = 5 if conts else 10
        g, sink = build(base, refs, conts, **opts)
        g["56"]["inputs"]["value"] = prompt          # the real prompt, not the placeholder
        g["4145:147"]["inputs"]["value"] = seconds
        g["4145:149"]["inputs"]["noise_seed"] = 777  # same seed everywhere
        g[sink]["inputs"]["filename_prefix"] = f"minimax_i2v/bench_{len(results)}"

        print(f"\n▶ {label} ({len(g)} nodes, sink {sink})", flush=True)
        pid, err = submit(server, g)
        if err:
            print(f"  REJECTED: {err}", flush=True)
            results.append((label, None, "rejected"))
            continue
        status, dur, entry = wait(server, pid)
        out = ""
        if entry:
            files = [f.get("filename") for o in entry.get("outputs", {}).values()
                     for k in ("gifs", "videos", "images") for f in o.get(k, [])]
            out = files[0] if files else ""
        print(f"  {status} in {dur:.1f}s  {out}", flush=True)
        results.append((label, dur, status))

    print("\n── results ──")
    # Only successful runs are comparable: a run that raised part-way still reports a duration,
    # and reading that as a speedup is how a crash gets mistaken for a win.
    baseline = next((d for _, d, st in results if st == "success"), None)
    for label, dur, status in results:
        if dur is None or status != "success":
            note = f"{dur:.1f}s before failing" if dur else ""
            print(f"  {label:32} {status.upper():9} {note}")
            continue
        rel = ""
        if baseline and dur != baseline:
            rel = (f"  ({baseline / dur:.2f}x faster)" if dur < baseline
                   else f"  ({dur / baseline:.2f}x slower)")
        print(f"  {label:32} {dur:7.1f}s{rel}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
