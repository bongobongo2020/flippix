"""Replays H3 Cast Hybrid's submit-time graph patch offline and validates it against a live
ComfyUI /object_info.

The tab rewires h3-cast-hybrid.json on every submit: N LoadImage nodes — the keyframe stills
first, then the cast's sheet panels — wired into `ref_images.ref_image_N` on
MiniMaxH3ReferenceToVideo, then a tail that is wired per job (the face-refine pass, FILM ×2
interpolation, RTX ×2) and pruned by reachability. None of that is exercised until a render is
already under way, so this replays every combination and checks what ComfyUI would reject:

  * every class_type exists on the server;
  * every input written exists on that class (and every required input is supplied);
  * no link points at a node that was pruned, and no node is unreachable from the video sink;
  * combo inputs carry a legal value;
  * the mux frame rate follows the interpolation choice — a graph that interpolates and muxes at
    24 fps is a clip that plays at half speed, and nothing else would catch it;
  * each refine pass is conditioned on one character's panels only, numbered from ref_image_0,
    tracked by that character's face close-up and reading its own prompt primitive — sending a pass
    the keyframe stills, the other character's photographs, or node 10's keyframe-numbered prompt
    would have it redraw faces against a picture list it never received;
  * character 2's cloned pass reads the frames character 1's pass stitched, at both the tracker and
    the stitch, so the two edits compose instead of one discarding the other.

Usage:  python tools/verify_h3cast_hybrid.py [--url http://10.0.0.10:8188]
        python tools/verify_h3cast_hybrid.py --offline     # structure only, no server needed

Mirrors H3CastHybridViewModel.EnsureInputPrimitives / WireReferenceImages / WireOutputChain /
RtxSuperResolutionCompat.Normalize / PruneToOutputs. Keep the node ids and input names below in
step with those methods.

`--url` validation reports the face-refine classes as missing on a server without
ComfyUI-H3-FaceRefine and MiniMaxH3NativeAudioLock. That is a true finding about the server, not
about the graph: the refine-off rows are the ones that must pass there. Both packs are installed on
10.0.0.10 as of 2026-08-17 and every row passes against it.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys
import urllib.request

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

WORKFLOW = (pathlib.Path(__file__).resolve().parent.parent
            / "workflow" / "video" / "h3-minimax" / "h3-cast-hybrid.json")

NODE_PROMPT = "10"
NODE_REFINE_PROMPT = "15"
NODE_RESOLUTION = "11"
NODE_DURATION = "12"
NODE_FRAMES = "13"
NODE_SEED = "14"
NODE_REFERENCE = "20"
NODE_SCHEDULER = "22"
NODE_BASE_FRAMES = "30"
NODE_INTERPOLATE = "33"
NODE_RTX = "34"
NODE_FPS = "35"
NODE_FPS_DOUBLED = "36"
NODE_CREATE_VIDEO = "37"
NODE_SAVE_VIDEO = "38"
NODE_REF_IMAGE_1 = "40"

NODE_FACE_TRACK = "100"
NODE_REFINE_REFERENCE = "101"
NODE_AUDIO_LOCK = "103"
NODE_REFINE_DENOISE = "106"
NODE_REFINE_SEED = "108"
NODE_FACE_STITCH = "111"

# Character 2's pass — the 100-block cloned into the 200s at submit time.
REFINE_PASS_2_OFFSET = 100
NODE_REFINE_PROMPT_2 = "215"
NODE_FACE_TRACK_2 = "200"
NODE_REFINE_REFERENCE_2 = "201"
NODE_FACE_STITCH_2 = "211"

REF_IMAGE_PREFIX = "ref_images.ref_image_"
REFERENCE_NODE_ID_BASE = 900
MAX_REFERENCE_IMAGES = 9

# Widgets a workflow export carries that /object_info does not declare, because the node expands
# them from another widget's value in the browser. ComfyUI ignores undeclared inputs at validation,
# and these ship in the workflow file rather than being written by the tab.
UNDECLARED_BUT_HARMLESS = {
    ("SaveVideo", "codec.encoding"),
    ("SaveVideo", "codec.encoding.crf"),
    ("SaveVideo", "video-preview"),
    ("LTX_lora_loader", "lora_ui"),
    ("ComfyMathExpression", "values.a"),
    # Both RTX signatures are written on every submit, so whichever pack the server has, the other
    # version's widgets are the undeclared ones. See normalize_rtx.
    ("RTXVideoSuperResolution", "resize_type.scale"),
    ("RTXVideoSuperResolution", "scale"),
    ("RTXVideoSuperResolution", "deblur"),
}


def ensure_input_primitives(graph: dict) -> dict:
    """Mirrors EnsureInputPrimitives: each reference node reads its prompt, canvas and frame count
    from the input primitives rather than from widget values baked in by an export. The refine
    pass's node keeps its own width/height — they come from the face-crop canvas."""
    inputs = graph[NODE_REFERENCE]["inputs"]
    inputs["prompt"] = [NODE_PROMPT, 0]
    inputs["width"] = [NODE_RESOLUTION, 0]
    inputs["height"] = [NODE_RESOLUTION, 1]
    inputs["length"] = [NODE_FRAMES, 1]

    refine = graph[NODE_REFINE_REFERENCE]["inputs"]
    refine["prompt"] = [NODE_REFINE_PROMPT, 0]
    refine["length"] = [NODE_FRAMES, 1]
    return graph


