"""Offline check that every graph the 🌀 MiniMax I2V tab submits would pass ComfyUI validation.

Mirrors MiniMaxI2VViewModel.BuildWorkflow — the same node ids, the same reference wiring, the
same reachability prune — then checks the result the way ComfyUI's validate_prompt does: every
required input present, every link pointing at a node that exists and has that output index, and
every COMBO widget holding one of the options the server actually offers.

Usage:  python tools/verify_h3i2v.py [http://10.0.0.10:8188]
"""

import io
import json
import math
import sys
import urllib.request

WORKFLOW = "workflow/video/h3-minimax/h3-minimax-i2v.json"

NODE_REFERENCE_0 = "10"
NODE_BASE_REF2V = "4145:174"
NODE_LOOP_REF2V = "4146:175"
NODE_BASE_PROMPT = "56"
NODE_BASE_SECONDS = "4145:147"
NODE_BASE_SEED = "4145:149"
NODE_RESOLUTION = "60"
NODE_LOOP_START = "4146:126"
NODE_OVERLAP = "4174:4171"
NODE_SAVE_SINGLE = "49"
NODE_SAVE_JOINED = "52"
NODE_BASE_DETAIL = "4145:4220"
NODE_LOOP_DETAIL = ["4146:4241", "4146:4321"]
NODE_SLA = ["sla_base", "sla_loop"]
NODE_SPARSE_ATTENTION = "55:3706"
NODE_BASE_UPSCALE = "4145:139"
NODE_LOOP_UPSCALE = "4146:70"
NODE_BASE_AUDIO = "4145:143"
NODE_LOOP_AUDIO = "4146:73"
NODE_CONT_PROMPTS = ["57", "58", "46"]
NODE_CONT_SECONDS = ["7", "8", "6"]
NODE_CONT_SEEDS = ["4146:97", "4146:96", "4146:94"]
NODE_CANVAS_WIDTH = "i2v_canvas_w"
NODE_CANVAS_HEIGHT = "i2v_canvas_h"

RESOLUTION_MULTIPLE = 32
LATENT_UPSCALE_FACTOR = 2

# The aspects H3Canvas offers, and the integer pairs ResolutionSelector divides the megapixel budget
# by. 2:1 is not one of the node's own options - see resolve_canvas.
RATIO_PAIRS = {
    "1:1 (Square)": (1, 1),
    "2:3 (Portrait Photo)": (2, 3),
    "3:2 (Photo)": (3, 2),
    "3:4 (Portrait Standard)": (3, 4),
    "4:3 (Standard)": (4, 3),
    "9:16 (Portrait Widescreen)": (9, 16),
    "16:9 (Widescreen)": (16, 9),
    "21:9 (Ultrawide)": (21, 9),
    "2:1 (Stereo SBS)": (2, 1),
    "1:2 (Stereo Over-Under)": (1, 2),
}
STEREO_ASPECT = "2:1 (Stereo SBS)"
STEREO_OU_ASPECT = "1:2 (Stereo Over-Under)"
LITERAL_ASPECTS = (STEREO_ASPECT, STEREO_OU_ASPECT)


def resolve_canvas(aspect, megapixels, multiple=RESOLUTION_MULTIPLE):
    """H3Canvas.Resolve, in Python - the sampled canvas, not the finished one."""
    w_ratio, h_ratio = RATIO_PAIRS.get(aspect, (16, 9))
    scale = math.sqrt(megapixels * 1024 * 1024 / (w_ratio * h_ratio))
    if aspect in LITERAL_ASPECTS:
        # Exact proportion, both sides on the multiple - the eye panels have to be square.
        unit = max(multiple, round(scale / multiple) * multiple)
        return w_ratio * unit, h_ratio * unit
    return (max(multiple, round(w_ratio * scale / multiple) * multiple),
            max(multiple, round(h_ratio * scale / multiple) * multiple))


def retarget(g, source_id, slot, new_id):
    """Repoints every link reading one output of a node at slot 0 of another."""
    for node in g.values():
        inputs = node.get("inputs")
        if not isinstance(inputs, dict):
            continue
        for name, value in list(inputs.items()):
            if (isinstance(value, list) and len(value) == 2
                    and str(value[0]) == source_id and value[1] == slot):
                inputs[name] = [new_id, 0]


