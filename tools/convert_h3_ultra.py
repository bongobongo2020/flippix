#!/usr/bin/env python3
"""
One-off converter: JunkunStudio's "Minimax H3 All-In-One Ultra (Full-Res Tiled)" -> flat API JSON.

Source: workflow/video/h3-minimax/Minimax H3 All-In-One Ultra (Full-Res Tiled).json   (ComfyUI UI graph)
Output: workflow/video/h3-minimax/h3-minimax-ultra.json                              (what FlipPix submits)

What the graph does, and what 🌀 MiniMax I2V's "All-In-One Ultra" workflow drives:

    the fl2va/ref2va hybrid (UNETLoader) -> comfy kitchen attention -> BlockSparseAttention (sol-attn,
    tau 1.3, from 20% of the schedule) -> MysticXXX 0.50 -> DMAD dare-ties 0.75 -> [High motion fix:
    Combat 0.65 -> Motion Repair 0.35 -> Fight Flow 0.35 -> speed slider 3.00, behind a ComfySwitchNode
    that is off as shipped] -> MiniMaxH3SigmaShift 12/3 -> ModelPreviewOverrideKJ

    one MiniMaxH3ReferenceToVideo (up to 9 images, 3 videos, 3 audios, ref_image_size max) at the
    FINISHED canvas (ResolutionSelector 16:9, 2.0 MP, multiple 32 -> 1920x1088), length from seconds
    through the 17k+5 frame expression -> SelfLiftH3Sampler: euler on a 10-step beta schedule, cfg 1,
    the first 6 evaluations at 0.6x scale, then the LMS latent upscaler lifts it to full size, tiled ->
    VAEDecode + VAEDecodeAudio -> VHS_VideoCombine, 24 fps h264 crf 15.

Every widget value below is read from the source graph by node id, not retyped, so a re-export of the
authored file re-converts to the same numbers.

Dropped, because they are UI-only or because FlipPix wires them per run:

- SetNode / GetNode pairs are resolved into direct links. Notes, labels and the three rgthree Fast Groups
  Bypassers are UI only.
- Every reference loader (LoadImage x9, VHS_LoadAudioUpload x3, VHS_LoadVideo x3) and the Ref2V node's
  ref_* inputs. The tab uploads its own files at submit time and adds one loader per filled slot, the
  way the authored graph's group bypassers switch slots on and off.
- UniBlockSwap (138) is authored BYPASSED - the low-VRAM option. Not emitted; the tab grafts it back in
  between the loader and the attention backend when "Low VRAM block swap" is on.
- The second CLIPLoader (236, the uncensored heretic encoder) is authored bypassed; it is an alternative
  file for node 33, which the tab offers as a choice rather than a second node.

Kept exactly as authored, including the switch: the High motion fix LoRAs stay behind ComfySwitchNode 222
reading PrimitiveBoolean 223, so the tab flips one boolean and the lazy switch never loads the four
LoRAs while it is off.

Run:  python tools/convert_h3_ultra.py [--object-info URL_OR_PATH]
"""
import argparse
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax",
                   "Minimax H3 All-In-One Ultra (Full-Res Tiled).json")
DST = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-minimax-ultra.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"


def load_source():
    with open(SRC, encoding="utf-8") as f:
        graph = json.load(f)
    return {n["id"]: n for n in graph["nodes"]}


def widgets(nodes, node_id, names, skip=()):
    """Maps a node's widgets_values onto names, in order. `skip` lists positions holding UI-only values
    (control_after_generate's "fixed"/"randomize") that have no input of their own."""
    values = nodes[node_id]["widgets_values"]
    if isinstance(values, dict):
        return {n: values[n] for n in names}
    values = [v for i, v in enumerate(values) if i not in skip]
    return {n: clean(v) for n, v in zip(names, values)}


def clean(v):
    """The UI stores 0.65 as 0.6500000000000001. Round floats back to what the widget showed."""
    return round(v, 6) if isinstance(v, float) else v


def node(class_type, title, inputs):
    return {"inputs": inputs, "class_type": class_type, "_meta": {"title": title}}