def wire_reference_images(graph: dict, uploaded: list[str]) -> list[str]:
    assert 0 < len(uploaded) <= MAX_REFERENCE_IMAGES, f"{len(uploaded)} references"

    loaders = []
    for i, name in enumerate(uploaded):
        node_id = NODE_REF_IMAGE_1 if i == 0 else str(REFERENCE_NODE_ID_BASE + i)
        graph[node_id] = {"inputs": {"image": name}, "class_type": "LoadImage",
                          "_meta": {"title": f"Ref Image {i + 1}"}}
        loaders.append(node_id)

    attach(graph, NODE_REFERENCE, loaders)
    return loaders


def add_second_refine_pass(graph: dict) -> dict:
    """Mirrors AddSecondRefinePass: nodes 100-111 and the refine prompt cloned into the 200s, every
    link inside the clone remapped to the clone, and the two links that read the base decode moved
    onto the first pass's stitched output so the second edit lands on top of the first."""
    mapping = {NODE_REFINE_PROMPT: NODE_REFINE_PROMPT_2}
    mapping.update({node_id: str(int(node_id) + REFINE_PASS_2_OFFSET)
                    for node_id in graph if node_id.isdigit() and 100 <= int(node_id) <= 111})

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


def wire_refine_passes(graph: dict, loaders: list[str], cast_start: int,
                       panels1: int, face1: int, panels2: int, face2: int,
                       per_character: bool, refine_character2: bool) -> dict:
    """Mirrors WireRefinePasses. Each character's pass sees only their own panels, renumbered from
    ref_image_0 — the numbering their refine prompt was written for — and tracks them by their own
    face close-up, which is the only way two people in one frame can be told apart."""
    cast = loaders[cast_start:]
    loaders1, loaders2 = cast[:panels1], cast[panels1:panels1 + panels2]

    attach(graph, NODE_REFINE_REFERENCE, loaders1 if per_character and loaders1 else cast)
    if per_character and loaders1:
        graph[NODE_FACE_TRACK]["inputs"]["identity_reference"] = [loaders1[min(face1, len(loaders1) - 1)], 0]

    if refine_character2 and loaders2:
        graph = add_second_refine_pass(graph)
        attach(graph, NODE_REFINE_REFERENCE_2, loaders2)
        graph[NODE_FACE_TRACK_2]["inputs"]["identity_reference"] = [loaders2[min(face2, len(loaders2) - 1)], 0]
    return graph


def attach(graph: dict, node_id: str, loaders: list[str]) -> None:
    inputs = graph[node_id]["inputs"]
    for key in [k for k in inputs if k.startswith(REF_IMAGE_PREFIX)]:
        del inputs[key]
    for i, loader in enumerate(loaders):
        inputs[f"{REF_IMAGE_PREFIX}{i}"] = [loader, 0]