def build(base, references, continuations, *, detail, rtx, audio, max_fidelity,
          overlap, sparse=False, sla=True, sla_sparsity=0.85,
          aspect="16:9 (Widescreen)", megapixels=0.7):
    """The C# BuildWorkflow, in Python."""
    g = json.loads(json.dumps(base))
    extending = len(continuations) > 0
    sink = NODE_SAVE_JOINED if extending else NODE_SAVE_SINGLE

    g[NODE_REFERENCE_0]["inputs"]["image"] = references[0]
    loaders = [NODE_REFERENCE_0]
    for i, name in enumerate(references[1:], start=1):
        nid = f"i2v_ref_{i}"
        g[nid] = {"inputs": {"image": name}, "class_type": "LoadImage",
                  "_meta": {"title": f"Picture {i + 1}"}}
        loaders.append(nid)

    for node in (NODE_BASE_REF2V, NODE_LOOP_REF2V):
        inputs = g[node]["inputs"]
        for key in [k for k in inputs if k.startswith("ref_images.ref_image_")]:
            del inputs[key]
        for i, loader in enumerate(loaders):
            inputs[f"ref_images.ref_image_{i}"] = [loader, 0]
        inputs["ref_image_size"] = "max" if max_fidelity else "match"

    g[NODE_BASE_PROMPT]["inputs"]["value"] = "Ref2VA:\n\nsummary:\nA test."
    g[NODE_BASE_SECONDS]["inputs"]["value"] = 10
    g[NODE_BASE_SEED]["inputs"]["noise_seed"] = 12345

    for i in range(3):
        seg = continuations[i] if i < len(continuations) else (continuations[-1] if continuations else None)
        g[NODE_CONT_PROMPTS[i]]["inputs"]["value"] = (seg or {}).get("prompt", "fallback")
        g[NODE_CONT_SECONDS[i]]["inputs"]["value"] = (seg or {}).get("seconds", 10)
        g[NODE_CONT_SEEDS[i]]["inputs"]["noise_seed"] = 12345 + i + 1
    g[NODE_LOOP_START]["inputs"]["total"] = max(1, len(continuations))
    g[NODE_OVERLAP]["inputs"]["choice"] = str(overlap)

    # The sampled canvas: with the detail pass on, ResolutionSelector is handed a quarter of the
    # megapixel target and the upscaler doubles its answer.
    sampled_mp = megapixels / (LATENT_UPSCALE_FACTOR ** 2) if detail else megapixels
    if aspect in LITERAL_ASPECTS:
        # ResolutionSelector's aspect_ratio is a COMBO of eight fixed strings, and neither stereo
        # canvas is among them, so it is resolved here and fed in as two PrimitiveInt nodes standing
        # where its outputs were. The orphaned selector falls out in the prune.
        cw, ch = resolve_canvas(aspect, sampled_mp)
        g[NODE_CANVAS_WIDTH] = {"inputs": {"value": cw}, "class_type": "PrimitiveInt",
                                "_meta": {"title": "Canvas width"}}
        g[NODE_CANVAS_HEIGHT] = {"inputs": {"value": ch}, "class_type": "PrimitiveInt",
                                 "_meta": {"title": "Canvas height"}}
        retarget(g, NODE_RESOLUTION, 0, NODE_CANVAS_WIDTH)
        retarget(g, NODE_RESOLUTION, 1, NODE_CANVAS_HEIGHT)
    else:
        g[NODE_RESOLUTION]["inputs"]["aspect_ratio"] = aspect
        g[NODE_RESOLUTION]["inputs"]["megapixels"] = sampled_mp
        g[NODE_RESOLUTION]["inputs"]["multiple"] = RESOLUTION_MULTIPLE

    for nid in NODE_SLA:
        g[nid]["inputs"]["enabled"] = sla
        g[nid]["inputs"]["sparsity_ratio"] = sla_sparsity
        g[nid]["inputs"]["block_size"] = "64"
    g[NODE_SPARSE_ATTENTION]["inputs"]["switch"] = sparse
    g[NODE_BASE_DETAIL]["inputs"]["switch"] = detail
    for nid in NODE_LOOP_DETAIL:
        g[nid]["inputs"]["switch"] = detail
    g[NODE_BASE_UPSCALE]["inputs"]["switch"] = (not extending) and rtx
    g[NODE_LOOP_UPSCALE]["inputs"]["switch"] = rtx
    g[NODE_BASE_AUDIO]["inputs"]["switch"] = (not extending) and audio
    g[NODE_LOOP_AUDIO]["inputs"]["switch"] = audio

    g[sink]["inputs"]["filename_prefix"] = "minimax_i2v/mmi2v_test"
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


