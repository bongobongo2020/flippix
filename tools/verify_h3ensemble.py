"""Replays H3 Ensemble's submit-time graph patch offline and validates it against a live ComfyUI
/object_info.

The 🎬🎭 H3 Ensemble tab renders on the same `h3-cast-hybrid.json` as the 🪪👥⚡ Hybrid tab, but the
patch it applies is bigger in two ways, and neither is exercised until a render is already under way:

  * the reference list is **keyframes, then the cast a clip actually names, then the location** — up
    to five characters sharing nine slots with a set photograph, so which picture is which moves
    from clip to clip;
  * the face-refine chain is cloned **once per character in the clip** rather than once for a second
    character. Pass k lives in the 100·k block (200-211/215, 300-311/315, …), reads the frames pass
    k-1 stitched, is conditioned on that character's panels alone and reads its own prompt primitive.

So this replays every combination and checks what ComfyUI would reject, plus the invariants the tab
itself depends on:

  * every class_type exists on the server, every input written exists on that class, and every
    required input is supplied;
  * no link points at a node that was pruned, and no node is unreachable from the video sink;
  * the mux frame rate follows the interpolation choice — a graph that interpolates and muxes at
    24 fps is a clip that plays at half speed, and nothing else would catch it;
  * each refine pass is conditioned on exactly one character's panels, numbered from ref_image_0,
    tracked by that character's face close-up, reading its own prompt primitive — a pass shown the
    keyframes, the location or another character's photographs redraws faces against a picture list
    it never received;
  * every pass after the first reads the previous pass's stitch at BOTH the tracker and the stitch,
    so N edits compose instead of the last one discarding the other N-1;
  * the location, when wired, is the LAST reference slot — the numbering the prompt was written for.

Usage:  python tools/verify_h3ensemble.py [--url http://10.0.0.10:8188]
        python tools/verify_h3ensemble.py --offline     # structure only, no server needed

Mirrors H3EnsembleViewModel.EnsureInputPrimitives / WireReferenceImages / WireRefinePasses /
AddRefinePass / WireOutputChain / RtxSuperResolutionCompat.Normalize / PruneToOutputs. Keep the node
ids and input names below in step with those methods.

`--url` validation reports the face-refine classes as missing on a server without
ComfyUI-H3-FaceRefine and MiniMaxH3NativeAudioLock. That is a true finding about the server, not
about the graph: the refine-off rows are the ones that must pass there.
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
NODE_BASE_FRAMES = "30"
NODE_INTERPOLATE = "33"
NODE_RTX = "34"
NODE_FPS = "35"
NODE_FPS_DOUBLED = "36"
NODE_CREATE_VIDEO = "37"
NODE_SAVE_VIDEO = "38"
NODE_REF_IMAGE_1 = "40"

# The refine block as shipped. Pass k >= 2 is this block shifted into the 100·k range.
REFINE_BLOCK_FIRST = 100
REFINE_BLOCK_LAST = 111
NODE_FACE_TRACK = "100"
NODE_REFINE_REFERENCE = "101"
NODE_AUDIO_LOCK = "103"
NODE_REFINE_DENOISE = "106"
NODE_REFINE_SEED = "108"
NODE_FACE_STITCH = "111"
REFINE_PROMPT_ID = 15

REF_IMAGE_PREFIX = "ref_images.ref_image_"
REFERENCE_NODE_ID_BASE = 900
MAX_REFERENCE_IMAGES = 9

STILL_PICK_ID_BASE = 800
STILL_SAVE_ID_BASE = 810

# Widgets a workflow export carries that /object_info does not declare, because the node expands
# them from another widget's value in the browser. ComfyUI ignores undeclared inputs at validation.
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


def pass_node(pass_index: int, base_id: int) -> str:
    """Mirrors PassNode: pass 1 is the block as shipped (15, 100-111); pass k is that block shifted
    into the 100·k range, so pass 2 is 215 and 200-211, pass 3 is 315 and 300-311."""
    if base_id == REFINE_PROMPT_ID:
        return str(REFINE_PROMPT_ID if pass_index <= 1 else 100 * pass_index + REFINE_PROMPT_ID)
    return str(base_id if pass_index <= 1 else base_id + 100 * (pass_index - 1))


def ensure_input_primitives(graph: dict) -> dict:
    """Mirrors EnsureInputPrimitives: the reference node reads its prompt, canvas and frame count
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


