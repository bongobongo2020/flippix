"""Replays H3 Cast's submit-time graph patch offline and validates it against a live ComfyUI
/object_info, for both reference paths.

The tab rewires h3facerefiner.json on every submit: N LoadImage nodes for the cast's panels, then
either the numbered `ref_images.ref_image_N` slots the workflow ships with, or — when the server's
MiniMaxH3-Contex-Loop is new enough — a MiniMaxH3TaggedPictureReference chain feeding
MiniMaxH3TaggedReferenceToVideo. Neither patch is exercised until a 20-minute render is already
under way, so this replays both and checks what ComfyUI would reject:

  * every class_type exists on the server;
  * every input written exists on that class (and every required input is supplied);
  * no link points at a node that was pruned, and no node is unreachable from the video sink;
  * combo inputs carry a legal value.

Usage:  python tools/verify_h3cast_tagged.py [--url http://192.168.1.10:8188]

Mirrors H3CastViewModel.ConvertToTaggedReferences / WireReferenceImages / WireOutputChain.
Keep the node ids and input names below in step with those methods.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys
import urllib.request

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

WORKFLOW = pathlib.Path(__file__).resolve().parent.parent / "workflow" / "video" / "h3-minimax" / "h3facerefiner.json"

NODE_REFERENCE = "23"
NODE_REFINE_REFERENCE = "101"
NODE_CHARACTER_1 = "44"
NODE_PROMPT = "48"
NODE_REFINE_PROMPT = "931"
NODE_BASE_FRAMES = "3"
NODE_FACE_STITCH = "111"
NODE_RTX = "64"
NODE_VIDEO_COMBINE = "65"
REF_IMAGE_PREFIX = "ref_images.ref_image_"
REFERENCE_NODE_ID_BASE = 900
TAGGED_NODE_ID_BASE = 920
TAGGED_REFERENCE_CLASS = "MiniMaxH3TaggedReferenceToVideo"
TAGGED_PICTURE_CLASS = "MiniMaxH3TaggedPictureReference"

# Nodes whose inputs are built in the browser and never declared in /object_info. Their widget
# names cannot be checked against anything, so only their links are.
DYNAMIC_INPUT_CLASSES = {"Power Lora Loader (rgthree)"}

# Widgets the export carries that /object_info does not declare, because the node expands them from
# another widget's value in the browser. ComfyUI ignores undeclared inputs at validation, and these
# ship in the workflow file rather than being written by the tab, so they are not findings.
UNDECLARED_BUT_HARMLESS = {
    ("VHS_VideoCombine", "pix_fmt"),
    ("VHS_VideoCombine", "crf"),
    ("VHS_VideoCombine", "save_metadata"),
    ("VHS_VideoCombine", "trim_to_audio"),
}


def aliases(character: int, panels: int) -> list[str]:
    if panels <= 1:
        return [f"char{character}"]
    if panels == 3:
        return [f"char{character}_front", f"char{character}_back", f"char{character}_face"]
    return [f"char{character}_v{v}" for v in range(1, panels + 1)]


def all_aliases(panels1: int, panels2: int) -> list[str]:
    return aliases(1, max(1, panels1)) + (aliases(2, panels2) if panels2 > 0 else [])


def wire_reference_images(graph: dict, uploaded: list[str]) -> dict:
    loaders = []
    for i, name in enumerate(uploaded):
        node_id = NODE_CHARACTER_1 if i == 0 else str(REFERENCE_NODE_ID_BASE + i)
        graph[node_id] = {"inputs": {"image": name}, "class_type": "LoadImage",
                          "_meta": {"title": f"Ref Image {i + 1}"}}
        loaders.append(node_id)

    for node_id in (NODE_REFERENCE, NODE_REFINE_REFERENCE):
        node = graph.get(node_id)
        if node is None:
            continue
        for key in [k for k in node["inputs"] if k.startswith(REF_IMAGE_PREFIX)]:
            del node["inputs"][key]
        for i, loader in enumerate(loaders):
            node["inputs"][f"{REF_IMAGE_PREFIX}{i}"] = [loader, 0]
    return graph


def convert_to_tagged(graph: dict, alias_list: list[str]) -> dict:
    inputs = graph[NODE_REFERENCE]["inputs"]

    loaders = []
    i = 0
    while f"{REF_IMAGE_PREFIX}{i}" in inputs:
        loaders.append(inputs[f"{REF_IMAGE_PREFIX}{i}"][0])
        i += 1
    assert len(loaders) == len(alias_list), f"{len(loaders)} loaders vs {len(alias_list)} aliases"

    previous = None
    for i, alias in enumerate(alias_list):
        node_id = str(TAGGED_NODE_ID_BASE + i)
        tagged_inputs = {"image": [loaders[i], 0], "tag": alias}
        if previous is not None:
            tagged_inputs["previous"] = [previous, 0]
        graph[node_id] = {"inputs": tagged_inputs, "class_type": TAGGED_PICTURE_CLASS,
                          "_meta": {"title": f"@{alias}"}}
        previous = node_id

    for key in [k for k in inputs if k.startswith(REF_IMAGE_PREFIX)]:
        del inputs[key]

    graph[NODE_REFERENCE]["class_type"] = TAGGED_REFERENCE_CLASS
    inputs["references"] = [previous, 0]
    inputs["clip_index"] = 1
    inputs["clip_count"] = 1
    inputs["reference_policy"] = "strict"

    refine = graph.get(NODE_REFINE_REFERENCE)
    if refine is not None:
        graph[NODE_REFINE_PROMPT] = {"inputs": {"value": ""}, "class_type": "PrimitiveStringMultiline",
                                     "_meta": {"title": "Refine prompt (numbered references)"}}
        refine["inputs"]["prompt"] = [NODE_REFINE_PROMPT, 0]
    return graph


def wire_output_chain(graph: dict, face_refine: bool, rtx: bool) -> dict:
    frames = NODE_FACE_STITCH if face_refine else NODE_BASE_FRAMES
    graph[NODE_RTX]["inputs"]["images"] = [frames, 0]
    graph[NODE_VIDEO_COMBINE]["inputs"]["images"] = [NODE_RTX if rtx else frames, 0]
    return graph


def prune_to_outputs(graph: dict, sinks: list[str]) -> dict:
    """Reachability sweep from the sinks, matching PruneToOutputs."""
    keep, stack = set(), list(sinks)
    while stack:
        node_id = stack.pop()
        if node_id in keep or node_id not in graph:
            continue
        keep.add(node_id)
        for value in graph[node_id]["inputs"].values():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                stack.append(value[0])
    return {k: v for k, v in graph.items() if k in keep}


def validate(graph: dict, object_info: dict, label: str) -> list[str]:
    problems = []
    for node_id, node in sorted(graph.items(), key=lambda kv: int(kv[0].split(":")[0])):
        class_type = node["class_type"]
        spec = object_info.get(class_type)
        if spec is None:
            problems.append(f"{node_id}: class {class_type} is not on the server")
            continue

        required = spec["input"].get("required", {})
        optional = spec["input"].get("optional", {})
        known = {**required, **optional}
        dynamic = class_type in DYNAMIC_INPUT_CLASSES

        for key, value in node["inputs"].items():
            # Autogrow inputs (`ref_images.ref_image_0`) are declared under their group name.
            base = key.split(".")[0]
            if key not in known and base not in known:
                if not dynamic and (class_type, key) not in UNDECLARED_BUT_HARMLESS:
                    problems.append(f"{node_id} ({class_type}): no input '{key}'")
                continue
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                if value[0] not in graph:
                    problems.append(f"{node_id} ({class_type}).{key} -> missing node {value[0]}")
                continue
            # LoadImage.image / LoadAudio.audio enumerate files already sitting in ComfyUI/input;
            # the tab uploads its panels at submit time, so their absence here means nothing.
            if (class_type, key) in (("LoadImage", "image"), ("LoadAudio", "audio")):
                continue
            declared = known.get(key)
            if declared and isinstance(declared[0], list) and value not in declared[0]:
                problems.append(f"{node_id} ({class_type}).{key} = {value!r} not in {declared[0]}")

        for key in required if not dynamic else ():
            if key in node["inputs"]:
                continue
            # LoadImage.image etc. are widgets the tab always writes; a genuinely absent required
            # input is only a problem when nothing supplies it.
            if any(k.split(".")[0] == key for k in node["inputs"]):
                continue
            problems.append(f"{node_id} ({class_type}): required input '{key}' is not supplied")

    unreachable = set(graph) - set(prune_to_outputs(dict(graph), [NODE_VIDEO_COMBINE]))
    for node_id in sorted(unreachable):
        problems.append(f"{node_id} ({graph[node_id]['class_type']}): unreachable from the video sink")

    print(f"  {label}: {len(graph)} nodes, {len(problems)} problem(s)")
    return problems


# Transcribed from ComfyUI-MiniMaxH3-Contex-Loop v0.4.5 chain_nodes.py, for checking the tagged
# path against a server whose copy of the pack is older than v0.4.0 and cannot describe these
# classes itself. Drop this and --assume-tagged once the server is updated.
UPSTREAM_TAGGED_SPECS = {
    TAGGED_PICTURE_CLASS: {
        "input": {
            "required": {"image": ["IMAGE", {}], "tag": ["STRING", {"default": "hero_face"}]},
            "optional": {"previous": ["H3_TAGGED_REFERENCES", {}]},
        },
        "output": ["H3_TAGGED_REFERENCES", "STRING", "STRING"],
    },
    TAGGED_REFERENCE_CLASS: {
        "input": {
            "required": {
                "clip": ["CLIP", {}],
                "vae": ["VAE", {}],
                "audio_vae": ["VAE", {}],
                "references": ["H3_TAGGED_REFERENCES", {}],
                "clip_index": ["INT", {"default": 1, "min": 1, "max": 128}],
                "clip_count": ["INT", {"default": 1, "min": 1, "max": 128}],
                "prompt": ["STRING", {"multiline": True}],
                "width": ["INT", {"default": 960, "min": 32, "max": 4096}],
                "height": ["INT", {"default": 544, "min": 32, "max": 4096}],
                "length": ["INT", {"default": 124, "min": 5, "max": 3600}],
                "ref_image_size": [["match", "max"], {"default": "match"}],
            },
            "optional": {
                "state": ["H3_CHAIN_STATE", {}],
                "reference_policy": [["strict", "soft", "disabled"], {"default": "strict"}],
            },
        },
        "output": ["CONDITIONING", "LATENT", "STRING", "STRING", "STRING"],
    },
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://192.168.1.10:8188")
    parser.add_argument("--assume-tagged", action="store_true",
                        help="check the tagged path against the transcribed upstream node "
                             "definitions when the server's pack is too old to declare them")
    args = parser.parse_args()

    with urllib.request.urlopen(f"{args.url}/object_info", timeout=60) as response:
        object_info = json.load(response)
    print(f"/object_info: {len(object_info)} node classes from {args.url}")

    has_tagged = TAGGED_REFERENCE_CLASS in object_info and TAGGED_PICTURE_CLASS in object_info
    print(f"tagged reference nodes present: {has_tagged}")
    if not has_tagged and args.assume_tagged:
        object_info = {**object_info, **UPSTREAM_TAGGED_SPECS}
        has_tagged = True
        print("--assume-tagged: using the transcribed v0.4.5 definitions for the two tagged classes")

    source = json.loads(WORKFLOW.read_text(encoding="utf-8"))
    failures = 0

    for panels1, panels2 in ((3, 3), (3, 0), (1, 1), (4, 2)):
        alias_list = all_aliases(panels1, panels2)
        uploaded = [f"panel_{i}.png" for i in range(len(alias_list))]

        for face_refine in (True, False):
            for rtx in (False, True):
                for tagged in ((True, False) if has_tagged else (False,)):
                    label = (f"{panels1}+{panels2} refs, "
                             f"{'tagged' if tagged else 'numbered'}, "
                             f"{'refine' if face_refine else 'no refine'}, "
                             f"{'rtx' if rtx else 'no rtx'}")
                    graph = json.loads(json.dumps(source))
                    graph = wire_reference_images(graph, uploaded)
                    if tagged:
                        graph = convert_to_tagged(graph, alias_list)
                    graph = wire_output_chain(graph, face_refine, rtx)
                    graph = prune_to_outputs(graph, [NODE_VIDEO_COMBINE])

                    problems = validate(graph, object_info, label)
                    for problem in problems:
                        print(f"    ! {problem}")
                    failures += len(problems)

    print()
    print("OK" if failures == 0 else f"{failures} problem(s)")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
