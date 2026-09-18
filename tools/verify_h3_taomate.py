#!/usr/bin/env python3
r"""
Rebuilds, in Python, the exact submissions ⚡ H3 Express makes on the 🍥 TaoMate stack, and validates
each one against a live ComfyUI /object_info the way the server's own validate_prompt does.

What it mirrors, patch for patch:

  H3ErosViewModel.ApplyCommonInputs   the model, the injected LoadImage per cast panel wired into
                                      `5.ref_images.ref_image_N`, ref_image_size, the prompt, the
                                      length, the step count and the canvas
  H3ExpressViewModel.ApplyCommonInputs  the optional LoRA: every reader of node `21` retargeted at a
                                      new LoraLoaderModelOnly which reads `21` itself
  H3ExpressViewModel.BuildFinish      the clip's seed into pass 1 and the derived one into pass 2, the
                                      RTX factor, the sink wired through RIFE or straight past it, and
                                      the prune to node `34`

The cases cover both RIFE settings, both ref_image_size settings, 1 and 9 reference panels, and the
LoRA on and off — eight submissions in all. Every one has to validate, and none may be left holding a
link into a node the prune removed, which is the failure the tab cannot see until the server rejects it.

Run:  python tools/verify_h3_taomate.py [--object-info URL_OR_PATH]
"""
import argparse
import copy
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GRAPH = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-taomate.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from build_h3_taomate import check, WIDGET_ONLY_INPUTS, fetch_object_info  # noqa: E402

# The C# constants this mirrors. A change on either side has to be a change on both.
NODE_PROMPT, NODE_SECONDS, NODE_STEPS, NODE_RESOLUTION = "22:11", "22:23", "22:8", "22:9"
NODE_REF2V, NODE_UNET, NODE_POWER_LORA = "5", "171:4", "21"
NODE_LORA = "h3express_lora"
NODE_PASS1, NODE_PASS2, NODE_RTX = "tm:pass1", "tm:pass2", "tm:rtx"
NODE_AUDIO, NODE_RIFE, NODE_SINK = "190", "165", "34"
REF_PREFIX = "ref_images.ref_image_"

TAOMATE_STEPS = 10
TAOMATE_UPSCALE = 2.0
DRAFT_FPS = 24
MAX_REFS = 9


def second_leg_seed(seed):
    """H3ExpressViewModel.SecondLegSeed."""
    return (seed ^ 0x2545F4914F6CDD1D) & 0x7FFFFFFFFFFFFFFF


def apply_common_inputs(g, *, model, panels, max_fidelity, prompt, seconds, megapixels, aspect,
                        lora, lora_strength, panel_name):
    if model:
        require(g, NODE_UNET, "UNETLoader")
        g[NODE_UNET]["inputs"]["unet_name"] = model

    require(g, NODE_REF2V, "MiniMaxH3ReferenceToVideo")
    require(g, NODE_SINK, "VHS_VideoCombine")

    loaders = []
    for i in range(panels):
        nid = f"eros_ref_{i}"
        g[nid] = {"inputs": {"image": panel_name}, "class_type": "LoadImage",
                  "_meta": {"title": f"Picture {i + 1}"}}
        loaders.append(nid)

    inputs = g[NODE_REF2V]["inputs"]
    for key in [k for k in inputs if k.startswith(REF_PREFIX)]:
        del inputs[key]
    for i, nid in enumerate(loaders):
        inputs[REF_PREFIX + str(i)] = [nid, 0]
    inputs["ref_image_size"] = "max" if max_fidelity else "match"

    g[NODE_PROMPT]["inputs"]["value"] = prompt
    g[NODE_SECONDS]["inputs"]["value"] = seconds
    g[NODE_STEPS]["inputs"]["value"] = TAOMATE_STEPS

    g[NODE_RESOLUTION]["inputs"]["aspect_ratio"] = aspect
    g[NODE_RESOLUTION]["inputs"]["megapixels"] = megapixels
    g[NODE_RESOLUTION]["inputs"]["multiple"] = 32

    # The Express LoRA splice: retarget first, while the new node does not exist, then wire it to 21.
    if lora and lora_strength > 0:
        require(g, NODE_POWER_LORA, "Power Lora Loader (rgthree)")
        retarget(g, NODE_POWER_LORA, 0, NODE_LORA)
        g[NODE_LORA] = {"inputs": {"lora_name": lora, "strength_model": lora_strength,
                                   "model": [NODE_POWER_LORA, 0]},
                        "class_type": "LoraLoaderModelOnly",
                        "_meta": {"title": "H3 Express LoRA"}}