def add_refine_pass(graph: dict, pass_index: int) -> dict:
    """Mirrors AddRefinePass: the refine block and its prompt cloned into pass `pass_index`'s own
    100·k range, every link inside the clone remapped to the clone, and the two links that read the
    base decode moved onto the PREVIOUS pass's stitched output so this edit lands on top of that one
    rather than discarding it."""
    previous_stitch = pass_node(pass_index - 1, REFINE_BLOCK_LAST)
    mapping = {NODE_REFINE_PROMPT: pass_node(pass_index, REFINE_PROMPT_ID)}
    mapping.update({
        node_id: pass_node(pass_index, int(node_id))
        for node_id in list(graph)
        if node_id.isdigit() and REFINE_BLOCK_FIRST <= int(node_id) <= REFINE_BLOCK_LAST
    })

    for source, clone in mapping.items():
        node = json.loads(json.dumps(graph[source]))
        for key, value in node["inputs"].items():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                target = mapping.get(value[0],
                                     previous_stitch if value[0] == NODE_BASE_FRAMES else value[0])
                node["inputs"][key] = [target, value[1]]
        title = node.get("_meta", {}).get("title", clone)
        node["_meta"] = {"title": f"{title} (refine pass {pass_index})"}
        graph[clone] = node
    return graph


def wire_refine_passes(graph: dict, loaders: list[str], passes: list[tuple[int, int, int]]) -> dict:
    """Mirrors WireRefinePasses. `passes` is (loader_start, loader_count, face_panel) per character,
    in cast order. Each pass sees only that character's panels, renumbered from ref_image_0 — the
    numbering their refine prompt was written for — and tracks them by their own face close-up."""
    for i, (start, count, face) in enumerate(passes):
        index = i + 1
        own = loaders[start:start + count]
        assert own, f"pass {index} has no panel to condition on"

        if index > 1:
            graph = add_refine_pass(graph, index)

        attach(graph, pass_node(index, 101), own)
        graph[pass_node(index, 100)]["inputs"]["identity_reference"] = [own[min(face, len(own) - 1)], 0]
    return graph


def attach(graph: dict, node_id: str, loaders: list[str]) -> None:
    inputs = graph[node_id]["inputs"]
    for key in [k for k in inputs if k.startswith(REF_IMAGE_PREFIX)]:
        del inputs[key]
    for i, loader in enumerate(loaders):
        inputs[f"{REF_IMAGE_PREFIX}{i}"] = [loader, 0]


def wire_output_chain(graph: dict, refine_passes: int, interpolate: bool, rtx: bool) -> dict:
    rendered = pass_node(refine_passes, REFINE_BLOCK_LAST) if refine_passes else NODE_BASE_FRAMES
    frames = NODE_INTERPOLATE if interpolate else rendered
    graph[NODE_INTERPOLATE]["inputs"]["images"] = [rendered, 0]
    graph[NODE_RTX]["inputs"]["images"] = [frames, 0]
    graph[NODE_CREATE_VIDEO]["inputs"]["images"] = [NODE_RTX if rtx else frames, 0]
    graph[NODE_CREATE_VIDEO]["inputs"]["fps"] = [NODE_FPS_DOUBLED if interpolate else NODE_FPS, 0]
    return graph


def normalize_rtx(graph: dict) -> dict:
    """Mirrors RtxSuperResolutionCompat.Normalize: the node's signature changed, and writing the
    union of both is correct on either pack — each version reads the widgets it declares."""
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


