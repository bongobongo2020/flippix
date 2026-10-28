#!/usr/bin/env python3
"""
One-off converter: "H3 Turbo 2-Pass ErosMax b7 (experimental, RefMods v3)" -> flat API JSON.

Source: workflow/video/h3-minimax/H3_Turbo_2Pass_ErosMax_b7_experimental_refmods_v3 (1).json   (UI graph)
Output: workflow/video/h3-minimax/h3-erosmax-2pass.json                                         (what FlipPix submits)

What the graph does, and what 🌀 MiniMax I2V's "ErosMax 2-Pass" workflow drives:

    10Eros Max b7 8-step TURBO int8HQ (UNETLoader) -> [Turbo LoRA, authored bypassed] -> H3PowerLoraStack
    (one row, off) -> MiniMaxH3SigmaShift 12/3 -> BlockSparseAttention (vsa, keep 10%, blocks 0,1 dense)
    -> comfy kitchen attention -> { pass 2 guider, ModelPreviewOverrideKJ -> pass 1 guider }

    one MiniMaxH3ReferenceToVideo (main picture + 3 more, 3 videos with their soundtracks, 3 audios,
    ref_image_size match) at the FIRST-PASS canvas (ResolutionSelector 4:3, 0.3 MP, multiple 32), length
    from seconds through the 17k+5 frame expression -> [MiniMaxH3RefModApply when RefMods are loaded] ->

    pass 1: SamplerCustomAdvanced, er_sde on sgm_uniform 8 steps, denoise 1
    learned latent upscale: MinimaxH3LatentUpscaler3D to the final megapixels (1.0) on the video half
    pass 2: SamplerCustomAdvanced, er_sde on beta 3 steps at denoise 0.4 (or the manual sigmas)

    -> VAEDecode -> RTX Video Super Resolution x1.2 -> [RTX FrameGen x2..5, Normal mode only]
    -> LokkenVideoPostProcessing (CRF 18, optional grain) -> ConcatenateVideo + audio -> SaveVideo

Preview mode skips the upscale and pass 2; "use first pass audio" picks pass 1's soundtrack. Continue
mode extends a previous video in the same submission: the last 22/39/56 of its frames (and optionally its
soundtrack and last frame) guide both passes through MiniMaxH3AddGuide, and the export trims the overlap
off the new clip and appends it to every frame of the source.

Every widget value below is read from the source graph by node id, not retyped, so a re-export of the
authored file re-converts to the same numbers. Subgraph nodes are flattened to ComfyUI's own
"<instance>:<inner>" ids (151:11 is the Ref2V node inside the "Conditioning and two-pass generation"
subgraph 151, 152:135 a Lokken node inside "Continuation export" 152).

Resolved or dropped, because they are UI-only or because FlipPix wires them per run:

- Notes, the rgthree Seed (59; FlipPix writes the seed onto RandomNoise 31), the unconnected
  VAEDecodeTiled (151:46) and the unconnected ManualSigmas alternative's wiring (185 is kept, unlinked,
  for the "manual second-pass sigmas" option).
- The two CustomCombo subgraphs: the guide window (153) becomes a PrimitiveInt holding its index, read by
  the same 22 + 17*a expression; the encoder preset (178) becomes a literal on both Lokken nodes.
- Every reference loader (LoadImage x4, VHS_LoadVideo x3, LoadAudio x3, all authored bypassed and empty)
  and the Ref2V node's ref_* inputs. The tab uploads its own files and adds one loader per filled slot.
- The previous-video path (104 -> VHS_LoadVideoPath 151:105) becomes an uploaded VHS_LoadVideo with the
  same settings, so the source does not have to be on the server's disk already.
- The Turbo LoRA (36) is emitted but unlinked, as authored (bypassed); the tab threads it onto the wire.
- RefMods (181 loader, 151:182 apply) are emitted; the tab steps over them when no RefMod is chosen, so a
  server without ComfyUI-MiniMaxH3Mod still runs the rest of the graph.

Run:  python tools/convert_h3_erosmax_2pass.py [--object-info URL_OR_PATH]
"""
import argparse
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax",
                   "H3_Turbo_2Pass_ErosMax_b7_experimental_refmods_v3 (1).json")