def build_finish(g, *, seed, use_rife, run_token, subfolder="h3_express"):
    for nid, cls in ((NODE_PASS1, "ClownsharKSampler_Beta"), (NODE_PASS2, "ClownsharKSampler_Beta"),
                     (NODE_RTX, "RTXVideoSuperResolution")):
        require(g, nid, cls)

    g[NODE_PASS1]["inputs"]["seed"] = seed
    g[NODE_PASS2]["inputs"]["seed"] = second_leg_seed(seed)
    g[NODE_RTX]["inputs"]["resize_type"] = "scale by multiplier"
    g[NODE_RTX]["inputs"]["resize_type.scale"] = TAOMATE_UPSCALE

    if use_rife:
        g[NODE_RIFE]["inputs"]["source_fps"] = float(DRAFT_FPS)
        g[NODE_RIFE]["inputs"]["target_fps"] = float(DRAFT_FPS * 2)
        g[NODE_RIFE]["inputs"]["images"] = [NODE_RTX, 0]
        g[NODE_SINK]["inputs"]["images"] = [NODE_RIFE, 0]
        g[NODE_SINK]["inputs"]["frame_rate"] = DRAFT_FPS * 2
    else:
        g[NODE_SINK]["inputs"]["images"] = [NODE_RTX, 0]
        g[NODE_SINK]["inputs"]["frame_rate"] = DRAFT_FPS
    g[NODE_SINK]["inputs"]["audio"] = [NODE_AUDIO, 0]
    g[NODE_SINK]["inputs"]["save_output"] = True
    g[NODE_SINK]["inputs"]["filename_prefix"] = f"{subfolder}/{run_token}_final"

    return prune_to_outputs(g, [NODE_SINK])


# -- the helpers the C# uses, by the same rules -----------------------------------------------------

def require(g, nid, cls):
    if nid not in g:
        raise SystemExit(f"node '{nid}' is not in the graph")
    if g[nid]["class_type"] != cls:
        raise SystemExit(f"node '{nid}' is {g[nid]['class_type']}, expected {cls}")


def retarget(g, source, slot, new_id):
    for node in g.values():
        for key, value in list(node["inputs"].items()):
            if isinstance(value, list) and len(value) == 2 and value[0] == source and value[1] == slot:
                node["inputs"][key] = [new_id, 0]


def prune_to_outputs(g, keep):
    reachable = set()
    stack = list(keep)
    while stack:
        nid = stack.pop()
        if nid in reachable:
            continue
        reachable.add(nid)
        for value in g.get(nid, {}).get("inputs", {}).values():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                stack.append(value[0])
    pruned = {nid: n for nid, n in g.items() if nid in reachable}
    return pruned, len(g) - len(pruned)