def wire_output_chain(graph: dict, face_refine: bool, refine_character2: bool,
                      interpolate: bool, rtx: bool) -> dict:
    rendered = NODE_BASE_FRAMES
    if face_refine:
        rendered = NODE_FACE_STITCH_2 if refine_character2 else NODE_FACE_STITCH
    frames = NODE_INTERPOLATE if interpolate else rendered
    graph[NODE_INTERPOLATE]["inputs"]["images"] = [rendered, 0]
    graph[NODE_RTX]["inputs"]["images"] = [frames, 0]
    graph[NODE_CREATE_VIDEO]["inputs"]["images"] = [NODE_RTX if rtx else frames, 0]
    graph[NODE_CREATE_VIDEO]["inputs"]["fps"] = [NODE_FPS_DOUBLED if interpolate else NODE_FPS, 0]
    return graph


def normalize_rtx(graph: dict) -> dict:
    """Mirrors RtxSuperResolutionCompat.Normalize, which the tab runs on every submit.

    The node's signature changed: `scale`/`quality`/`deblur` became `resize_type` (a dynamic combo
    supplying `resize_type.scale`) plus `quality`. Writing the union is correct on either pack —
    each version reads the widgets it declares and ignores the rest — and skipping it here is why
    this script used to report a missing `resize_type` on a graph that submits perfectly well.
    """
    for node in graph.values():
        if node["class_type"] != "RTXVideoSuperResolution":
            continue
        inputs = node["inputs"]
        if inputs.get("resize_type") == "target dimensions":
            continue
        scale = inputs.get("resize_type.scale", inputs.get("scale", 2.0))
        inputs["resize_type"] = "scale by multiplier"
        inputs["resize_type.scale"] = scale
        inputs["scale"] = scale
        inputs.setdefault("deblur", "MEDIUM")
        inputs.setdefault("quality", "ULTRA")
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


