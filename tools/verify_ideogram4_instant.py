"""Replays the Ideogram tab's submit-time patch offline and validates it against a live ComfyUI
/object_info.

IdeogramViewModel.ApplyToWorkflow drives ideogram4-instant.json entirely by hardcoded node id:
seed into 235, canvas into 247 and 185, filename prefix into 241, the scene document into 186 and
the prompt builder's fallback widgets into 185 — and it deletes the FluxResolutionNode (246) whose
enum-driven size the graph as authored wired into both canvas consumers. A renamed node in the
JSON would not fail the build; it would submit a graph that silently renders the workflow's
authored size, ignores the analyzed scene, or dangles a link ComfyUI rejects.

The Instant graph also leans on several third-party packs (StarSampler, Ideogram4PromptBuilderKJ,
FantasticLoraLoader, ModelAttentionBackend) and on specific UNet/CLIP/VAE files, so the online run
is what tells you the tab can actually generate on this server.

Usage:  python tools/verify_ideogram4_instant.py [--url http://10.0.0.10:8188]
        python tools/verify_ideogram4_instant.py --offline   # structure only, no server needed

Keep the node ids below in step with IdeogramViewModel.
"""

from __future__ import annotations

import argparse
import copy
import json
import math
import pathlib
import urllib.request

WORKFLOW = pathlib.Path(__file__).resolve().parent.parent / "workflow" / "image" / "ideogram" / "ideogram4-instant.json"

PROMPT_BUILDER_NODE = "185"   # Ideogram4PromptBuilderKJ
IMPORT_JSON_NODE = "186"      # PrimitiveStringMultiline -> 185.import_json
LATENT_NODE = "247"           # EmptyFlux2LatentImage
SAMPLER_NODE = "235"          # StarSampler (the single pass)
SAVE_NODE = "241"             # SaveImage
RESOLUTION_NODE = "246"       # FluxResolutionNode - dropped at submit time
SAVE_PREFIX = "gram"

# Output nodes: everything else must be reachable from one of these.
OUTPUT_NODES = (SAVE_NODE, "220", "199")

# SelectedAspectRatio -> W:H, from AspectToRatioString.
ASPECTS = {"Square": (1, 1), "Widescreen": (16, 9), "Portrait": (9, 16)}
# MegapixelOptions, plus a legacy value a queue item saved against the old two-pass graph carries.
MEGAPIXELS = ("1.0", "1.5", "2.0", "2.5", "3.0", "0.75")


def approx_resolution(aspect: str, megapixel: str) -> tuple[int, int]:
    """Mirrors IdeogramViewModel.ApproxResolution."""
    rw, rh = ASPECTS[aspect]
    ratio = rw / rh
    mp = min(max(float(megapixel), 0.25), 4.0)
    total = mp * 1_000_000
    width = int(round(math.sqrt(total * ratio) / 16) * 16)
    height = int(round(math.sqrt(total / ratio) / 16) * 16)
    return max(16, width), max(16, height)


def patch(graph: dict, aspect: str, megapixel: str) -> dict:
    """Mirrors IdeogramViewModel.ApplyToWorkflow."""
    graph = copy.deepcopy(graph)
    width, height = approx_resolution(aspect, megapixel)

    graph[SAMPLER_NODE]["inputs"]["seed"] = 123456789
    graph[LATENT_NODE]["inputs"]["width"] = width
    graph[LATENT_NODE]["inputs"]["height"] = height
    del graph[RESOLUTION_NODE]

    graph[SAVE_NODE]["inputs"]["filename_prefix"] = SAVE_PREFIX
    graph[IMPORT_JSON_NODE]["inputs"]["value"] = json.dumps({
        "high_level_description": "a test scene",
        "style_description": {"aesthetics": "", "lighting": "", "medium": "", "art_style": "", "color_palette": []},
        "compositional_deconstruction": {"background": "", "elements": []},
    })

    builder = graph[PROMPT_BUILDER_NODE]["inputs"]
    builder["high_level_description"] = "a test scene"
    builder["width"] = width
    builder["height"] = height
    builder["elements_data"] = "[]"
    builder["import_mode"] = "always"
    builder["coord_mode"] = "normalized"
    builder["bbox_order"] = "yx"
    # ApplyStyleInputs
    builder["style"] = "art_style"
    builder["background"] = ""
    builder["style.art_style"] = ""
    builder["aesthetics"] = ""
    builder["lighting"] = ""
    builder["medium"] = ""
    builder["style_palette_data"] = ""

    return graph