DST = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-erosmax-2pass.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

GEN = "494706d1-099f-48ed-8d77-11d5e2e77698"      # "Conditioning and two-pass generation" (instance 151)
EXPORT = "630fdab5-fda5-411b-b3ad-9d894df24641"   # "Continuation export" (instance 152)
GUIDE = "2d42fa0e-37bc-4eed-b125-7c9a85fbac7c"    # "Guide window (frames): 22 / 39 / 56" (instance 153)
PRESET = "0c4996bd-a4b2-4ebf-850d-ff1d9eb15c71"   # "Output saving quality - encoder preset" (instance 178)


def load_source():
    with open(SRC, encoding="utf-8") as f:
        graph = json.load(f)
    nodes = {n["id"]: n for n in graph["nodes"]}
    subgraphs = {s["id"]: {n["id"]: n for n in s["nodes"]} for s in graph["definitions"]["subgraphs"]}
    return nodes, subgraphs


def widgets(nodes, node_id, names, skip=()):
    """Maps a node's widgets_values onto names, in order. `skip` lists positions holding UI-only values
    (control_after_generate's "fixed"/"randomize") that have no input of their own. A None name drops
    that position (a widget whose value arrives over a link instead)."""
    values = nodes[node_id]["widgets_values"]
    if isinstance(values, dict):
        return {n: values[n] for n in names if n}
    values = [v for i, v in enumerate(values) if i not in skip]
    return {n: clean(v) for n, v in zip(names, values) if n}


def clean(v):
    """The UI stores 0.65 as 0.6500000000000001. Round floats back to what the widget showed."""
    return round(v, 6) if isinstance(v, float) else v


def node(class_type, title, inputs):
    return {"inputs": inputs, "class_type": class_type, "_meta": {"title": title}}


def g(inner):
    return f"151:{inner}"


def x(inner):
    return f"152:{inner}"