def check_structure(graph: dict, face_refine: bool, interpolate: bool, rtx: bool,
                    references: int, panels1: int, panels2: int,
                    per_character: bool, refine_character2: bool) -> list[str]:
    """Everything that can be checked without a server: link integrity, reachability, and the
    invariants the tab itself depends on."""
    problems = []

    for node_id, node in graph.items():
        for key, value in node["inputs"].items():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                if value[0] not in graph:
                    problems.append(f"{node_id} ({node['class_type']}).{key} -> missing node {value[0]}")

    unreachable = set(graph) - set(prune_to_outputs(dict(graph), [NODE_SAVE_VIDEO]))
    for node_id in sorted(unreachable):
        problems.append(f"{node_id} ({graph[node_id]['class_type']}): unreachable from the video sink")

    # The pruned-away branches really are gone, not merely unhooked: FrameInterpolate and
    # RTXVideoSuperResolution both cost a whole extra frame stack if they run.
    if not interpolate and NODE_INTERPOLATE in graph:
        problems.append("interpolation is off but FrameInterpolate survived the prune")
    if not rtx and NODE_RTX in graph:
        problems.append("RTX is off but RTXVideoSuperResolution survived the prune")
    if interpolate and NODE_INTERPOLATE not in graph:
        problems.append("interpolation is on but FrameInterpolate was pruned")
    if rtx and NODE_RTX not in graph:
        problems.append("RTX is on but RTXVideoSuperResolution was pruned")

    # The refine pass is a whole second diffusion over a full-length crop stack; "unhooked" is not
    # good enough, and its H3FaceTrackCrop would run on its own.
    refine_nodes = (NODE_FACE_TRACK, NODE_REFINE_REFERENCE, NODE_AUDIO_LOCK, NODE_REFINE_DENOISE,
                    NODE_REFINE_SEED, NODE_FACE_STITCH, NODE_REFINE_PROMPT)
    for node_id in refine_nodes:
        if not face_refine and node_id in graph:
            problems.append(f"face refine is off but {node_id} "
                            f"({graph[node_id]['class_type']}) survived the prune")
        if face_refine and node_id not in graph:
            problems.append(f"face refine is on but node {node_id} was pruned")

    # Character 2's cloned pass: present exactly when this clip refines them, and never left behind.
    pass2_nodes = (NODE_FACE_TRACK_2, NODE_REFINE_REFERENCE_2, NODE_FACE_STITCH_2, NODE_REFINE_PROMPT_2)
    for node_id in pass2_nodes:
        if refine_character2 and node_id not in graph:
            problems.append(f"character 2's refine pass is on but node {node_id} is missing")
        if not refine_character2 and node_id in graph:
            problems.append(f"character 2's refine pass is off but {node_id} is in the graph")

    if face_refine:
        # Each pass sees one character's panels, numbered from zero: their refine prompt numbers them
        # from <Picture 1>, so a keyframe still — or the other character — in slot 0 is a face redrawn
        # from the wrong photograph.
        expected1 = panels1 if per_character else panels1 + panels2
        wired = sum(1 for k in graph[NODE_REFINE_REFERENCE]["inputs"]
                    if k.startswith(REF_IMAGE_PREFIX))
        if wired != expected1:
            problems.append(f"refine pass 1 has {wired} ref_image slot(s), expected {expected1} panel(s)")

        prompt_link = graph[NODE_REFINE_REFERENCE]["inputs"].get("prompt")
        if not (isinstance(prompt_link, list) and prompt_link[0] == NODE_REFINE_PROMPT):
            problems.append("the refine pass reads the base prompt, not its own cast-only primitive")

        identity = graph[NODE_FACE_TRACK]["inputs"].get("identity_reference")
        if per_character and not isinstance(identity, list):
            problems.append("the tracker has no identity_reference: it follows whoever is largest, "
                            "which in a two-character clip is not reliably the same person")
        if per_character and isinstance(identity, list) and identity[0] not in graph:
            problems.append(f"the tracker's identity_reference points at pruned node {identity[0]}")

        if refine_character2:
            wired2 = sum(1 for k in graph[NODE_REFINE_REFERENCE_2]["inputs"]
                         if k.startswith(REF_IMAGE_PREFIX))
            if wired2 != panels2:
                problems.append(f"refine pass 2 has {wired2} ref_image slot(s), expected {panels2}")
            prompt2 = graph[NODE_REFINE_REFERENCE_2]["inputs"].get("prompt")
            if not (isinstance(prompt2, list) and prompt2[0] == NODE_REFINE_PROMPT_2):
                problems.append("character 2's pass does not read its own prompt primitive")
            # Composing rather than competing: pass 2 reads what pass 1 stitched, both for the frames
            # it tracks and for the frames it pastes back into.
            for node_id, key in ((NODE_FACE_TRACK_2, "images"), (NODE_FACE_STITCH_2, "base_images")):
                link = graph[node_id]["inputs"].get(key)
                if not (isinstance(link, list) and link[0] == NODE_FACE_STITCH):
                    problems.append(f"{node_id}.{key} reads {link}, expected the first pass's stitch "
                                    f"({NODE_FACE_STITCH}) — character 1's refined faces would be discarded")

        # The whole point of the pass: the stitched frames, not the raw decode, reach the file.
        last_stitch = NODE_FACE_STITCH_2 if refine_character2 else NODE_FACE_STITCH
        tail = NODE_INTERPOLATE if interpolate else last_stitch
        if interpolate and graph[NODE_INTERPOLATE]["inputs"]["images"][0] != last_stitch:
            problems.append("FrameInterpolate does not read the last stitched frames")
        expected = NODE_RTX if rtx else tail
        if graph[NODE_CREATE_VIDEO]["inputs"]["images"][0] != expected:
            problems.append(f"CreateVideo reads node {graph[NODE_CREATE_VIDEO]['inputs']['images'][0]}, "
                            f"expected {expected}")

    # A clip interpolated to 2× the frames and muxed at 1× the rate plays at half speed, and it
    # looks like a model problem rather than a wiring one.
    fps_source = graph[NODE_CREATE_VIDEO]["inputs"]["fps"][0]
    expected_fps = NODE_FPS_DOUBLED if interpolate else NODE_FPS
    if fps_source != expected_fps:
        problems.append(f"CreateVideo.fps reads node {fps_source}, expected {expected_fps}")

    wired = sum(1 for k in graph[NODE_REFERENCE]["inputs"] if k.startswith(REF_IMAGE_PREFIX))
    if wired != references:
        problems.append(f"{wired} ref_image slots wired, expected {references}")

    # The reference node must read the primitives, not a baked-in widget value: a literal here is
    # the export's demo prompt or canvas reaching the GPU.
    for key, source in (("prompt", NODE_PROMPT), ("width", NODE_RESOLUTION),
                        ("height", NODE_RESOLUTION), ("length", NODE_FRAMES)):
        value = graph[NODE_REFERENCE]["inputs"].get(key)
        if not (isinstance(value, list) and value[0] == source):
            problems.append(f"MiniMaxH3ReferenceToVideo.{key} is not linked to node {source}")

    return problems


