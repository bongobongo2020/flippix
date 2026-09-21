"""Replays the Qwen 2.1 (prompt enhancer) submit-time patch offline and validates it against a
live ComfyUI /object_info.

Both halves of the Image Generator's Generation Settings panel drive qwen21-prompt-enhancer.json
by hardcoded node id — Text Prompt through ImageGeneratorViewModel.UpdateQwen21EnhancerWorkflow,
Image Analysis through the Qwen21Enhancer branches of
ImageAnalyzerViewModel.UpdateWorkflowForGenerationSimple:

    prompt   -> 468.value      (the USER PROMPT primitive, NOT the encoder)
    enhancer -> 473.value      (ORIGINAL / ENHANCED switch)
    negative -> 459:452.negative_prompt   (analyzer only, and only when it has one)
    canvas   -> 459:456.width / height
    sampler  -> 459:458.seed / steps / cfg / denoise

The prompt node is the trap this script exists to catch. 468 feeds both the switch's on_false and
the enhancer's concatenate, so it is the one place the text can go; writing it into 474 or 459:452
instead would submit a graph that renders, silently ignoring the enhancer. A renamed node would
not fail the build either — it would render the workflow's authored prompt instead of the user's.

Usage:  python tools/verify_qwen21_enhancer.py [--url http://10.0.0.10:8188]
        python tools/verify_qwen21_enhancer.py --offline     # structure only, no server needed

Keep the node ids below in step with those two methods.
"""

from __future__ import annotations

import argparse
import copy
import json
import pathlib
import urllib.request

WORKFLOW = pathlib.Path(__file__).resolve().parent.parent / "workflow" / "image" / "qwen" / "qwen21-prompt-enhancer.json"

PROMPT_NODE = "468"          # PrimitiveStringMultiline — USER PROMPT
SYSTEM_NODE = "470"          # PrimitiveStringMultiline — the enhancer's system prompt
CONCAT_NODE = "471"          # StringConcatenate — system + user
ENHANCER_NODE = "472"        # TextGenerate — Qwen3-VL 8B rewrite
SWITCH_FLAG_NODE = "473"     # PrimitiveBoolean — ORIGINAL / ENHANCED
SWITCH_NODE = "474"          # ComfySwitchNode — picks 468 or 472
ENCODER_NODE = "459:452"     # TextEncodeQwenImage21
LATENT_NODE = "459:456"      # EmptyLatentImage
SAMPLER_NODE = "459:458"     # KSampler
SAVE_NODE = "461"            # SaveImage

# Landscape / portrait / square, from ImageGeneratorViewModel.Qwen21Resolution — the analyzer
# calls the same helper, so the two halves cannot drift apart.
RESOLUTIONS = [(1600, 1088), (1088, 1600), (1600, 1600)]


def patch(graph: dict, aspect_index: int, use_enhancer: bool, negative: str | None) -> dict:
    """Mirrors UpdateQwen21EnhancerWorkflow / the Qwen21Enhancer branches of the analyzer.

    `negative` is None for the Text Prompt half (it has no negative-prompt control) and a string
    for the analyzer, which only overrides the workflow's own negative when the field is filled.
    """
    graph = copy.deepcopy(graph)
    width, height = RESOLUTIONS[aspect_index]

    graph[PROMPT_NODE]["inputs"]["value"] = "a test prompt"
    graph[SWITCH_FLAG_NODE]["inputs"]["value"] = use_enhancer
    graph[LATENT_NODE]["inputs"]["width"] = width
    graph[LATENT_NODE]["inputs"]["height"] = height
    graph[SAMPLER_NODE]["inputs"].update(seed=12345, steps=9, cfg=3.5, denoise=1.0)
    if negative:
        graph[ENCODER_NODE]["inputs"]["negative_prompt"] = negative

    return graph


