"""Re-run the exact configuration that failed with a token-count mismatch.

15s base + a 15s continuation at 21:9 / 0.4 MP with the detail pass on. Before the ResolutionSelector
was moved to 64-multiples the base was 960x416, and 416 * 1.5 = 624 = 19.5 blocks of 32 — the
conditioning latent and the 3D latent upscaler rounded that in opposite directions and the
continuation loop's detail sampler (4146:4240) died on a 6440-vs-6118 token mismatch.

Waits for the queue to drain first, so it never jumps ahead of a real run.

Usage:  python tools/test_h3i2v_detailloop.py <image-name-on-server> [server]
"""

import io
import json
import sys
import time
import urllib.request

sys.path.insert(0, "tools")
from verify_h3i2v import build, WORKFLOW  # noqa: E402

SERVER = "http://10.0.0.10:8188"


def get(server, path, timeout=30):
    with urllib.request.urlopen(f"{server}{path}", timeout=timeout) as r:
        return json.loads(r.read().decode())


def wait_for_idle(server):
    while True:
        q = get(server, "/queue")
        busy = len(q.get("queue_running", [])) + len(q.get("queue_pending", []))
        if not busy:
            return
        print(f"  queue busy ({busy}), waiting…", flush=True)
        time.sleep(20)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    image = sys.argv[1]
    server = sys.argv[2] if len(sys.argv) > 2 else SERVER

    base = json.load(io.open(WORKFLOW, encoding="utf-8"))
    print("waiting for the GPU to be free…", flush=True)
    wait_for_idle(server)

    g, sink = build(
        base, [image],
        [{"prompt": "[Continuation 1] The take continues without a cut.", "seconds": 15}],
        detail=True, rtx=False, audio=True, max_fidelity=False, overlap=22,
        sla=True, sla_sparsity=0.85)
    g["56"]["inputs"]["value"] = base["56"]["inputs"]["value"]
    g["4145:147"]["inputs"]["value"] = 15
    g["60"]["inputs"]["aspect_ratio"] = "21:9 (Ultrawide)"
    g["60"]["inputs"]["megapixels"] = 0.4
    g["4145:149"]["inputs"]["noise_seed"] = 777
    g[sink]["inputs"]["filename_prefix"] = "minimax_i2v/detailloop_fix"

    print(f"multiple = {g['60']['inputs']['multiple']} | 15s + 15s | 21:9 | 0.4 MP | "
          f"detail pass on | {len(g)} nodes | sink {sink}", flush=True)

    req = urllib.request.Request(
        f"{server}/prompt",
        data=json.dumps({"prompt": g, "client_id": "flippix-detailloop"}).encode(),
        headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            pid = json.loads(r.read().decode())["prompt_id"]
    except urllib.error.HTTPError as e:
        print("REJECTED at validation:\n" + e.read().decode()[:2000])
        return 1
    print(f"submitted {pid}", flush=True)

    deadline = time.time() + 5400
    while time.time() < deadline:
        h = get(server, f"/history/{pid}")
        if pid in h:
            e = h[pid]
            msgs = {m[0]: m[1] for m in e["status"]["messages"]}
            t0 = msgs.get("execution_start", {}).get("timestamp")
            t1 = (msgs.get("execution_success") or msgs.get("execution_error") or {}).get("timestamp")
            dur = (t1 - t0) / 1000 if t0 and t1 else None
            status = e["status"]["status_str"]
            print(f"\n{status.upper()} in {dur:.1f}s" if dur else f"\n{status.upper()}")
            if status == "error":
                err = msgs.get("execution_error", {})
                print("  node   :", err.get("node_id"), err.get("node_type"))
                print("  message:", (err.get("exception_message") or "")[:800])
                return 1
            files = [f.get("filename") for o in e.get("outputs", {}).values()
                     for k in ("gifs", "videos", "images") for f in o.get(k, [])]
            print("  output :", files[0] if files else "(none)")
            return 0
        time.sleep(10)
    print("timed out")
    return 1


if __name__ == "__main__":
    sys.exit(main())
