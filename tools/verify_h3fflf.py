"""Offline check that every graph the 🌀🎯 MiniMax FFLF tab submits would pass ComfyUI validation.

Mirrors MiniMaxFflfViewModel.BuildWorkflow — the same node ids, the same keyframe wiring, the same
reachability prune — then checks the result the way ComfyUI's validate_prompt does: every required
input present, every link pointing at a node that exists and has that output index, and every COMBO
widget holding one of the options the server actually offers.

Usage:  python tools/verify_h3fflf.py [http://10.0.0.10:8188]
"""

import io
import json
import sys
import urllib.request

WORKFLOW = "workflow/video/h3-minimax/h3-minimax-fflf.json"

NODE_OPENING_FRAME = "462"
NODE_BASE_END_FRAME = "463"
NODE_BASE_FL2V = "16:382"
NODE_LOOP_FL2V = "521:515"
NODE_BASE_PROMPT = "3371"
NODE_BASE_SECONDS = "16:12"
NODE_BASE_SEED = "16:483"
NODE_RESOLUTION = "17"
NODE_LOOP_START = "521:523"
NODE_OVERLAP = "4177:4171"
NODE_SAVE_SINGLE = "381"
NODE_SAVE_JOINED = "526"
NODE_CLIP_PROMPTS = ["3372", "3373", "3374"]
NODE_CLIP_SECONDS = ["3377", "3378", "3379"]
NODE_CLIP_END_FRAMES = ["3478", "3477", "3479"]
NODE_CLIP_SEEDS = ["521:3447", "521:3448", "521:3450"]
NODE_LOOP_SEED_SWITCH = "521:3451"
NODE_BASE_DETAIL = "16:4194"
NODE_LOOP_DETAIL = ["521:4209", "521:4214"]
NODE_SPARSE_ATTENTION = "478:473"
NODE_BASE_UPSCALE = "16:3391"
NODE_LOOP_UPSCALE = "521:3393"
NODE_BASE_AUDIO = "16:3442"
NODE_LOOP_AUDIO = "521:3444"

MAX_CLIPS = 4


def build(base, opening, clips, *, detail, rtx, audio, overlap, sparse=False, seed=12345):
    """The C# BuildWorkflow, in Python. `clips` is [{end, prompt, seconds}, …], one per clip."""
    g = json.loads(json.dumps(base))
    extensions = len(clips) - 1
    sink = NODE_SAVE_JOINED if extensions > 0 else NODE_SAVE_SINGLE

    g[NODE_OPENING_FRAME]["inputs"]["image"] = opening
    g[NODE_BASE_END_FRAME]["inputs"]["image"] = clips[0]["end"]
    g[NODE_BASE_PROMPT]["inputs"]["value"] = clips[0]["prompt"]
    g[NODE_BASE_SECONDS]["inputs"]["value"] = clips[0]["seconds"]
    g[NODE_BASE_SEED]["inputs"]["noise_seed"] = seed

    for i in range(MAX_CLIPS - 1):
        source = max(0, min(i + 1, len(clips) - 1))
        g[NODE_CLIP_END_FRAMES[i]]["inputs"]["image"] = clips[source]["end"]
        g[NODE_CLIP_PROMPTS[i]]["inputs"]["value"] = clips[source]["prompt"]
        g[NODE_CLIP_SECONDS[i]]["inputs"]["value"] = clips[source]["seconds"]
        g[NODE_CLIP_SEEDS[i]]["inputs"]["noise_seed"] = seed + i + 1
    g[NODE_LOOP_SEED_SWITCH]["inputs"]["switch"] = True
    g[NODE_LOOP_START]["inputs"]["total"] = max(1, extensions)
    g[NODE_OVERLAP]["inputs"]["choice"] = str(overlap)

    g[NODE_RESOLUTION]["inputs"]["aspect_ratio"] = "16:9 (Widescreen)"
    g[NODE_RESOLUTION]["inputs"]["megapixels"] = 0.7
    g[NODE_RESOLUTION]["inputs"]["multiple"] = 64

    g[NODE_SPARSE_ATTENTION]["inputs"]["switch"] = sparse
    g[NODE_BASE_DETAIL]["inputs"]["switch"] = detail
    for nid in NODE_LOOP_DETAIL:
        g[nid]["inputs"]["switch"] = detail
    g[NODE_BASE_UPSCALE]["inputs"]["switch"] = (extensions == 0) and rtx
    g[NODE_LOOP_UPSCALE]["inputs"]["switch"] = rtx
    g[NODE_BASE_AUDIO]["inputs"]["switch"] = (extensions == 0) and audio
    g[NODE_LOOP_AUDIO]["inputs"]["switch"] = audio

    g[sink]["inputs"]["filename_prefix"] = "minimax_fflf/mmfflf_test"
    g[sink]["inputs"]["save_output"] = True

    return prune(g, [sink]), sink