def build(nodes):
    api = {}

    # ── Models ────────────────────────────────────────────────────────────────────────────────────
    api["100"] = node("UNETLoader", "Load Diffusion Model",
                      widgets(nodes, 100, ["unet_name", "weight_dtype"]))
    api["33"] = node("CLIPLoader", "Load CLIP",
                     widgets(nodes, 33, ["clip_name", "type", "device"]))
    api["4"] = node("VAELoader", "Video VAE", widgets(nodes, 4, ["vae_name"]))
    api["5"] = node("VAELoader", "Audio VAE", widgets(nodes, 5, ["vae_name"]))

    # ── The model wire ────────────────────────────────────────────────────────────────────────────
    # UniBlockSwap 138 sits between 100 and 120 in the UI graph, bypassed. Bypassed = pass-through.
    api["120"] = node("ModelAttentionBackend", "Model Attention Backend",
                      {"model": ["100", 0], **widgets(nodes, 120, ["attention"])})
    api["204"] = node("BlockSparseAttention", "Block Sparse Attention (sol-attn)",
                      {"model": ["120", 0], **widgets(nodes, 204, [
                          "selection", "selection.tau", "start_percent", "end_percent", "dense_blocks",
                          "min_tokens", "extra_tokens", "sink_conditioning", "verbose"])})

    lora = ["lora_name", "strength_model"]
    api["186"] = node("LoraLoaderModelOnly", "LoRA: MysticXXX", {"model": ["204", 0], **widgets(nodes, 186, lora)})
    api["242"] = node("LoraLoaderModelOnly", "LoRA: DMAD dare-ties", {"model": ["186", 0], **widgets(nodes, 242, lora)})

    # The High motion fix group, in the authored order 215 -> 217 -> 216 -> 218.
    api["215"] = node("LoraLoaderModelOnly", "High motion fix: Combat", {"model": ["242", 0], **widgets(nodes, 215, lora)})
    api["217"] = node("LoraLoaderModelOnly", "High motion fix: Motion Repair", {"model": ["215", 0], **widgets(nodes, 217, lora)})
    api["216"] = node("LoraLoaderModelOnly", "High motion fix: Fight Flow", {"model": ["217", 0], **widgets(nodes, 216, lora)})
    api["218"] = node("LoraLoaderModelOnly", "High motion fix: speed slider", {"model": ["216", 0], **widgets(nodes, 218, lora)})

    api["223"] = node("PrimitiveBoolean", "Boolean(Fighting High Motion Fix)", widgets(nodes, 223, ["value"]))
    api["222"] = node("ComfySwitchNode", "High motion fix switch",
                      {"switch": ["223", 0], "on_false": ["242", 0], "on_true": ["218", 0]})

    api["219"] = node("MiniMaxH3SigmaShift", "MiniMax H3 Sigma Shift",
                      {"model": ["222", 0], **widgets(nodes, 219, ["shift_video", "shift_audio"])})
    api["221"] = node("ModelPreviewOverrideKJ", "Model Preview Override",
                      {"model": ["219", 0], "audio_vae": ["5", 0], **widgets(nodes, 221, [
                          "max_resolution", "jpeg_quality", "suppress_default_preview", "preview_frames",
                          "preview_fps", "tiny_vae"])})

    # ── Inputs ────────────────────────────────────────────────────────────────────────────────────
    api["190"] = node("PrimitiveStringMultiline", "Prompt", widgets(nodes, 190, ["value"]))
    api["111"] = node("PrimitiveFloat", "Duration(Second)", widgets(nodes, 111, ["value"]))
    api["66"] = node("ComfyMathExpression", "Seconds -> frames (17k+5)",
                     {**widgets(nodes, 66, ["expression"]), "values.a": ["111", 0]})
    api["112"] = node("ResolutionSelector", "Resolution Selector (Final)",
                      widgets(nodes, 112, ["aspect_ratio", "megapixels", "multiple"]))

    ref2v = widgets(nodes, 121, ["prompt", "width", "height", "length", "ref_image_size"])
    api["121"] = node("MiniMaxH3ReferenceToVideo", "MiniMax H3 Reference To Video", {
        "clip": ["33", 0],
        "vae": ["4", 0],
        "audio_vae": ["5", 0],
        "prompt": ["190", 0],
        "width": ["112", 0],
        "height": ["112", 1],
        "length": ["66", 1],
        "ref_image_size": ref2v["ref_image_size"],
    })
    api["225"] = node("ConditioningZeroOut", "Negative (zeroed)", {"conditioning": ["121", 0]})

    # ── SelfLift sampling ─────────────────────────────────────────────────────────────────────────
    api["164"] = node("KSamplerSelect", "KSampler Select", widgets(nodes, 164, ["sampler_name"]))
    api["165"] = node("BasicScheduler", "Basic Scheduler",
                      {"model": ["221", 0], **widgets(nodes, 165, ["scheduler", "steps", "denoise"])})
    api["224"] = node("SelfLiftH3Sampler", "SelfLift H3 Sampler", {
        "model": ["221", 0],
        "positive": ["121", 0],
        "negative": ["225", 0],
        "vae": ["4", 0],
        "latent_image": ["121", 1],
        "sampler": ["164", 0],
        "sigmas": ["165", 0],
        # Position 1 is control_after_generate ("fixed"), which has no input.
        **widgets(nodes, 224, ["seed", "cfg", "transition_step", "lowres_scale", "rho", "w_min", "w_max",
                               "upscaler_model", "highres_tiling", "upscaler_unload"], skip=(1,)),
    })

    # ── Decode and save ───────────────────────────────────────────────────────────────────────────
    api["153"] = node("VAEDecode", "VAE Decode", {"samples": ["224", 0], "vae": ["4", 0]})
    api["159"] = node("VAEDecodeAudio", "VAE Decode Audio", {"samples": ["224", 0], "vae": ["5", 0]})
    api["163"] = node("VHS_VideoCombine", "Video Combine", {
        "images": ["153", 0],
        "audio": ["159", 0],
        **widgets(nodes, 163, ["frame_rate", "loop_count", "filename_prefix", "format", "pingpong",
                               "save_output", "pix_fmt", "crf", "save_metadata", "trim_to_audio"]),
    })
    return api