def build(nodes, subgraphs):
    gen = subgraphs[GEN]
    exp = subgraphs[EXPORT]
    api = {}

    # ── Models ────────────────────────────────────────────────────────────────────────────────────
    api["30"] = node("UNETLoader", "Load Diffusion Model", widgets(nodes, 30, ["unet_name", "weight_dtype"]))
    api["15"] = node("CLIPLoader", "Load CLIP", widgets(nodes, 15, ["clip_name", "type", "device"]))
    api["3"] = node("VAELoader", "Video VAE", widgets(nodes, 3, ["vae_name"]))
    api["4"] = node("VAELoader", "Audio VAE", widgets(nodes, 4, ["vae_name"]))

    # ── The model wire ────────────────────────────────────────────────────────────────────────────
    # 36 is authored bypassed: emitted on the loader but nothing reads it until the tab threads it in.
    api["36"] = node("LoraLoaderModelOnly", "Turbo LoRA (skip if the model includes it)",
                     {"model": ["30", 0], **widgets(nodes, 36, ["lora_name", "strength_model"])})
    stack = nodes[101]["widgets_values"]
    rows = {f"lora_{i + 1}": row for i, row in enumerate(v for v in stack[2:] if isinstance(v, dict))}
    api["101"] = node("H3PowerLoraStack", "H3 Power LoRA Stack", {
        "model": ["30", 0], "quantized_layers": stack[0], "adaln_port": stack[1], **rows})
    api["38"] = node("MiniMaxH3SigmaShift", "MiniMax H3 Sigma Shift",
                     {"model": ["101", 0], **widgets(nodes, 38, ["shift_video", "shift_audio"])})
    api["100"] = node("BlockSparseAttention", "Block Sparse Attention", {"model": ["38", 0], **widgets(nodes, 100, [
        "selection", "selection.keep_percent", "start_percent", "end_percent", "dense_blocks", "min_tokens",
        "extra_tokens", "sink_conditioning", "verbose"])})
    api["37"] = node("ModelAttentionBackend", "Model Attention Backend",
                     {"model": ["100", 0], **widgets(nodes, 37, ["attention"])})
    api["12"] = node("ModelPreviewOverrideKJ", "Model Preview Override (pass 1)", {
        "model": ["37", 0], "preview_frames": [g(19), 1],
        **widgets(nodes, 12, ["max_resolution", "jpeg_quality", "suppress_default_preview", None,
                              "preview_fps", "tiny_vae"])})

    # ── Inputs ────────────────────────────────────────────────────────────────────────────────────
    api["2"] = node("PrimitiveStringMultiline", "Prompt", widgets(nodes, 2, ["value"]))
    api["17"] = node("PrimitiveFloat", "Duration in secs (Preview + Full)", widgets(nodes, 17, ["value"]))
    api["20"] = node("ResolutionSelector", "Selector First LowRes Pass",
                     widgets(nodes, 20, ["aspect_ratio", "megapixels", "multiple"]))
    api["23"] = node("PrimitiveFloat", "Final Target Megapixels (2nd Pass)", widgets(nodes, 23, ["value"]))
    api["87"] = node("PrimitiveBoolean", "Use first pass audio?", widgets(nodes, 87, ["value"]))
    api["94"] = node("PrimitiveBoolean", "Preview - skip second pass", widgets(nodes, 94, ["value"]))
    api["103"] = node("PrimitiveBoolean", "Normal / Continue (TRUE = Continue)", widgets(nodes, 103, ["value"]))
    api["117"] = node("PrimitiveBoolean", "Previous video has audio?", widgets(nodes, 117, ["value"]))
    api["131"] = node("PrimitiveBoolean", "Use last source frame as H3 reference?", widgets(nodes, 131, ["value"]))
    api["144"] = node("PrimitiveBoolean", "Use source AUDIO guide?", widgets(nodes, 144, ["value"]))
    api["147"] = node("PrimitiveString", "Extension output subfolder", widgets(nodes, 147, ["value"]))
    api["73"] = node("PrimitiveString", "File name prefix for Full Resolution", widgets(nodes, 73, ["value"]))
    api["74"] = node("PrimitiveString", "File name prefix for Previews", widgets(nodes, 74, ["value"]))
    api["173"] = node("INTConstant", "FrameGen multiplier (1 = OFF)", widgets(nodes, 173, ["value"]))
    api["174"] = node("PrimitiveFloat", "Output grain strength (0 = OFF)", widgets(nodes, 174, ["value"]))

    # The guide-window CustomCombo, resolved to the index it hands the 22 + 17a expression.
    guide_choice = nodes[153]["widgets_values"][0]
    guide_options = subgraphs[GUIDE][139]["widgets_values"][2:5]
    api["153"] = node("PrimitiveInt", "Guide window index (0 = 22, 1 = 39, 2 = 56 frames)",
                      {"value": guide_options.index(guide_choice)})
    encoder_preset = nodes[178]["widgets_values"][0]

    # ── Sampling inputs ───────────────────────────────────────────────────────────────────────────
    api["31"] = node("RandomNoise", "Random Noise", widgets(nodes, 31, ["noise_seed"], skip=(1,)))
    api["18"] = node("KSamplerSelect", "KSampler Select", widgets(nodes, 18, ["sampler_name"]))
    api["24"] = node("BasicScheduler", "Scheduler First Pass",
                     {"model": ["37", 0], **widgets(nodes, 24, ["scheduler", "steps", "denoise"])})
    api["102"] = node("BasicScheduler", "Scheduler Second Pass",
                      {"model": ["37", 0], **widgets(nodes, 102, ["scheduler", "steps", "denoise"])})
    api["185"] = node("ManualSigmas", "Manual Second Pass Sigmas", widgets(nodes, 185, ["sigmas"]))

    # ── RefMods ───────────────────────────────────────────────────────────────────────────────────
    order = ["show_info"]
    for i in range(1, 9):
        order += [f"mod_{i}", f"strength_{i}", f"copies_{i}"]
    order += ["max_total_tokens"]
    for i in range(1, 9):
        order += [f"components_{i}", f"visual_strength_{i}", f"audio_strength_{i}"]
    api["181"] = node("MiniMaxH3RefModsLoader", "RefMods: saved references", widgets(nodes, 181, order))

    # ── Conditioning and two-pass generation (subgraph 151) ───────────────────────────────────────
    api[g(19)] = node("ComfyMathExpression", "Seconds -> frames (17k+5)",
                      {**widgets(gen, 19, ["expression"]), "values.a": ["17", 0]})
    ref2v = widgets(gen, 11, ["prompt", "width", "height", "length", "ref_image_size"])
    api[g(11)] = node("MiniMaxH3ReferenceToVideo", "MiniMax H3 Reference To Video", {
        "clip": ["15", 0], "vae": ["3", 0], "audio_vae": ["4", 0],
        "prompt": ["2", 0], "width": ["20", 0], "height": ["20", 1], "length": [g(19), 1],
        "ref_image_size": ref2v["ref_image_size"],
        "ref_images.ref_image_4": [g(134), 0],
    })
    api[g(182)] = node("MiniMaxH3RefModApply", "RefMods: append before Normal / Continue branches", {
        "conditioning": [g(11), 0], "mods": ["181", 0],
        **widgets(gen, 182, ["override", "retention", "curve_direction", "scramble_seed", "curve_shape",
                             "curve_value", "graph_preset", "scramble_mode", "scramble_keep",
                             "max_total_tokens", "save_preset_as"], skip=(4,))})

    # Continue: the source video, its guide window, its soundtrack.
    src = gen[105]["widgets_values"]
    api[g(105)] = node("VHS_LoadVideo", "Previous video: all frames at 24 fps; format None", {
        "video": "", **{k: src[k] for k in ("force_rate", "custom_width", "custom_height", "frame_load_cap",
                                            "skip_first_frames", "select_every_nth", "format")}})
    api[g(140)] = node("ComfyMathExpression", "Selected guide frames (22 / 39 / 56)",
                       {**widgets(gen, 140, ["expression"]), "values.a": ["153", 0]})
    api[g(106)] = node("ComfyMathExpression", "Guide start: source frames minus selected window",
                       {**widgets(gen, 106, ["expression"]), "values.a": [g(105), 1], "values.b": [g(140), 1]})
    api[g(107)] = node("GetImageRangeFromBatch", "Last selected source frames for BOTH guides",
                       {"images": [g(105), 0], "start_index": [g(106), 1], "num_frames": [g(140), 1]})
    api[g(116)] = node("ComfyMathExpression", "Previous clip duration = loaded frames / 24",
                       {**widgets(gen, 116, ["expression"]), "values.a": [g(105), 1]})
    api[g(118)] = node("EmptyAudio", "Silent source soundtrack",
                       {"duration": [g(116), 0], **widgets(gen, 118, [None, "sample_rate", "channels"])})
    api[g(120)] = node("AudioConcat", "Pad short source soundtrack with silence",
                       {"audio1": [g(105), 2], "audio2": [g(118), 0], **widgets(gen, 120, ["direction"])})
    api[g(119)] = node("TrimAudioDuration", "Source soundtrack: fit to source video duration",
                       {"audio": [g(120), 0], "duration": [g(116), 0], **widgets(gen, 119, ["start_index"])})
    api[g(121)] = node("ComfySwitchNode", "Previous audio / silent prefix",
                       {"switch": ["117", 0], "on_false": [g(118), 0], "on_true": [g(119), 0]})
    api[g(141)] = node("ComfyMathExpression", "Selected overlap in seconds = frames / 24",
                       {**widgets(gen, 141, ["expression"]), "values.a": [g(140), 1]})
    api[g(142)] = node("ComfyMathExpression", "Source guide audio start = source tail frame / 24",
                       {**widgets(gen, 142, ["expression"]), "values.a": [g(106), 1]})
    api[g(143)] = node("TrimAudioDuration", "Same source tail soundtrack as the VIDEO guide",
                       {"audio": [g(121), 0], "start_index": [g(142), 0], "duration": [g(141), 0]})
    api[g(145)] = node("ComfyAndNode", "Continue AND source has audio AND audio guide ON",
                       {"values.value0": ["103", 0], "values.value1": ["117", 0], "values.value2": ["144", 0]})
    api[g(146)] = node("ComfySwitchNode", "Optional AUDIO guide / OFF = video-only guides",
                       {"switch": [g(145), 0], "on_true": [g(143), 0]})
    api[g(132)] = node("ComfyAndNode", "Continue AND source-reference enabled",
                       {"values.value0": ["103", 0], "values.value1": ["131", 0]})
    api[g(133)] = node("ImageFromBatch", "Last source-video frame",
                       {"image": [g(107), 0], **widgets(gen, 133, ["batch_index", "length"])})
    api[g(134)] = node("ComfySwitchNode", "Optional automatic source frame / OFF = no extra reference",
                       {"switch": [g(132), 0], "on_true": [g(133), 0]})

    # Pass 1 at the first-pass canvas.
    guide = ["frame_idx"]
    api[g(108)] = node("MiniMaxH3AddGuide", "Continue: selected guide at first-pass resolution", {
        "positive": [g(182), 0], "latent": [g(11), 1], "vae": ["3", 0], "audio_vae": ["4", 0],
        "image": [g(107), 0], "audio": [g(146), 0], **widgets(gen, 108, guide)})
    api[g(110)] = node("ComfySwitchNode", "Normal / Continue conditioning: pass 1",
                       {"switch": ["103", 0], "on_false": [g(182), 0], "on_true": [g(108), 0]})
    api[g(29)] = node("BasicGuider", "Guider pass 1", {"model": ["12", 0], "conditioning": [g(110), 0]})
    api[g(21)] = node("SamplerCustomAdvanced", "Sampler pass 1", {
        "noise": ["31", 0], "guider": [g(29), 0], "sampler": ["18", 0], "sigmas": ["24", 0],
        "latent_image": [g(11), 1]})
    api[g(35)] = node("LTXVSeparateAVLatent", "Split pass 1 AV latent", {"av_latent": [g(21), 1]})

    # Learned latent upscale, then pass 2 at the final canvas.
    up = widgets(gen, 26, ["model_name", "mode", None, "align", "enable_temporal_chunking", "force_unload",
                           "device", "precision"])
    api[g(26)] = node("MinimaxH3LatentUpscaler3D", "Learned latent upscale", {
        "latent": [g(35), 0], "mode.megapixels": ["23", 0], **up})
    api[g(10)] = node("LTXVConcatAVLatent", "Rejoin upscaled video + pass 1 audio",
                      {"video_latent": [g(26), 0], "audio_latent": [g(35), 1]})
    api[g(109)] = node("MiniMaxH3AddGuide", "Continue: selected guide at second-pass resolution", {
        "positive": [g(182), 0], "latent": [g(10), 0], "vae": ["3", 0], "audio_vae": ["4", 0],
        "image": [g(107), 0], "audio": [g(146), 0], **widgets(gen, 109, guide)})
    api[g(111)] = node("ComfySwitchNode", "Normal / Continue conditioning: pass 2",
                       {"switch": ["103", 0], "on_false": [g(182), 0], "on_true": [g(109), 0]})
    api[g(32)] = node("BasicGuider", "Guider pass 2", {"model": ["37", 0], "conditioning": [g(111), 0]})
    api[g(16)] = node("SamplerCustomAdvanced", "Sampler pass 2", {
        "noise": ["31", 0], "guider": [g(32), 0], "sampler": ["18", 0], "sigmas": ["102", 0],
        "latent_image": [g(10), 0]})

    # Preview / audio choice, decode.
    api[g(95)] = node("ComfySwitchNode", "Choose video for preview",
                      {"switch": ["94", 0], "on_false": [g(16), 0], "on_true": [g(35), 0]})
    api[g(98)] = node("ComfyOrNode", "First pass audio OR preview",
                      {"values.value0": ["87", 0], "values.value1": ["94", 0]})
    api[g(88)] = node("ComfySwitchNode", "Switch audio 1st or 2nd pass",
                      {"switch": [g(98), 0], "on_false": [g(16), 0], "on_true": [g(35), 1]})
    api[g(25)] = node("VAEDecode", "VAE Decode", {"samples": [g(95), 0], "vae": ["3", 0]})
    halt = widgets(gen, 86, ["disable", "method", "clear_queue", "alert_on_trigger"])
    api[g(86)] = node("HaltExecution", "Halt Execution (Audio VAE Last)",
                      {"input": [g(88), 0], "run_after": ["156", 0], **halt})
    api[g(14)] = node("VAEDecodeAudio", "VAE Decode Audio", {"samples": [g(86), 0], "vae": ["4", 0]})

    # ── RTX ───────────────────────────────────────────────────────────────────────────────────────
    api["39"] = node("RTXVideoSuperResolution", "RTX Video Super Resolution", {
        "images": [g(25), 0], **widgets(nodes, 39, ["resize_type", "resize_type.scale", "quality", "strength",
                                                    "image_encoding"])})
    api["160"] = node("ComfyMathExpression", "RTX multiplier: minimum 2",
                      {**widgets(nodes, 160, ["expression"]), "values.a": ["173", 0]})
    api["161"] = node("ComfyMathExpression", "Skip RTX: Continue mode or multiplier 1",
                      {**widgets(nodes, 161, ["expression"]), "values.a": ["173", 0], "values.b": ["103", 0]})
    api["155"] = node("RTXVideoFrameGeneration", "RTX Frame Generation", {
        "images": ["39", 0], "generation_type.multiplier": ["160", 1],
        **widgets(nodes, 155, ["generation_type", None, "mode", "automatic_shot_change_detection",
                               "shot_change", "image_encoding"])})
    api["156"] = node("ComfySwitchNode", "Skip FrameGen: Continue mode or 1x",
                      {"switch": ["161", 2], "on_false": ["155", 0], "on_true": ["39", 0]})

    # ── Continuation export (subgraph 152) ────────────────────────────────────────────────────────
    lokken = ["upscale", "upscale_method", "upscale_by", "interpolate", None, "interpolate_by", "dual_pass_rife",
              None, None, "saturation_mix", "compress", "crf", "batching_mode", "custom_batch_size", None,
              "h264_profile"]
    api[x(75)] = node("ComfySwitchNode", "Full / Preview filename prefix",
                      {"switch": ["94", 0], "on_false": ["73", 0], "on_true": ["74", 0]})
    api[x(112)] = node("GetImageSizeAndCount", "Current RTX output dimensions and frame count",
                       {"image": ["156", 0]})
    api[x(127)] = node("GetImageRangeFromBatch", "Cut selected overlap from START of NEW video", {
        "images": ["156", 0], "start_index": [g(140), 1], **widgets(exp, 127, [None, "num_frames"])})
    api[x(115)] = node("ComfySwitchNode", "Encode full Normal clip / trimmed Continue segment",
                       {"switch": ["103", 0], "on_false": ["156", 0], "on_true": [x(127), 0]})
    api[x(158)] = node("ComfyMathExpression", "Normal FPS = 24 x multiplier; Continue always 24",
                       {**widgets(exp, 158, ["expression"]), "values.a": ["173", 0], "values.b": ["103", 0]})
    api[x(176)] = node("ComfyMathExpression", "Grain enabled only when strength > 0",
                       {**widgets(exp, 176, ["expression"]), "values.a": ["174", 0]})
    api[x(135)] = node("LokkenVideoPostProcessing", "Lokken: current segment + optional grain", {
        "images": [x(115), 0], "original_fps": [x(158), 0], "grain": [x(176), 2], "grain_intensity": ["174", 0],
        "encoder_preset": encoder_preset, **widgets(exp, 135, lokken)})
    api[x(137)] = node("ConcatenateVideo", "Normal export: attach selected audio once", {
        "videos.video0": [x(135), 0], "complete_audio": [g(14), 0], **widgets(exp, 137, ["codec"])})

    api[x(128)] = node("GetImageSizeAndCount", "Previous clip dimensions", {"image": [g(105), 0]})
    api[x(113)] = node("ImageScale", "Match previous clip to current RTX output canvas", {
        "image": [g(105), 0], "width": [x(112), 1], "height": [x(112), 2],
        **widgets(exp, 113, ["upscale_method", None, None, "crop"])})
    api[x(129)] = node("ComfyMathExpression", "Do source and current RTX dimensions match?", {
        **widgets(exp, 129, ["expression"]), "values.a": [x(128), 1], "values.b": [x(128), 2],
        "values.c": [x(112), 1], "values.d": [x(112), 2]})
    api[x(130)] = node("ComfySwitchNode", "Same size: passthrough / Otherwise: resize",
                       {"switch": [x(129), 2], "on_false": [x(113), 0], "on_true": [x(128), 0]})
    lokken_src = ["upscale", "upscale_method", "upscale_by", "interpolate", "original_fps", "interpolate_by",
                  "dual_pass_rife", "grain", "grain_intensity", "saturation_mix", "compress", "crf",
                  "batching_mode", "custom_batch_size", None, "h264_profile"]
    api[x(136)] = node("LokkenVideoPostProcessing", "Lokken: source segment, grain OFF", {
        "images": [x(130), 0], "encoder_preset": encoder_preset, **widgets(exp, 136, lokken_src)})
    api[x(122)] = node("ComfyMathExpression", "New audio duration = (new frames - window) / 24", {
        **widgets(exp, 122, ["expression"]), "values.a": [x(112), 3], "values.b": [g(140), 1]})
    api[x(123)] = node("TrimAudioDuration", "Cut selected overlap from START of NEW audio",
                       {"audio": [g(14), 0], "start_index": [g(141), 0], "duration": [x(122), 0]})
    api[x(124)] = node("AudioConcat", "Stitched audio: source then trimmed continuation",
                       {"audio1": [g(121), 0], "audio2": [x(123), 0], **widgets(exp, 124, ["direction"])})
    api[x(114)] = node("ConcatenateVideo", "Keep ALL source frames + append trimmed NEW clip", {
        "videos.video0": [x(136), 0], "videos.video1": [x(135), 0], "complete_audio": [x(124), 0],
        **widgets(exp, 114, ["codec"])})
    api[x(138)] = node("ComfySwitchNode", "Normal / Continue: select VIDEO container",
                       {"switch": ["103", 0], "on_false": [x(137), 0], "on_true": [x(114), 0]})

    api[x(148)] = node("RegexReplace", "Keep selected filename; replace its folder", {
        "string": [x(75), 0], **widgets(exp, 148, [None, "regex_pattern", "replace", "case_insensitive",
                                                   "multiline", "dotall", "count"])})
    api[x(149)] = node("StringConcatenate", "Extension folder / selected filename", {
        "string_a": ["147", 0], "string_b": [x(148), 0], **widgets(exp, 149, [None, None, "delimiter"])})
    api[x(150)] = node("ComfySwitchNode", "Output folder: Normal original / Continue extension",
                       {"switch": ["103", 0], "on_false": [x(75), 0], "on_true": [x(149), 0]})

    api["40"] = node("SaveVideo", "Save Video", {
        "video": [x(138), 0], "filename_prefix": [x(150), 0], "format": nodes[40]["widgets_values"][1],
        "format.codec": nodes[40]["widgets_values"][2]})
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