def dangling(g):
    """The failure a prune can leave behind: a kept node reading a node that was removed."""
    bad = []
    for nid, n in g.items():
        for key, value in n["inputs"].items():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                if value[0] not in g:
                    bad.append(f"{nid}.{key} -> missing '{value[0]}'")
    return bad


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--object-info", default=OBJ_URL)
    args = ap.parse_args()

    with open(GRAPH, encoding="utf-8") as fh:
        shipped = json.load(fh)
    obj = fetch_object_info(args.object_info)

    # A real run uploads each cast panel before it submits, so the injected LoadImage names a file the
    # server already has. Borrowing one the server lists keeps that half of the check honest instead of
    # failing on a placeholder every time.
    panel_name = obj["LoadImage"]["input"]["required"]["image"][0][0]

    cases = []
    for use_rife in (True, False):
        for max_fidelity in (False, True):
            for lora in ("", "H3/h3-realism-people-t2v-i2v-r2v.safetensors"):
                cases.append({
                    "use_rife": use_rife, "max_fidelity": max_fidelity, "lora": lora,
                    "panels": 9 if max_fidelity else 1,
                })

    failures = 0
    for i, case in enumerate(cases, start=1):
        g = copy.deepcopy(shipped)
        apply_common_inputs(
            g, model="h3-minimax/minimax_h3_fl2va_pruned_int8_convrot.safetensors",
            panels=case["panels"], max_fidelity=case["max_fidelity"],
            prompt="subject_definitions:\n<Subject 1>: a man ...", seconds=7.0,
            megapixels=0.8, aspect="16:9 (Widescreen)",
            lora=case["lora"], lora_strength=0.85, panel_name=panel_name)
        pruned, removed = build_finish(
            g, seed=4815162342, use_rife=case["use_rife"], run_token="h3express_20260914_120000_c01")

        problems = dangling(pruned) + check(pruned, obj)

        # What the prune must have done, rather than only what it must not have broken.
        if case["use_rife"] and NODE_RIFE not in pruned:
            problems.append("RIFE is on but node 165 was pruned away")
        if not case["use_rife"] and NODE_RIFE in pruned:
            problems.append("RIFE is off but node 165 is still in the graph")
        if pruned[NODE_SINK]["inputs"]["images"][0] != (NODE_RIFE if case["use_rife"] else NODE_RTX):
            problems.append("the sink is not reading the node it should")
        refs = [k for k in pruned[NODE_REF2V]["inputs"] if k.startswith(REF_PREFIX)]
        if len(refs) != case["panels"]:
            problems.append(f"{len(refs)} ref slots wired, expected {case['panels']}")
        if case["panels"] > MAX_REFS:
            problems.append("more reference panels than the node accepts")
        # Both legs of the relay must sample through the LoRA when there is one.
        if case["lora"]:
            for leg, expect in ((NODE_PASS1, "tm:shift"), (NODE_PASS2, "tm:tao")):
                if pruned[leg]["inputs"]["model"][0] != expect:
                    problems.append(f"{leg} no longer reads {expect}")
            for wire in ("tm:attn", "tm:tao"):
                if pruned[wire]["inputs"]["model"][0] != NODE_LORA:
                    problems.append(f"{wire} does not read the spliced LoRA — that leg samples without it")
            if pruned[NODE_LORA]["inputs"]["model"][0] != NODE_POWER_LORA:
                problems.append("the spliced LoRA does not read node 21")
        elif NODE_LORA in pruned:
            problems.append("no LoRA chosen but one is in the graph")
        # The relay itself.
        if pruned[NODE_PASS2]["inputs"]["latent_image"][0] != NODE_PASS1:
            problems.append("pass 2 is not continuing pass 1")
        if pruned[NODE_PASS1]["inputs"]["sampler_mode"] != "standard" or \
                pruned[NODE_PASS2]["inputs"]["sampler_mode"] != "resample":
            problems.append("the relay's sampler modes are not standard -> resample")
        if pruned[NODE_PASS1]["inputs"]["steps"] != [NODE_STEPS, 0] or \
                pruned[NODE_PASS2]["inputs"]["steps"] != [NODE_STEPS, 0]:
            problems.append("a leg's step count is not linked to 22:8")
        if pruned[NODE_PASS2]["inputs"]["seed"] == pruned[NODE_PASS1]["inputs"]["seed"]:
            problems.append("both legs share a seed")

        label = (f"case {i}: rife={case['use_rife']} refs={case['panels']} "
                 f"size={'max' if case['max_fidelity'] else 'match'} "
                 f"lora={'yes' if case['lora'] else 'no'}")
        if problems:
            failures += 1
            print(f"! {label}")
            for p in problems:
                print(f"    ! {p}")
        else:
            print(f"ok {label} — {len(pruned)} nodes, {removed} pruned")

    print(f"\n{len(cases) - failures}/{len(cases)} submissions validate")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