def check_structure(graph: dict, aspect: str, megapixel: str) -> list[str]:
    problems = []
    width, height = approx_resolution(aspect, megapixel)

    def cls(node_id: str) -> str:
        return graph.get(node_id, {}).get("class_type", "<missing>")

    expected = {
        PROMPT_BUILDER_NODE: "Ideogram4PromptBuilderKJ",
        IMPORT_JSON_NODE: "PrimitiveStringMultiline",
        LATENT_NODE: "EmptyFlux2LatentImage",
        SAMPLER_NODE: "StarSampler",
        SAVE_NODE: "SaveImage",
    }
    for node_id, class_type in expected.items():
        if cls(node_id) != class_type:
            problems.append(f"{node_id}: expected {class_type}, found {cls(node_id)}")

    if RESOLUTION_NODE in graph:
        problems.append(f"{RESOLUTION_NODE} (FluxResolutionNode) survived the submit patch")

    # The scene document is the authoritative description; import_mode "always" is what
    # makes the node read it instead of its own widgets.
    builder = graph.get(PROMPT_BUILDER_NODE, {}).get("inputs", {})
    if builder.get("import_json", [None])[0] != IMPORT_JSON_NODE:
        problems.append(f"{PROMPT_BUILDER_NODE}.import_json does not read {IMPORT_JSON_NODE} — the analyzed scene would be ignored")
    if builder.get("import_mode") != "always":
        problems.append(f"{PROMPT_BUILDER_NODE}.import_mode is {builder.get('import_mode')!r}, not 'always'")
    if builder.get("width") != width or builder.get("height") != height:
        problems.append(f"{PROMPT_BUILDER_NODE}: canvas is not {width}x{height}")

    latent = graph.get(LATENT_NODE, {}).get("inputs", {})
    if latent.get("width") != width or latent.get("height") != height:
        problems.append(f"{LATENT_NODE}: canvas is not {width}x{height}")

    # The builder's string must reach the sampler through the text encoder.
    sampler = graph.get(SAMPLER_NODE, {}).get("inputs", {})
    positive = sampler.get("positive", [None])[0]
    if graph.get(positive, {}).get("inputs", {}).get("text", [None])[0] != PROMPT_BUILDER_NODE:
        problems.append(f"{SAMPLER_NODE}.positive does not trace back to {PROMPT_BUILDER_NODE} — the prompt would be ignored")
    if sampler.get("latent", [None])[0] != LATENT_NODE:
        problems.append(f"{SAMPLER_NODE}.latent does not read {LATENT_NODE} — the chosen size would be ignored")

    # RetrieveOutputImageAsync matches saved files by prefix, and only SaveImage-style
    # nodes register in /history for remote retrieval.
    if graph.get(SAVE_NODE, {}).get("inputs", {}).get("filename_prefix") != SAVE_PREFIX:
        problems.append(f"{SAVE_NODE}: filename_prefix is not {SAVE_PREFIX!r}")

    # Every link must resolve, and nothing may be stranded off the output graph.
    for node_id, node in graph.items():
        for key, value in node.get("inputs", {}).items():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                if value[0] not in graph:
                    problems.append(f"{node_id}.{key} points at pruned node {value[0]}")

    reachable, frontier = set(), list(OUTPUT_NODES)
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
            problems.append(f"{node_id} ({cls(node_id)}) is not reachable from an output node")

    return problems


def expand_dynamic_combos(known: dict, node_inputs: dict) -> tuple[dict, list[str]]:
    """Resolves COMFY_DYNAMICCOMBO_V3 inputs into the dotted sub-inputs the API format uses.

    Ideogram4PromptBuilderKJ's `style` is a dynamic combo whose options each carry their own
    fields; the frontend serializes the selected one as `style.<option>`. /object_info only
    declares `style`, so the sub-input has to be unfolded before the unknown-key check runs.
    """
    known = dict(known)
    problems = []
    for key, declared in list(known.items()):
        if not (declared and declared[0] == "COMFY_DYNAMICCOMBO_V3"):
            continue
        options = {opt["key"]: opt for opt in declared[1].get("options", [])}
        selected = node_inputs.get(key)
        if selected not in options:
            problems.append(f"{key} = {selected!r} not in {sorted(options)}")
            continue
        for option_key, option in options.items():
            for sub_declared in option.get("inputs", {}).get("required", {}).values():
                known[f"{key}.{option_key}"] = sub_declared
    return known, problems


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
        known, combo_problems = expand_dynamic_combos(known, node["inputs"])
        problems += [f"{node_id} ({class_type}).{p}" for p in combo_problems]

        for key, value in node["inputs"].items():
            if key not in known:
                problems.append(f"{node_id} ({class_type}): no input '{key}'")
                continue
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                continue
            declared = known[key]
            if declared and isinstance(declared[0], list) and value not in declared[0]:
                problems.append(f"{node_id} ({class_type}).{key} = {value!r} not in {declared[0]}")

        for key in required:
            if key not in node["inputs"]:
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
    problems = 0

    for aspect in ASPECTS:
        for megapixel in MEGAPIXELS:
            label = f"{aspect}, {megapixel} MP"
            graph = patch(source, aspect, megapixel)
            found = check_structure(graph, aspect, megapixel)
            if not args.offline:
                found += validate_against_server(graph, object_info)
            if found:
                problems += len(found)
                print(f"FAIL {label}")
                for problem in found:
                    print(f"  - {problem}")
            else:
                width, height = approx_resolution(aspect, megapixel)
                print(f"ok   {label} -> {width}x{height} ({len(graph)} nodes)")

    print("\nall rows pass" if problems == 0 else f"\n{problems} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