def main():
    server = sys.argv[1] if len(sys.argv) > 1 else "http://10.0.0.10:8188"
    with urllib.request.urlopen(f"{server}/object_info", timeout=120) as r:
        oi = json.loads(r.read().decode("utf-8"))
    base = json.load(io.open(WORKFLOW, encoding="utf-8"))

    # Use image names the server really has, so LoadImage's combo check means something.
    pool = oi["LoadImage"]["input"]["required"]["image"][0]
    pool = pool if isinstance(pool, list) else oi["LoadImage"]["input"]["required"]["image"][1]["options"]
    a, b, c, d = (pool + pool + pool + pool)[:4]

    seg = lambda n, s: {"prompt": f"Continuation {n}", "seconds": s}
    cases = [
        ("1 ref, no continuation, defaults", [a], [], dict(detail=True, rtx=False, audio=True, max_fidelity=False, overlap=22)),
        ("1 ref, no continuation, everything off", [a], [], dict(detail=False, rtx=False, audio=False, max_fidelity=False, overlap=5)),
        ("1 ref, no continuation, RTX + max refs", [a], [], dict(detail=True, rtx=True, audio=True, max_fidelity=True, overlap=22)),
        ("4 refs, no continuation", [a, b, c, d], [], dict(detail=True, rtx=False, audio=True, max_fidelity=False, overlap=22)),
        ("1 ref, 1 continuation", [a], [seg(1, 10)], dict(detail=True, rtx=False, audio=True, max_fidelity=False, overlap=22)),
        ("2 refs, 3 continuations", [a, b], [seg(1, 5), seg(2, 12), seg(3, 15)], dict(detail=True, rtx=True, audio=True, max_fidelity=False, overlap=56)),
        ("4 refs, 3 continuations, no detail pass", [a, b, c, d], [seg(1, 8), seg(2, 8), seg(3, 8)], dict(detail=False, rtx=False, audio=False, max_fidelity=True, overlap=39)),
        ("21:9, 1 ref", [a], [], dict(detail=True, rtx=False, audio=True, max_fidelity=False, overlap=22, aspect="21:9 (Ultrawide)")),
        ("9:16, 1 ref", [a], [], dict(detail=True, rtx=False, audio=True, max_fidelity=False, overlap=22, aspect="9:16 (Portrait Widescreen)")),
        ("2:1 stereo, 2 refs, max fidelity", [a, b], [], dict(detail=True, rtx=False, audio=True, max_fidelity=True, overlap=22, aspect=STEREO_ASPECT)),
        ("2:1 stereo, 2 refs, no detail pass", [a, b], [], dict(detail=False, rtx=False, audio=True, max_fidelity=True, overlap=22, aspect=STEREO_ASPECT)),
        ("2:1 stereo, 2 refs, 2 continuations", [a, b], [seg(1, 10), seg(2, 10)], dict(detail=True, rtx=True, audio=True, max_fidelity=True, overlap=22, aspect=STEREO_ASPECT)),
        ("2:1 stereo at 1.5 MP", [a], [], dict(detail=True, rtx=False, audio=True, max_fidelity=True, overlap=22, aspect=STEREO_ASPECT, megapixels=1.5)),
        ("1:2 over-under, 2 refs, max fidelity", [a, b], [], dict(detail=True, rtx=False, audio=True, max_fidelity=True, overlap=22, aspect=STEREO_OU_ASPECT)),
        ("1:2 over-under, no detail pass", [a, b], [], dict(detail=False, rtx=False, audio=True, max_fidelity=True, overlap=22, aspect=STEREO_OU_ASPECT)),
        ("1:2 over-under, 2 continuations", [a, b], [seg(1, 10), seg(2, 10)], dict(detail=True, rtx=True, audio=True, max_fidelity=True, overlap=22, aspect=STEREO_OU_ASPECT)),
    ]

    # A stereo canvas is only worth having if it holds its exact proportion and survives the 2x the
    # way the continuation loop's own round(a * b / 32) * 32 expects.
    for aspect in LITERAL_ASPECTS:
        w_ratio, h_ratio = RATIO_PAIRS[aspect]
        for mp in (0.4, 0.7, 1.0, 1.5):
            cw, ch = resolve_canvas(aspect, mp / (LATENT_UPSCALE_FACTOR ** 2))
            fw, fh = cw * LATENT_UPSCALE_FACTOR, ch * LATENT_UPSCALE_FACTOR
            assert cw * h_ratio == ch * w_ratio, f"{aspect} draft {cw}x{ch} is off ratio"
            assert cw % RESOLUTION_MULTIPLE == 0 and ch % RESOLUTION_MULTIPLE == 0, f"{cw}x{ch} off the grid"
            assert round(cw * LATENT_UPSCALE_FACTOR / RESOLUTION_MULTIPLE) * RESOLUTION_MULTIPLE == fw
            assert round(ch * LATENT_UPSCALE_FACTOR / RESOLUTION_MULTIPLE) * RESOLUTION_MULTIPLE == fh
            eye = f"{fw // 2}x{fh}" if w_ratio > h_ratio else f"{fw}x{fh // 2}"
            print(f"{aspect:<24} {mp:0.1f} MP -> draft {cw}x{ch}, finished {fw}x{fh}, {eye} per eye")
    print()

    failed = 0
    for label, refs, conts, opts in cases:
        g, sink = build(base, refs, conts, **opts)
        errors, notes = validate(g, oi)
        if opts.get("aspect") in LITERAL_ASPECTS and NODE_RESOLUTION in g:
            errors.append(f"{NODE_RESOLUTION}: ResolutionSelector survived the prune on a stereo canvas")
        status = "OK " if not errors else "FAIL"
        print(f"[{status}] {label}: {len(g)} nodes, sink {sink}, {len(notes)} ignored extras")
        for e in errors:
            print(f"         {e}")
        failed += bool(errors)

    print("\nall graphs valid" if not failed else f"\n{failed} case(s) failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