def load_object_info(arg):
    if not arg:
        return None
    try:
        if arg.startswith("http"):
            with urllib.request.urlopen(arg, timeout=60) as r:
                return json.load(r)
        with open(arg, encoding="utf-8") as f:
            return json.load(f)
    except Exception as ex:  # noqa: BLE001 - validation is optional
        print(f"(object_info unavailable: {ex}; skipping validation)")
        return None


def validate(api, info):
    """Every class exists, every widget input is one the node declares, combos hold a listed value."""
    problems = []
    for nid, n in api.items():
        spec = info.get(n["class_type"])
        if spec is None:
            problems.append(f"{nid}: {n['class_type']} is not installed")
            continue
        declared = {}
        for sec in ("required", "optional"):
            declared.update(spec["input"].get(sec, {}))
        # VHS_VideoCombine declares its per-format widgets (pix_fmt, crf, ...) under format's options.
        fmt = declared.get("format")
        if fmt and len(fmt) > 1 and isinstance(fmt[1], dict):
            for extra in fmt[1].get("formats", {}).get(n["inputs"].get("format"), []):
                declared.setdefault(extra[0], [extra[1]] if len(extra) == 2 else extra[1:])
        for name, value in n["inputs"].items():
            base = name.split(".")[0]
            if base not in declared:
                problems.append(f"{nid} {n['class_type']}: unknown input '{name}'")
                continue
            if isinstance(value, list):
                continue  # a link
            kind = declared[base][0]
            if isinstance(kind, list) and value not in kind:
                problems.append(f"{nid} {n['class_type']}: '{name}'={value!r} not among the server's options")
            elif kind == "COMBO":
                options = declared[base][1].get("options", [])
                if options and value not in options:
                    problems.append(f"{nid} {n['class_type']}: '{name}'={value!r} not in {options}")
        for name, s in spec["input"].get("required", {}).items():
            if name not in n["inputs"] and not any(k.startswith(name + ".") for k in n["inputs"]):
                if s[0] in ("COMFY_AUTOGROW_V3",):
                    continue
                problems.append(f"{nid} {n['class_type']}: required input '{name}' missing")
    return problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--object-info", default=OBJ_URL)
    args = ap.parse_args()

    nodes = load_source()
    api = build(nodes)

    info = load_object_info(args.object_info)
    if info is not None:
        problems = validate(api, info)
        for p in problems:
            print("  !", p)
        if problems:
            sys.exit(1)
        print(f"validated {len(api)} nodes against object_info")

    with open(DST, "w", encoding="utf-8", newline="\n") as f:
        json.dump(api, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"wrote {os.path.relpath(DST, ROOT)} ({len(api)} nodes)")


if __name__ == "__main__":
    main()
