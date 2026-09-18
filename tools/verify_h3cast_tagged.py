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

Usage:  python tools/verify_h3cast_tagged.py [--url http://10.0.0.10:8188]

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
NODE_FACE_TRACK = "100"
NODE_FACE_STITCH = "111"

# Character 2's pass — the 100-block cloned into the 200s at submit time.
REFINE_PASS_2_OFFSET = 100
NODE_REFINE_PROMPT_2 = "932"
NODE_FACE_TRACK_2 = "200"
NODE_REFINE_REFERENCE_2 = "201"
NODE_FACE_STITCH_2 = "211"
NODE_RTX = "64"
NODE_VIDEO_COMBINE = "65"
REF_IMAGE_PREFIX = "ref_images.ref_image_"
REFERENCE_NODE_ID_BASE = 900
TAGGED_NODE_ID_BASE = 920
TAGGED_REFERENCE_CLASS = "MiniMaxH3TaggedReferenceToVideo"
TAGGED_PICTURE_CLASS = "MiniMaxH3TaggedPictureReference"

# Nodes whose inputs are built in the browser and never declared in /object_info. Their widget
# names cannot be checked against anything, so only their links are.
DYNAMIC_INPUT_CLASSES = {"Power Lora Loader (rgthree)", "RTXVideoSuperResolution"}

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


def attach_references(graph: dict, node_id: str, loaders: list[str]) -> None:
    node = graph[node_id]
    for key in [k for k in node["inputs"] if k.startswith(REF_IMAGE_PREFIX)]:
        del node["inputs"][key]
    for i, loader in enumerate(loaders):
        node["inputs"][f"{REF_IMAGE_PREFIX}{i}"] = [loader, 0]


def wire_reference_images(graph: dict, uploaded: list[str]) -> list[str]:
    loaders = []
    for i, name in enumerate(uploaded):
        node_id = NODE_CHARACTER_1 if i == 0 else str(REFERENCE_NODE_ID_BASE + i)
        graph[node_id] = {"inputs": {"image": name}, "class_type": "LoadImage",
                          "_meta": {"title": f"Ref Image {i + 1}"}}
        loaders.append(node_id)

    for node_id in (NODE_REFERENCE, NODE_REFINE_REFERENCE):
        if node_id in graph:
            attach_references(graph, node_id, loaders)
    return loaders


def add_second_refine_pass(graph: dict) -> dict:
    """Mirrors AddSecondRefinePass: nodes 100-111 cloned into the 200s, every link inside the clone
    remapped to the clone, and the two that read the base decode moved onto the first pass's stitch
    so the second edit lands on top of the first rather than discarding it."""
    mapping = {node_id: str(int(node_id) + REFINE_PASS_2_OFFSET)
               for node_id in graph if node_id.isdigit() and 100 <= int(node_id) <= 111}

    for source, clone in mapping.items():
        node = json.loads(json.dumps(graph[source]))
        for key, value in node["inputs"].items():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                target = mapping.get(value[0],
                                     NODE_FACE_STITCH if value[0] == NODE_BASE_FRAMES else value[0])
                node["inputs"][key] = [target, value[1]]
        node["_meta"] = {"title": f"{node.get('_meta', {}).get('title', clone)} (character 2)"}
        graph[clone] = node
    return graph


def wire_refine_passes(graph: dict, loaders: list[str], panels1: int, panels2: int,
                       per_character: bool, refine_character2: bool) -> dict:
    """Mirrors WireRefinePasses. Each character's pass sees only their own panels, numbered from
    ref_image_0, reads its own prompt primitive, and tracks that character by their face close-up —
    the last panel, which is the order the sheet is built and split in."""
    loaders1, loaders2 = loaders[:panels1], loaders[panels1:panels1 + panels2]

    graph[NODE_REFINE_PROMPT] = {"inputs": {"value": ""}, "class_type": "PrimitiveStringMultiline",
                                 "_meta": {"title": "Refine prompt (character 1)"}}
    graph[NODE_REFINE_REFERENCE]["inputs"]["prompt"] = [NODE_REFINE_PROMPT, 0]
    if per_character:
        attach_references(graph, NODE_REFINE_REFERENCE, loaders1)
        graph[NODE_FACE_TRACK]["inputs"]["identity_reference"] = [loaders1[-1], 0]

    if refine_character2 and loaders2:
        graph = add_second_refine_pass(graph)
        graph[NODE_REFINE_PROMPT_2] = {"inputs": {"value": ""}, "class_type": "PrimitiveStringMultiline",
                                       "_meta": {"title": "Refine prompt (character 2)"}}
        graph[NODE_REFINE_REFERENCE_2]["inputs"]["prompt"] = [NODE_REFINE_PROMPT_2, 0]
        attach_references(graph, NODE_REFINE_REFERENCE_2, loaders2)
        graph[NODE_FACE_TRACK_2]["inputs"]["identity_reference"] = [loaders2[-1], 0]
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

    # The refine passes keep numbered references and get their own prompt primitives — see
    # wire_refine_passes, which runs on the numbered path too.
    return graph


def wire_output_chain(graph: dict, face_refine: bool, refine_character2: bool, rtx: bool) -> dict:
    frames = NODE_BASE_FRAMES
    if face_refine:
        frames = NODE_FACE_STITCH_2 if refine_character2 else NODE_FACE_STITCH
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


def check_refine_structure(graph: dict, face_refine: bool, refine_character2: bool,
                           panels1: int, panels2: int, per_character: bool, label: str) -> list[str]:
    """The invariants of the face-refine passes, which no server-side check can catch: a pass wired
    to the wrong panels or reading the wrong prompt is a valid graph that redraws the wrong face."""
    problems = []

    pass2_nodes = (NODE_FACE_TRACK_2, NODE_REFINE_REFERENCE_2, NODE_FACE_STITCH_2, NODE_REFINE_PROMPT_2)
    for node_id in pass2_nodes:
        if refine_character2 and node_id not in graph:
            problems.append(f"[{label}] character 2's pass is on but node {node_id} is missing")
        if not refine_character2 and node_id in graph:
            problems.append(f"[{label}] character 2's pass is off but {node_id} is in the graph")

    if not face_refine:
        for node_id in (NODE_FACE_TRACK, NODE_REFINE_REFERENCE, NODE_FACE_STITCH):
            if node_id in graph:
                problems.append(f"[{label}] face refine is off but {node_id} survived the prune")
        return problems

    expected1 = panels1 if per_character else panels1 + panels2
    wired = sum(1 for k in graph[NODE_REFINE_REFERENCE]["inputs"] if k.startswith(REF_IMAGE_PREFIX))
    if wired != expected1:
        problems.append(f"[{label}] refine pass 1 has {wired} ref_image slot(s), expected {expected1}")

    prompt = graph[NODE_REFINE_REFERENCE]["inputs"].get("prompt")
    if not (isinstance(prompt, list) and prompt[0] == NODE_REFINE_PROMPT):
        problems.append(f"[{label}] the refine pass reads the clip's own prompt, not its cast-of-one primitive")

    identity = graph[NODE_FACE_TRACK]["inputs"].get("identity_reference")
    if per_character and not isinstance(identity, list):
        problems.append(f"[{label}] the tracker has no identity_reference: it follows whoever is "
                        "largest, which in a two-character clip is not reliably the same person")

    if refine_character2:
        wired2 = sum(1 for k in graph[NODE_REFINE_REFERENCE_2]["inputs"] if k.startswith(REF_IMAGE_PREFIX))
        if wired2 != panels2:
            problems.append(f"[{label}] refine pass 2 has {wired2} ref_image slot(s), expected {panels2}")
        prompt2 = graph[NODE_REFINE_REFERENCE_2]["inputs"].get("prompt")
        if not (isinstance(prompt2, list) and prompt2[0] == NODE_REFINE_PROMPT_2):
            problems.append(f"[{label}] character 2's pass does not read its own prompt primitive")
        # Composing rather than competing: pass 2 reads what pass 1 stitched, at both ends.
        for node_id, key in ((NODE_FACE_TRACK_2, "images"), (NODE_FACE_STITCH_2, "base_images")):
            link = graph[node_id]["inputs"].get(key)
            if not (isinstance(link, list) and link[0] == NODE_FACE_STITCH):
                problems.append(f"[{label}] {node_id}.{key} reads {link}, expected the first pass's "
                                f"stitch ({NODE_FACE_STITCH}) — character 1's refined faces would be lost")

    last_stitch = NODE_FACE_STITCH_2 if refine_character2 else NODE_FACE_STITCH
    sink = graph[NODE_VIDEO_COMBINE]["inputs"]["images"][0]
    if sink not in (last_stitch, NODE_RTX):
        problems.append(f"[{label}] the video reads node {sink}, not the last stitched frames")

    return problems


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
    parser.add_argument("--url", default="http://10.0.0.10:8188")
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

    # panels per character, and whether the item was queued with per-character refine passes
    # (a legacy item keeps the single whole-cast pass).
    for panels1, panels2, per_character in ((3, 3, True), (3, 0, True), (1, 1, True),
                                            (4, 2, True), (3, 3, False)):
        alias_list = all_aliases(panels1, panels2)
        uploaded = [f"panel_{i}.png" for i in range(len(alias_list))]

        for face_refine in (True, False):
            for rtx in (False, True):
                for tagged in ((True, False) if has_tagged else (False,)):
                    label = (f"{panels1}+{panels2} refs, "
                             f"{'per-character' if per_character else 'legacy'}, "
                             f"{'tagged' if tagged else 'numbered'}, "
                             f"{'refine' if face_refine else 'no refine'}, "
                             f"{'rtx' if rtx else 'no rtx'}")
                    graph = json.loads(json.dumps(source))
                    loaders = wire_reference_images(graph, uploaded)
                    if tagged:
                        graph = convert_to_tagged(graph, alias_list)
                    refine_character2 = face_refine and per_character and panels2 > 0
                    if face_refine:
                        graph = wire_refine_passes(graph, loaders, panels1, panels2,
                                                   per_character, refine_character2)
                    graph = wire_output_chain(graph, face_refine, refine_character2, rtx)
                    graph = prune_to_outputs(graph, [NODE_VIDEO_COMBINE])
                    problems = check_refine_structure(graph, face_refine, refine_character2,
                                                      panels1, panels2, per_character, label)

                    problems += validate(graph, object_info, label)
                    for problem in problems:
                        print(f"    ! {problem}")
                    failures += len(problems)

    print()
    print("OK" if failures == 0 else f"{failures} problem(s)")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