def check_structure(graph: dict, aspect_index: int, use_enhancer: bool) -> list[str]:
    problems = []
    width, height = RESOLUTIONS[aspect_index]

    def cls(node_id: str) -> str:
        return graph.get(node_id, {}).get("class_type", "<missing>")

    expected = {
        PROMPT_NODE: "PrimitiveStringMultiline",
        SYSTEM_NODE: "PrimitiveStringMultiline",
        CONCAT_NODE: "StringConcatenate",
        ENHANCER_NODE: "TextGenerate",
        SWITCH_FLAG_NODE: "PrimitiveBoolean",
        SWITCH_NODE: "ComfySwitchNode",
        ENCODER_NODE: "TextEncodeQwenImage21",
        LATENT_NODE: "EmptyLatentImage",
        SAMPLER_NODE: "KSampler",
        SAVE_NODE: "SaveImage",
    }
    for node_id, class_type in expected.items():
        if cls(node_id) != class_type:
            problems.append(f"{node_id}: expected {class_type}, found {cls(node_id)}")

    # The prompt has to reach the encoder down BOTH routes, or one setting of the toggle
    # silently renders something the user never typed.
    switch_inputs = graph.get(SWITCH_NODE, {}).get("inputs", {})
    if switch_inputs.get("on_false", [None])[0] != PROMPT_NODE:
        problems.append(f"{SWITCH_NODE}.on_false does not read {PROMPT_NODE} — "
                        "with the enhancer off the prompt would be ignored")
    if switch_inputs.get("on_true", [None])[0] != ENHANCER_NODE:
        problems.append(f"{SWITCH_NODE}.on_true does not read {ENHANCER_NODE}")
    if switch_inputs.get("switch", [None])[0] != SWITCH_FLAG_NODE:
        problems.append(f"{SWITCH_NODE}.switch does not read {SWITCH_FLAG_NODE} — the toggle would do nothing")
    if graph.get(CONCAT_NODE, {}).get("inputs", {}).get("string_b", [None])[0] != PROMPT_NODE:
        problems.append(f"{CONCAT_NODE}.string_b does not read {PROMPT_NODE} — "
                        "with the enhancer on it would rewrite the wrong text")
    if graph.get(ENHANCER_NODE, {}).get("inputs", {}).get("prompt", [None])[0] != CONCAT_NODE:
        problems.append(f"{ENHANCER_NODE}.prompt does not read {CONCAT_NODE} — the system prompt would be lost")
    if graph.get(ENCODER_NODE, {}).get("inputs", {}).get("prompt", [None])[0] != SWITCH_NODE:
        problems.append(f"{ENCODER_NODE}.prompt does not read {SWITCH_NODE} — the toggle would be bypassed")

    if graph[SWITCH_FLAG_NODE]["inputs"]["value"] is not use_enhancer:
        problems.append(f"{SWITCH_FLAG_NODE}: toggle did not take")

    sampler_inputs = graph.get(SAMPLER_NODE, {}).get("inputs", {})
    if sampler_inputs.get("positive", [None])[0] != ENCODER_NODE:
        problems.append(f"{SAMPLER_NODE}.positive does not read {ENCODER_NODE}")
    if sampler_inputs.get("negative", [None])[0] != ENCODER_NODE:
        problems.append(f"{SAMPLER_NODE}.negative does not read {ENCODER_NODE}")

    if graph[LATENT_NODE]["inputs"]["width"] != width or graph[LATENT_NODE]["inputs"]["height"] != height:
        problems.append(f"{LATENT_NODE}: canvas is not {width}x{height}")

    # Every link must resolve, and the save node must still be reachable from the model chain.
    for node_id, node in graph.items():
        for key, value in node.get("inputs", {}).items():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                if value[0] not in graph:
                    problems.append(f"{node_id}.{key} points at missing node {value[0]}")

    # PreviewAny (475) is a second output root, so walk from both sinks.
    reachable, frontier = set(), [SAVE_NODE, "475"]
    while frontier:
        node_id = frontier.pop()
        if node_id in reachable or node_id not in graph:
            continue
        reachable.add(node_id)
        for value in graph[node_id]["inputs"].values():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                frontier.append(value[0])
    for node_id in graph:
        if node_id not in reachable:
            problems.append(f"{node_id} ({cls(node_id)}) is not reachable from {SAVE_NODE} or 475")

    return problems


def validate_against_server(graph: dict, object_info: dict) -> list[str]:
    problems = []
    for node_id, node in graph.items():
        class_type = node["class_type"]
        spec = object_info.get(class_type)
        if spec is None:
            problems.append(f"{node_id}: class {class_type} is not on the server")
            continue

        required = spec["input"].get("required", {})
        known = {**required, **spec["input"].get("optional", {})}

        for key, value in node["inputs"].items():
            # TextGenerate's sampling_mode sub-widgets arrive as "sampling_mode.<name>".
            base = key.split(".", 1)[0]
            if base not in known:
                problems.append(f"{node_id} ({class_type}): no input '{key}'")
                continue
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                continue
            declared = known[base]
            if "." not in key and declared and isinstance(declared[0], list) and value not in declared[0]:
                problems.append(f"{node_id} ({class_type}).{key} = {value!r} not in {declared[0]}")

        for key, declared in required.items():
            # COMFY_AUTOGROW_V3 / COMFY_DYNAMICCOMBO_V3 slots carry no value of their own.
            if key not in node["inputs"] and not str(declared[0]).startswith("COMFY_"):
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
            print(f"could not reach {args.url} ({exc}) - falling back to --offline")
            args.offline = True

    source = json.loads(WORKFLOW.read_text(encoding="utf-8"))
    problems = 0

    for aspect_index, aspect in enumerate(("landscape", "portrait", "square")):
        for use_enhancer in (False, True):
            for negative in (None, "blurry, low quality"):
                half = "text" if negative is None else "analyzer"
                label = f"{aspect}, enhancer={'on' if use_enhancer else 'off'}, {half}"
                graph = patch(source, aspect_index, use_enhancer, negative)
                found = check_structure(graph, aspect_index, use_enhancer)
                if not args.offline:
                    found += validate_against_server(graph, object_info)
                if found:
                    problems += len(found)
                    print(f"FAIL {label}")
                    for problem in found:
                        print(f"  - {problem}")
                else:
                    print(f"ok   {label} ({len(graph)} nodes)")

    print("\nall rows pass" if problems == 0 else f"\n{problems} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