# Values the tab always writes per run, so the server not listing the authored one is not a fault: the
# source video is uploaded at submit time, and the Turbo LoRA is resolved against the server's loras/
# (the author keeps it under Minimax_H3/, this server at the root).
PER_RUN = {("151:105", "video"), ("36", "lora_name")}


def validate(api, info):
    """Every class exists, every widget input is one the node declares, combos hold a listed value, and
    every link points at a node that is in the graph."""
    problems = []
    for nid, n in api.items():
        for name, value in n["inputs"].items():
            if isinstance(value, list) and len(value) == 2 and str(value[0]) not in api:
                problems.append(f"{nid}.{name} links to missing node {value[0]}")
        spec = info.get(n["class_type"])
        if spec is None:
            problems.append(f"{nid}: {n['class_type']} is not installed")
            continue
        declared = {}
        for sec in ("required", "optional"):
            declared.update(spec["input"].get(sec, {}))
        flexible = n["class_type"] in ("H3PowerLoraStack",)
        for name, value in n["inputs"].items():
            base = name.split(".")[0]
            if base not in declared:
                if not (flexible and name.startswith("lora_")):
                    problems.append(f"{nid} {n['class_type']}: unknown input '{name}'")
                continue
            if isinstance(value, list) or "." in name:
                continue  # a link, or a dynamic-combo child
            kind = declared[base][0]
            if (nid, name) in PER_RUN:
                continue
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

    nodes, subgraphs = load_source()
    api = build(nodes, subgraphs)

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