def prune(g, keep):
    reachable, stack = set(), list(keep)
    while stack:
        nid = stack.pop()
        if nid in reachable:
            continue
        reachable.add(nid)
        for v in (g.get(nid, {}).get("inputs") or {}).values():
            if isinstance(v, list) and len(v) == 2:
                stack.append(str(v[0]))
    return {k: v for k, v in g.items() if k in reachable}


def combo_choices(decl):
    """The strings a COMBO accepts, whether it is a plain list, a V3 combo, or a dynamic combo."""
    if isinstance(decl[0], list):
        return decl[0]
    opts = decl[1] if len(decl) > 1 and isinstance(decl[1], dict) else {}
    options = opts.get("options")
    if not isinstance(options, list):
        return None
    if options and isinstance(options[0], dict):
        return [o.get("key") for o in options]
    return options


def validate(g, oi):
    """What ComfyUI rejects (errors) versus what it silently ignores (notes)."""
    errors, notes = [], []
    for nid, node in g.items():
        ct = node.get("class_type")
        if ct not in oi:
            errors.append(f"{nid}: unknown class_type {ct}")
            continue
        spec = oi[ct]["input"]
        required = spec.get("required", {})
        optional = spec.get("optional", {})
        inputs = node.get("inputs") or {}

        for name, decl in required.items():
            if name in inputs or any(k.startswith(f"{name}.") for k in inputs):
                continue
            opts = decl[1] if len(decl) > 1 and isinstance(decl[1], dict) else {}
            if "default" in opts or decl[0] == "COMFY_AUTOGROW_V3":
                continue
            errors.append(f"{nid} ({ct}): missing required input '{name}'")

        for name, value in inputs.items():
            root = name.split(".")[0]
            decl = required.get(root) or optional.get(root)
            if decl is None:
                # VHS format extras and leftover frontend widget state; ComfyUI drops them.
                notes.append(f"{nid} ({ct}): extra input '{name}' (ignored by the server)")
                continue

            if isinstance(value, list) and len(value) == 2:
                src, idx = str(value[0]), value[1]
                if src not in g:
                    errors.append(f"{nid} ({ct}).{name}: link to missing node {src}")
                elif not isinstance(idx, int):
                    errors.append(f"{nid} ({ct}).{name}: non-integer output index {idx!r}")
                else:
                    outs = oi.get(g[src].get("class_type"), {}).get("output", [])
                    if idx >= len(outs):
                        errors.append(f"{nid} ({ct}).{name}: {g[src]['class_type']} has no output {idx}")
                continue

            if root != name:
                continue

            choices = combo_choices(decl)
            # An empty options list means the node fills the combo in at runtime.
            if choices and isinstance(value, str) and value not in choices:
                errors.append(f"{nid} ({ct}).{name}: {value!r} is not one of {len(choices)} options")

            opts = decl[1] if len(decl) > 1 and isinstance(decl[1], dict) else {}
            if isinstance(value, (int, float)) and not isinstance(value, bool):
                lo, hi = opts.get("min"), opts.get("max")
                if lo is not None and value < lo:
                    errors.append(f"{nid} ({ct}).{name}: {value} below min {lo}")
                if hi is not None and value > hi:
                    errors.append(f"{nid} ({ct}).{name}: {value} above max {hi}")
    return errors, notes


