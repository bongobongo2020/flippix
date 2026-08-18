"""Replays the Zimage Base submit-time patch offline and validates it against a live ComfyUI
/object_info.

Both halves of the Image Generator's Generation Settings panel — Text Prompt
(ImageGeneratorViewModel.UpdateZimageBaseWorkflow) and Image Analysis
(ImageAnalyzerViewModel.UpdateWorkflowForGenerationSimple) — drive z-image-base.json by hardcoded
node id: prompt into 76:67, negative into 76:71, canvas into 76:68, sampler into 76:69, filename
prefix into 9, and the LoRA either renamed on 76:96 or pruned with 76:95 rewired straight to the
UNETLoader (76:66). A renamed node in the JSON would not fail the build — it would submit a graph
that silently keeps the workflow's authored prompt, or dangle a link ComfyUI rejects.

Usage:  python tools/verify_zimage_base.py [--url http://10.0.0.10:8188]
        python tools/verify_zimage_base.py --offline     # structure only, no server needed

Keep the node ids below in step with those two methods.
"""

from __future__ import annotations

import argparse
import copy
import json
import pathlib
import urllib.request

WORKFLOW = pathlib.Path(__file__).resolve().parent.parent / "workflow" / "image" / "zimage" / "base" / "z-image-base.json"

PROMPT_NODE = "76:67"        # CLIPTextEncode (positive)
NEGATIVE_NODE = "76:71"      # CLIPTextEncode (negative)
LATENT_NODE = "76:68"        # EmptySD3LatentImage
SAMPLER_NODE = "76:69"       # KSampler
SAVE_NODE = "9"              # SaveImage
LORA_NODE = "76:96"          # LoraLoaderModelOnly
LORA_CONSUMER = "76:95"      # ModelAttentionBackend — reads the model the LoRA produced
UNET_NODE = "76:66"          # UNETLoader — what 76:95 falls back to with the LoRA pruned

# Landscape / portrait / square, from GetZimageDimensionsForAspectRatio and the analyzer's
# GetDimensionsForAspectRatio (they must agree — both feed the same latent node).
RESOLUTIONS = [(1600, 1088), (1088, 1600), (1600, 1600)]


def patch(graph: dict, aspect_index: int, lora: str | None) -> dict:
    """Mirrors UpdateZimageBaseWorkflow / the ZimageBase branch of UpdateWorkflowForGenerationSimple.

    `lora` is the ComfyUI-relative reference the tab builds from the selection
    (``zimage/<name>.safetensors``), or None for a LoRA-off submit.
    """
    graph = copy.deepcopy(graph)
    width, height = RESOLUTIONS[aspect_index]

    graph[PROMPT_NODE]["inputs"]["text"] = "a test prompt"
    graph[NEGATIVE_NODE]["inputs"]["text"] = ""
    graph[LATENT_NODE]["inputs"]["width"] = width
    graph[LATENT_NODE]["inputs"]["height"] = height
    graph[SAMPLER_NODE]["inputs"].update(seed=12345, steps=9, cfg=1.5, denoise=1.0)
    graph[SAVE_NODE]["inputs"]["filename_prefix"] = "ZBase"

    if lora:
        graph[LORA_NODE]["inputs"]["lora_name"] = lora
        graph[LORA_NODE]["inputs"]["strength_model"] = 1.0
    else:
        del graph[LORA_NODE]
        graph[LORA_CONSUMER]["inputs"]["model"] = [UNET_NODE, 0]

    return graph


def check_structure(graph: dict, aspect_index: int, lora: str | None) -> list[str]:
    problems = []
    width, height = RESOLUTIONS[aspect_index]

    def cls(node_id: str) -> str:
        return graph.get(node_id, {}).get("class_type", "<missing>")

    expected = {
        PROMPT_NODE: "CLIPTextEncode",
        NEGATIVE_NODE: "CLIPTextEncode",
        LATENT_NODE: "EmptySD3LatentImage",
        SAMPLER_NODE: "KSampler",
        SAVE_NODE: "SaveImage",
        LORA_CONSUMER: "ModelAttentionBackend",
        UNET_NODE: "UNETLoader",
    }
    for node_id, class_type in expected.items():
        if cls(node_id) != class_type:
            problems.append(f"{node_id}: expected {class_type}, found {cls(node_id)}")

    # The two text encoders must stay distinct: positive feeds the sampler's positive input.
    sampler_inputs = graph.get(SAMPLER_NODE, {}).get("inputs", {})
    if sampler_inputs.get("positive", [None])[0] != PROMPT_NODE:
        problems.append(f"{SAMPLER_NODE}.positive does not read {PROMPT_NODE} — the prompt would be ignored")
    if sampler_inputs.get("negative", [None])[0] != NEGATIVE_NODE:
        problems.append(f"{SAMPLER_NODE}.negative does not read {NEGATIVE_NODE}")

    if graph[LATENT_NODE]["inputs"]["width"] != width or graph[LATENT_NODE]["inputs"]["height"] != height:
        problems.append(f"{LATENT_NODE}: canvas is not {width}x{height}")

    if lora:
        if cls(LORA_NODE) != "LoraLoaderModelOnly":
            problems.append(f"{LORA_NODE}: expected LoraLoaderModelOnly, found {cls(LORA_NODE)}")
        elif graph[LORA_CONSUMER]["inputs"]["model"][0] != LORA_NODE:
            problems.append(f"{LORA_CONSUMER} does not read the LoRA — the selection would do nothing")
    elif LORA_NODE in graph:
        problems.append(f"{LORA_NODE} survived a LoRA-off submit")

    # Every link must resolve, and the save node must still be reachable from the model chain.
    for node_id, node in graph.items():
        for key, value in node.get("inputs", {}).items():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                if value[0] not in graph:
                    problems.append(f"{node_id}.{key} points at pruned node {value[0]}")

    reachable, frontier = set(), [SAVE_NODE]
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
            problems.append(f"{node_id} ({cls(node_id)}) is not reachable from {SAVE_NODE}")

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

    # The LoRA dropdown lists the user's <loras>/zimage folder and the tab submits
    # "zimage/<name>.safetensors", so a run only means something with a name the server actually
    # has. Offline, stand one in and let the structure checks carry the row.
    lora_ref = "zimage/placeholder.safetensors"
    if not args.offline:
        catalogue = object_info.get("LoraLoaderModelOnly", {}).get("input", {}).get("required", {}).get("lora_name", [[]])[0]
        zimage_loras = [name for name in catalogue if name.startswith("zimage/")]
        if zimage_loras:
            lora_ref = zimage_loras[0]
            print(f"LoRA row uses {lora_ref} ({len(zimage_loras)} zimage LoRAs on the server)")
        elif catalogue:
            # Nothing in <loras>/zimage yet: any real name still exercises the wiring, it just
            # says nothing about whether the dropdown's folder is populated.
            lora_ref = catalogue[0]
            print(f"no zimage/ LoRAs on the server — LoRA row falls back to {lora_ref}")

    for aspect_index, aspect in enumerate(("landscape", "portrait", "square")):
        for lora in (None, lora_ref):
            label = f"{aspect}, lora={'on' if lora else 'off'}"
            graph = patch(source, aspect_index, lora)
            found = check_structure(graph, aspect_index, lora)
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