def validate_against_server(graph: dict, object_info: dict) -> list[str]:
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

        for key, value in node["inputs"].items():
            # Autogrow inputs (`ref_images.ref_image_0`) are declared under their group name.
            base = key.split(".")[0]
            if key not in known and base not in known:
                if (class_type, key) not in UNDECLARED_BUT_HARMLESS:
                    problems.append(f"{node_id} ({class_type}): no input '{key}'")
                continue
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                continue
            # LoadImage.image enumerates files already sitting in ComfyUI/input; the tab uploads
            # its pictures at submit time, so their absence here means nothing.
            if (class_type, key) == ("LoadImage", "image"):
                continue
            declared = known.get(key)
            if declared and isinstance(declared[0], list) and value not in declared[0]:
                problems.append(f"{node_id} ({class_type}).{key} = {value!r} not in {declared[0]}")

        for key in required:
            if key in node["inputs"] or any(k.split(".")[0] == key for k in node["inputs"]):
                continue
            problems.append(f"{node_id} ({class_type}): required input '{key}' is not supplied")

    return problems


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://10.0.0.10:8188")
    parser.add_argument("--offline", action="store_true",
                        help="skip /object_info and check structure only")
    args = parser.parse_args()

    object_info = {}
    if not args.offline:
        try:
            with urllib.request.urlopen(f"{args.url}/object_info", timeout=60) as response:
                object_info = json.load(response)
            print(f"/object_info: {len(object_info)} node classes from {args.url}")
        except Exception as exc:  # noqa: BLE001 - the point is to carry on without a server
            print(f"could not reach {args.url} ({exc}) — falling back to --offline")
            args.offline = True

    source = json.loads(WORKFLOW.read_text(encoding="utf-8"))
    print(f"{WORKFLOW.name}: {len(source)} nodes")
    failures = 0

    # keyframes, character 1's panels, character 2's panels, and whether the item was queued with a
    # per-view cast (a legacy item keeps one whole-cast refine pass).
    cases = (
        (1, 2, 2, True),    # the default budget: front + face each
        (0, 2, 2, True),    # a story clip after the first
        (3, 3, 0, True),    # one character, every panel, three locks
        (0, 3, 3, True),    # two characters, every panel — the nine-slot ceiling
        (2, 1, 1, True),    # whole sheets
        (0, 3, 3, False),   # legacy: one pass over the whole cast, no identity reference
    )
    for keyframes, panels1, panels2, per_character in cases:
        panels = panels1 + panels2
        references = keyframes + panels
        uploaded = ([f"key_{i}.png" for i in range(keyframes)]
                    + [f"panel_{i}.png" for i in range(panels)])

        for face_refine in (True, False):
            for interpolate in (True, False):
                for rtx in (False, True):
                    refine_character2 = face_refine and per_character and panels2 > 0
                    label = (f"{keyframes} keyframe(s) + {panels1}+{panels2} panel(s) = {references} refs, "
                             f"{'per-character' if per_character else 'legacy'}, "
                             f"{'refine' if face_refine else 'no refine'}"
                             f"{' ×2' if refine_character2 else ''}, "
                             f"{'FILM' if interpolate else 'no FILM'}, {'rtx' if rtx else 'no rtx'}")
                    graph = json.loads(json.dumps(source))
                    graph = ensure_input_primitives(graph)
                    loaders = wire_reference_images(graph, uploaded)
                    if face_refine:
                        graph = wire_refine_passes(graph, loaders, keyframes, panels1, panels1 - 1,
                                                   panels2, panels2 - 1, per_character,
                                                   refine_character2)
                    graph = wire_output_chain(graph, face_refine, refine_character2, interpolate, rtx)
                    graph = normalize_rtx(graph)
                    graph = prune_to_outputs(graph, [NODE_SAVE_VIDEO])

                    problems = check_structure(graph, face_refine, interpolate, rtx, references,
                                               panels1, panels2, per_character, refine_character2)
                    if not args.offline:
                        problems += validate_against_server(graph, object_info)

                    print(f"  {label}: {len(graph)} nodes, {len(problems)} problem(s)")
                    for problem in problems:
                        print(f"    ! {problem}")
                    failures += len(problems)

    print()
    print("OK" if failures == 0 else f"{failures} problem(s)")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