def check_chain(g, sink, clips, oi):
    """Beyond validation: the wiring this tab exists for actually reached the graph."""
    problems = []
    base = g.get(NODE_BASE_FL2V, {}).get("inputs", {})
    if base.get("first_frame") != [NODE_OPENING_FRAME, 0]:
        problems.append("base pass is not conditioned on the opening frame")
    if base.get("last_frame") != [NODE_BASE_END_FRAME, 0]:
        problems.append("base pass is not conditioned on clip 1's end keyframe")

    if len(clips) > 1:
        loop = g.get(NODE_LOOP_FL2V, {}).get("inputs", {})
        if not isinstance(loop.get("last_frame"), list):
            problems.append("the continuation loop has no end keyframe")
        if g.get(NODE_LOOP_START, {}).get("inputs", {}).get("total") != len(clips) - 1:
            problems.append("loop total does not match the number of continuing clips")
        if NODE_SAVE_SINGLE in g:
            problems.append("the base-only sink survived the prune on a continued take")
    else:
        if NODE_LOOP_START in g:
            problems.append("the continuation loop survived the prune on a single-clip take")
        if NODE_SAVE_JOINED in g:
            problems.append("the joined sink survived the prune on a single-clip take")
    return problems


def main():
    server = sys.argv[1] if len(sys.argv) > 1 else "http://10.0.0.10:8188"
    with urllib.request.urlopen(f"{server}/object_info", timeout=180) as r:
        oi = json.loads(r.read().decode("utf-8"))
    base = json.load(io.open(WORKFLOW, encoding="utf-8"))

    # Use image names the server really has, so LoadImage's combo check means something.
    pool = oi["LoadImage"]["input"]["required"]["image"][0]
    pool = pool if isinstance(pool, list) else oi["LoadImage"]["input"]["required"]["image"][1]["options"]
    a, b, c, d, e = (pool * 5)[:5]

    clip = lambda end, n, s: {"end": end, "prompt": f"Clip {n} FL2VA prompt.", "seconds": s}
    cases = [
        ("2 keyframes, one clip, defaults", a, [clip(b, 1, 10)],
         dict(detail=True, rtx=False, audio=True, overlap=22)),
        ("2 keyframes, one clip, everything off", a, [clip(b, 1, 5)],
         dict(detail=False, rtx=False, audio=False, overlap=5)),
        ("2 keyframes, one clip, RTX + Sol-Attn", a, [clip(b, 1, 15)],
         dict(detail=True, rtx=True, audio=True, overlap=22, sparse=True)),
        ("3 keyframes, two clips", a, [clip(b, 1, 10), clip(c, 2, 10)],
         dict(detail=True, rtx=False, audio=True, overlap=22)),
        ("4 keyframes, three clips", a, [clip(b, 1, 8), clip(c, 2, 8), clip(d, 3, 8)],
         dict(detail=True, rtx=True, audio=True, overlap=39)),
        ("5 keyframes, four clips, no detail pass", a,
         [clip(b, 1, 5), clip(c, 2, 12), clip(d, 3, 15), clip(e, 4, 10)],
         dict(detail=False, rtx=False, audio=False, overlap=56)),
    ]

    failed = 0
    for label, opening, clips, opts in cases:
        g, sink = build(base, opening, clips, **opts)
        errors, notes = validate(g, oi)
        errors += [f"chain: {p}" for p in check_chain(g, sink, clips, oi)]
        status = "OK " if not errors else "FAIL"
        print(f"[{status}] {label}: {len(g)} nodes, sink {sink}, {len(notes)} ignored extras")
        for err in errors:
            print(f"         {err}")
        failed += bool(errors)

    print("\nall graphs valid" if not failed else f"\n{failed} case(s) failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