def wire_still_outputs(graph: dict, frames: int, run_token: str) -> list[tuple[str, int]]:
    """Mirrors WireStillOutputs: one ImageFromBatch + SaveImage pair per frame worth keeping."""
    indices = [0] + ([frames // 2, frames - 1] if frames >= 22 else [])
    saves = []
    for i, index in enumerate(indices):
        pick, save = str(STILL_PICK_ID_BASE + i), str(STILL_SAVE_ID_BASE + i)
        graph[pick] = {
            "inputs": {"image": [NODE_BASE_FRAMES, 0], "batch_index": index, "length": 1},
            "class_type": "ImageFromBatch",
            "_meta": {"title": f"Storyboard frame {index}"},
        }
        graph[save] = {
            "inputs": {"images": [pick, 0], "filename_prefix": f"h3_ensemble/{run_token}_f{index:03d}"},
            "class_type": "SaveImage",
            "_meta": {"title": f"Storyboard save {index}"},
        }
        saves.append((save, index))
    return saves


def frames_for_seconds(seconds: float) -> int:
    """Mirrors node 13's expression: 24 fps snapped onto the model's 17k+5 frame grid."""
    frames = max(5, round(seconds * 24))
    return frames + (5 - frames % 17 + 17) % 17


def check_links(graph: dict) -> list[str]:
    return [f"{node_id} ({node['class_type']}).{key} -> missing node {value[0]}"
            for node_id, node in graph.items()
            for key, value in node["inputs"].items()
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str)
            and value[0] not in graph]


def check_structure(graph: dict, interpolate: bool, rtx: bool, references: int,
                    panel_counts: list[int], passes: list[tuple[int, int, int]],
                    loaders: list[str], has_environment: bool) -> list[str]:
    """Everything checkable without a server: link integrity, reachability, and the invariants the
    tab itself depends on."""
    problems = check_links(graph)

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

    face_refine = bool(passes)

    # The refine chain is a whole second diffusion over a full-length crop stack per character;
    # "unhooked" is not good enough, and H3FaceTrackCrop would run on its own.
    for node_id in (NODE_FACE_TRACK, NODE_REFINE_REFERENCE, NODE_AUDIO_LOCK, NODE_REFINE_DENOISE,
                    NODE_REFINE_SEED, NODE_FACE_STITCH, NODE_REFINE_PROMPT):
        if not face_refine and node_id in graph:
            problems.append(f"face refine is off but {node_id} "
                            f"({graph[node_id]['class_type']}) survived the prune")
        if face_refine and node_id not in graph:
            problems.append(f"face refine is on but node {node_id} was pruned")

    # No pass beyond the ones this clip asked for may be left behind — a stray 300-block is a whole
    # extra diffusion over the clip.
    for extra in range(len(passes) + 1, 6):
        for base in (100, 101, 111, REFINE_PROMPT_ID):
            node_id = pass_node(extra, base)
            if node_id in graph:
                problems.append(f"only {len(passes)} refine pass(es) were asked for but {node_id} "
                                f"({graph[node_id]['class_type']}) is in the graph")

    for i, (start, count, face) in enumerate(passes):
        index = i + 1
        ref_node = pass_node(index, 101)
        track_node = pass_node(index, 100)
        prompt_node = pass_node(index, REFINE_PROMPT_ID)
        own = loaders[start:start + count]

        for node_id in (ref_node, track_node, prompt_node, pass_node(index, REFINE_BLOCK_LAST)):
            if node_id not in graph:
                problems.append(f"refine pass {index}: node {node_id} is missing")
        if any(n not in graph for n in (ref_node, track_node, prompt_node)):
            continue

        # Each pass sees ONE character's panels, numbered from zero: their refine prompt numbers them
        # from <Picture 1>, so a keyframe still, the location, or another character in slot 0 is a
        # face redrawn from the wrong photograph.
        wired = [v[0] for k, v in graph[ref_node]["inputs"].items() if k.startswith(REF_IMAGE_PREFIX)]
        if wired != own:
            problems.append(f"refine pass {index} is conditioned on {wired}, expected {own}")
        if has_environment and loaders[-1] in wired:
            problems.append(f"refine pass {index} was sent the location photograph")

        prompt_link = graph[ref_node]["inputs"].get("prompt")
        if not (isinstance(prompt_link, list) and prompt_link[0] == prompt_node):
            problems.append(f"refine pass {index} reads prompt {prompt_link}, expected its own "
                            f"primitive {prompt_node}")

        identity = graph[track_node]["inputs"].get("identity_reference")
        if not isinstance(identity, list):
            problems.append(f"refine pass {index}'s tracker has no identity_reference: it follows "
                            "whoever is largest, which in a multi-character clip is not reliably "
                            "the same person")
        elif identity[0] != own[min(face, len(own) - 1)]:
            problems.append(f"refine pass {index} tracks {identity[0]}, expected that character's "
                            f"face close-up {own[min(face, len(own) - 1)]}")

        # Composing rather than competing: every pass after the first reads what the one before it
        # stitched, both for the frames it tracks and for the frames it pastes back into.
        expected_source = NODE_BASE_FRAMES if index == 1 else pass_node(index - 1, REFINE_BLOCK_LAST)
        for node_id, key in ((track_node, "images"),
                             (pass_node(index, REFINE_BLOCK_LAST), "base_images")):
            link = graph.get(node_id, {}).get("inputs", {}).get(key)
            if not (isinstance(link, list) and link[0] == expected_source):
                problems.append(f"{node_id}.{key} reads {link}, expected {expected_source} — "
                                f"pass {index - 1}'s refined faces would be discarded")

    if face_refine:
        # The whole point of the passes: the LAST stitched frames, not the raw decode, reach the file.
        last_stitch = pass_node(len(passes), REFINE_BLOCK_LAST)
        tail = NODE_INTERPOLATE if interpolate else last_stitch
        if interpolate and graph[NODE_INTERPOLATE]["inputs"]["images"][0] != last_stitch:
            problems.append("FrameInterpolate does not read the last stitched frames")
        expected = NODE_RTX if rtx else tail
        if graph[NODE_CREATE_VIDEO]["inputs"]["images"][0] != expected:
            problems.append(f"CreateVideo reads node {graph[NODE_CREATE_VIDEO]['inputs']['images'][0]}, "
                            f"expected {expected}")

    # A clip interpolated to 2× the frames and muxed at 1× the rate plays at half speed, and it looks
    # like a model problem rather than a wiring one.
    fps_source = graph[NODE_CREATE_VIDEO]["inputs"]["fps"][0]
    expected_fps = NODE_FPS_DOUBLED if interpolate else NODE_FPS
    if fps_source != expected_fps:
        problems.append(f"CreateVideo.fps reads node {fps_source}, expected {expected_fps}")

    wired = [v[0] for k, v in graph[NODE_REFERENCE]["inputs"].items() if k.startswith(REF_IMAGE_PREFIX)]
    if wired != loaders:
        problems.append(f"the reference node is wired {wired}, expected {loaders}")
    if len(wired) != references:
        problems.append(f"{len(wired)} ref_image slots wired, expected {references}")
    if len(wired) > MAX_REFERENCE_IMAGES:
        problems.append(f"{len(wired)} references exceeds MiniMaxH3ReferenceToVideo's {MAX_REFERENCE_IMAGES}")

    # The location is the LAST slot. The prompt's code-written sections name it by that number in
    # three places, so wiring it anywhere else points them at a character.
    if has_environment and wired and wired[-1] != loaders[-1]:
        problems.append("the location is not the last reference slot")

    # The reference node must read the primitives, not a baked-in widget value: a literal here is the
    # export's demo prompt or canvas reaching the GPU.
    for key, source in (("prompt", NODE_PROMPT), ("width", NODE_RESOLUTION),
                        ("height", NODE_RESOLUTION), ("length", NODE_FRAMES)):
        value = graph[NODE_REFERENCE]["inputs"].get(key)
        if not (isinstance(value, list) and value[0] == source):
            problems.append(f"MiniMaxH3ReferenceToVideo.{key} is not linked to node {source}")

    return problems


def check_storyboard_structure(graph: dict, frames: int, references: int,
                               saves: list[tuple[str, int]]) -> list[str]:
    """The storyboard pass's own invariants: the clip's graph with the frame count written as a
    literal and everything past the decode replaced by image sinks."""
    problems = check_links(graph)

    length = graph[NODE_REFERENCE]["inputs"].get("length")
    if length != frames:
        problems.append(f"reference length is {length!r}, not the literal {frames} the pass asks for")
    if frames < 5 or (frames - 5) % 17:
        problems.append(f"{frames} frames is off MiniMaxH3ReferenceToVideo's 17k+5 grid")

    wired = sum(1 for k in graph[NODE_REFERENCE]["inputs"] if k.startswith(REF_IMAGE_PREFIX))
    if wired != references:
        problems.append(f"{wired} reference image(s) wired, expected {references}")

    for node_id, _ in saves:
        if node_id not in graph:
            problems.append(f"save node {node_id} was pruned away")

    # Everything that makes a clip expensive has to be gone: this is meant to be a still.
    for gone in (NODE_SAVE_VIDEO, NODE_CREATE_VIDEO, NODE_INTERPOLATE, NODE_RTX,
                 NODE_FACE_TRACK, NODE_REFINE_REFERENCE, NODE_FACE_STITCH):
        if gone in graph:
            problems.append(f"node {gone} ({graph[gone]['class_type']}) survived the prune to stills")

    # The seconds→frames primitives do survive: node 6's sampler preview reads them, so the pass sets
    # the duration to match the literal rather than leaving a preview sized for a clip nobody rendered.
    seconds = graph.get(NODE_DURATION, {}).get("inputs", {}).get("value")
    if seconds is not None and frames_for_seconds(seconds) != frames:
        problems.append(f"duration {seconds!r}s derives {frames_for_seconds(seconds)} frames, "
                        f"not the {frames} the reference node is rendering")

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
            # LoadImage.image enumerates files already sitting in ComfyUI/input; the tab uploads its
            # pictures at submit time, so their absence here means nothing.
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

    # (keyframes, per-character panel counts in cast order, location wired). Each row is a shape the
    # tab can actually produce — the Auto budget divides the free slots between the cast, so a
    # five-hander is five singles and a two-hander is three panels each.
    cases = (
        (1, [1, 1, 1, 1, 1], True),   # five characters, Auto → one face each, plus the set
        (0, [1, 1, 1, 1, 1], True),   # the same clip with no lock at all
        (1, [3, 3], True),            # a two-hander at the ceiling: 1 + 6 + 1 = 8
        (1, [2, 2, 2], True),         # three characters, front + face each: 1 + 6 + 1 = 8
        (0, [3], True),               # a solo, every panel, in a location
        (2, [1, 1], False),           # two hand-placed locks, no location
        (0, [1, 1, 1, 1], True),      # four singles — the crowded clip the advisory warns about
        (0, [3, 3, 3], False),        # three at every panel, no location: the nine-slot ceiling
    )

    for keyframes, panel_counts, has_environment in cases:
        cast_panels = sum(panel_counts)
        references = keyframes + cast_panels + (1 if has_environment else 0)
        uploaded = ([f"key_{i}.png" for i in range(keyframes)]
                    + [f"panel_{i}.png" for i in range(cast_panels)]
                    + (["location.png"] if has_environment else []))

        # Where each character's panels start in the loader list, and which of them is their face.
        passes_full, cursor = [], keyframes
        for count in panel_counts:
            passes_full.append((cursor, count, count - 1))
            cursor += count

        for face_refine in (True, False):
            for interpolate in (True, False):
                for rtx in (False, True):
                    passes = passes_full if face_refine else []
                    label = (f"{keyframes} lock(s) + {'+'.join(map(str, panel_counts))} panel(s)"
                             f"{' + set' if has_environment else ''} = {references} refs, "
                             f"{'refine ×' + str(len(passes)) if face_refine else 'no refine'}, "
                             f"{'FILM' if interpolate else 'no FILM'}, {'rtx' if rtx else 'no rtx'}")

                    graph = json.loads(json.dumps(source))
                    graph = ensure_input_primitives(graph)
                    loaders = wire_reference_images(graph, uploaded)
                    if face_refine:
                        graph = wire_refine_passes(graph, loaders, passes)
                    graph = wire_output_chain(graph, len(passes), interpolate, rtx)
                    graph = normalize_rtx(graph)
                    graph = prune_to_outputs(graph, [NODE_SAVE_VIDEO])

                    problems = check_structure(graph, interpolate, rtx, references, panel_counts,
                                               passes, loaders, has_environment)
                    if not args.offline:
                        problems += validate_against_server(graph, object_info)

                    print(f"  {label}: {len(graph)} nodes, {len(problems)} problem(s)")
                    for problem in problems:
                        print(f"    ! {problem}")
                    failures += len(problems)

    # ── The storyboard pass ────────────────────────────────────────────────────────────────
    # Same graph, a handful of frames long, pruned to image sinks: the stills the tab shows before
    # any clip is committed, and then locks in as those clips' opening frames.
    print()
    print("storyboard (H3 rendering its own keyframes):")
    for references in (2, 4, 6, 8):
        for frames in (5, 22, 39, 124):
            uploaded = [f"panel_{i}.png" for i in range(references - 1)] + ["location.png"]
            graph = json.loads(json.dumps(source))
            graph = ensure_input_primitives(graph)
            wire_reference_images(graph, uploaded)
            graph[NODE_DURATION]["inputs"]["value"] = frames / 24.0
            graph[NODE_REFERENCE]["inputs"]["length"] = frames
            saves = wire_still_outputs(graph, frames, "storyboard_c01")
            graph = prune_to_outputs(graph, [node for node, _ in saves])

            problems = check_storyboard_structure(graph, frames, references, saves)
            if not args.offline:
                problems += validate_against_server(graph, object_info)

            print(f"  {references} reference(s), {frames} frame(s) → {len(saves)} still(s): "
                  f"{len(graph)} nodes, {len(problems)} problem(s)")
            for problem in problems:
                print(f"    ! {problem}")
            failures += len(problems)

    print()
    print("OK" if failures == 0 else f"{failures} problem(s)")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
