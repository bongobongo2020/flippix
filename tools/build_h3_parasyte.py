#!/usr/bin/env python3
r"""
Builds the "Parasyte" graph from PlagueKind's MiniMax H3 Sparse Attention render.

Source: workflow/video/h3-minimax/plaguekindMinimaxH3SparseAttention_h3V11 (1).json
        (a ComfyUI *authored* graph: 18 top-level nodes over 5 nested subgraphs)
Output: workflow/video/h3-minimax/h3-parasyte.json
        (what H3 Express submits with 🦠 picked)

WHAT THE AUTHORED GRAPH IS
--------------------------
The current-generation PlagueKind H3 render, and it differs from every stack this repo already
ships in three ways that matter:

  * **Sparse attention on the model wire.** `H3SLAAttention` — block-sparse attention for H3 — sits
    last on the MODEL wire, feeding the guiders and schedulers directly. Measured 1.42x at sparsity
    0.85 and 1.63x at 0.90 on the 4090 (see the project notes); the 2.5x in circulation is an
    eight-GPU number. This is a speed patch and nothing else: it will never fix an OOM.
  * **The Parasyte turbo LoRA.** `H3/H3-PK-Parasyte-Turbo.safetensors` at 1.00, sampled
    `res_multistep`/`simple` at 13 steps. The author's export carries a second, switched-off turbo
    seat (`fasth3_6step`) which is not shipped here — see DELIBERATE DIFFERENCES.
  * **A one-node upscale finish.** `MMH3UltimateUpscale` replaces the `MinimaxH3LatentUpscaler3D` +
    second `SamplerCustomAdvanced` pair the other stacks use: it takes the noise, sampler and sigmas
    itself and hands back one **AV** latent, which is why both the video and the audio decode read
    its single output and there is no `LTXVConcatAVLatent` in this file at all.

WHY IT IS WRITTEN ON h3-eros.json's NODE IDS
--------------------------------------------
`H3ErosViewModel.ApplyCommonInputs` is shared by every stack, so every id it writes to carries the
class it expects: `22:11` prompt, `22:23` seconds, `22:24` the frame-count expression, `22:8` steps,
`22:9` the draft canvas, `22:6`/`22:7` the sampler and scheduler, `5` the reference node, `21` the
empty Power Lora Loader seat, `171:1-4` the loaders, `34` the sink, and the three (noise, sampler,
sink) triples. That is what keeps 🦠 a change of `WorkflowFileName`, `ShippedModel` and
`AuthoredFirstPassSteps` plus one `BuildFinish` override — and no second render path.

The conditioning node is deliberately `MiniMaxH3ReferenceToVideo` (id `5`), **not** the author's
`PK_MiniMaxH3Combined`. The two are near-identical for this tab's purposes, but they disagree on the
autogrow key the tab injects cast panels into (`ref_images.ref_image_N` vs
`reference_images.reference_image_N`), and `AttachReferences` is shared by four view models. The
author's extras that the swap gives up — `first_frame`/`last_frame` slots and the RefMod bundle —
are not driven by H3 Express or MiniMax I2V today.

The stack's own nodes, which have no counterpart in h3-eros.json, get readable ids:

    "pk:turbo"   LoraLoaderModelOnly              the Parasyte turbo LoRA at 1.00
    "pk:sla"     H3SLAAttention                   last on the wire, feeding guiders + schedulers
    "pk:upmodel" MMH3LatentUpscaleWithModelParams the finished canvas, as a param object
    "pk:ult"     MMH3UltimateUpscale              upscale and second pass in one node
    "pk:film"    FrameInterpolationModelLoader    FILM weights for the fps checkbox

THE FINISH, AND WHAT `165` IS HERE
----------------------------------
`165` is the id the shared `WireSink` puts between the decode and the mux when the fps checkbox is
ticked. On every other stack that id is `RIFEInterpolation`; here it is `FrameInterpolate` (FILM
x2), which is what the authored graph interpolates with. The two nodes take different inputs, so
`H3ErosViewModel.WireInterpolation` is the seam that writes whichever one the stack ships —
`H3ExpressViewModel.Parasyte.cs` overrides it.

DELIBERATE DIFFERENCES FROM THE AUTHORED GRAPH
----------------------------------------------
  * **`sparsity_ratio` 0.85, not the export's 0.70.** The 0.90 on the inner `H3SLAAttention` node is
    a leftover: the Main Model Loader subgraph promotes `sparsity_ratio` to its edge, and the placed
    node carries **0.70**, which is what that export actually renders. 0.85 is what every other SLA
    call site in this app pins, it is lightx2v's shipped value, and it is what the SLA turbo LoRA was
    distilled against — between the author's 0.70 and the 0.90 the node defaults toward. Below ~0.60
    the kernel is slower than dense, so 0.70 is a mild setting rather than an aggressive one.
  * **`block_size` "64", not the export's "32".** What the rest of the app pins. H3 packs audio at
    80 rows/sec, so the rule is never 128; 32 and 64 are both safe and 64 is the faster of the two.
  * **`min_seq_len` 8192, not 12288.** Above the export's value a draft-canvas pass silently falls
    back to dense, which is the failure mode that reads as "SLA did nothing".
  * **The three hunt branches added.** The author renders one take. H3 Express prunes to one branch
    before submitting, so the other two cost nothing; they are here so the file is the same shape as
    h3-eros.json and h3-bunny.json, and so any tab in this family can drive it.
  * **The reference loaders dropped.** The tab uploads one cast panel per `ref_image_N` slot and
    injects a `LoadImage` per panel at submit time, so none are shipped.
  * **Tiling and temporal chunking dropped.** Both are behind `ComfySwitchNode`s the author ships
    **off**, so `MMH3UltimateUpscale` gets neither `spatial_split_param` nor `temporal_split_param`
    here rather than being handed a switch that resolves to nothing.
  * **`RTXVideoSuperResolution` and `ImageSharpenKJ` dropped.** Both authored bypassed, at the very
    end of the chain. 🍥 TaoMate is the stack that finishes in frame space; this one finishes in the
    latent, and a stack switch should not quietly change that.
  * **The RefMod loader, the song/audio context and the second audio pair dropped.** `H3_refmod_loader`
    is authored empty, `MiniMaxH3SongMaskedAVContext` is bypassed *and* absent from this server, and
    the two `LoadAudio`/`TrimAudioDuration` pairs are bypassed. H3 Express has no audio input.
  * **`H3MiniMaxCache` dropped.** Behind the authored "Enable Cache" switch, off.
  * **The prompt is a placeholder**, overwritten on every clip.
  * **The `MarkdownNote` and the rgthree bypasser panels dropped** — browser-side only.

Run:  python tools/build_h3_parasyte.py [--object-info URL_OR_PATH] [--check]
"""
import argparse
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax",
                   "plaguekindMinimaxH3SparseAttention_h3V11 (1).json")
DST = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-parasyte.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

OUT_SUBFOLDER = "h3_parasyte"

# H3 Express is always all-reference: every clip conditions on cast panels wired into
# MiniMaxH3ReferenceToVideo's ref_images slots, never on a first or last frame. The authored graph
# already loads the fl2va/ref2va hybrid, so this is the export's own pick rather than a substitution.
REFERENCE_MODEL = "h3-minimax/minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors"

# What the rest of the app pins at every H3SLAAttention call site. See the docstring.
SLA_SPARSITY = 0.85
SLA_BLOCK_SIZE = "64"
SLA_MIN_SEQ_LEN = 8192

# The three hunt branches, exactly as H3ErosViewModel.SampleBranches names them:
#   slot -> (noise, sampler, guider, video decode, audio decode, sink)
BRANCHES = [
    ("125:17", "125:12", "125:14", "176", "177", "18"),
    ("133:128", "133:129", "133:130", "187", "188", "134"),
    ("143:138", "143:139", "143:140", "180", "181", "144"),
]

# Fixed schedules for the upscale pass, by step count. Same values h3-eros.json ships; the tab links
# one of them into MMH3UltimateUpscale's sigmas from its ⬆ steps dial.
SIGMA_SCHEDULES = {
    "222": ("3 step Sigmas", "0.9035, 0.6316, 0.3158, 0.0000"),
    "221": ("4 step Sigmas", "0.9035, 0.8000, 0.6316, 0.3158, 0.0000"),
    "220": ("5 step Sigmas", "0.9231, 0.8780, 0.8000, 0.6316, 0.3158, 0.0000"),
}

# Inputs a node carries as browser-side widgets and never declares in /object_info.
WIDGET_ONLY_INPUTS = {
    "Power Lora Loader (rgthree)": {"PowerLoraLoaderHeaderWidget", "➕ Add Lora"},
}

PROMPT_PLACEHOLDER = (
    "The story's own clip prompt is written into this node at submit time — H3 Express overwrites it "
    "on every clip. Whatever is left here is never rendered."
)

# ComfyUI serialises a bypassed node as mode 4 and a muted one as 2.
BYPASSED = (2, 4)


# ── reading the authored graph ──────────────────────────────────────────────────────────────────

def load_source():
    """Every value the Parasyte stack is, read out of the author's export."""
    with open(SRC, encoding="utf-8") as fh:
        ui = json.load(fh)

    subs = {s["id"]: s for s in ui.get("definitions", {}).get("subgraphs", [])}
    by_name = {s["name"]: s for s in subs.values()}

    def sub(name):
        if name not in by_name:
            raise SystemExit(f"{os.path.basename(SRC)}: no '{name}' subgraph — the export changed.")
        return by_name[name]

    def nodes_of(graph, node_type, only_live=True):
        found = [n for n in graph["nodes"]
                 if n["type"] == node_type and (not only_live or n.get("mode", 0) not in BYPASSED)]
        if not found:
            raise SystemExit(f"{os.path.basename(SRC)}: no {'live ' if only_live else ''}"
                             f"{node_type} node — the export changed.")
        return found

    def widgets(graph, node_type, index=0):
        return nodes_of(graph, node_type)[index].get("widgets_values") or []

    def promoted(parent, graph, label, minimum):
        """A subgraph's promoted widgets, by input name, off the node that places it.

        A ComfyUI subgraph can lift an inner node's widget to its own edge; the placed node then
        carries the live value and the inner node keeps whatever it was last left on. The placed
        node's `widgets_values` are the subgraph's widget-typed inputs in declaration order, so
        pairing the two is what reads the graph as it actually renders.
        """
        holder = next((n for n in parent["nodes"] if n["type"] == graph["id"]), None)
        if holder is None:
            raise SystemExit(f"{os.path.basename(SRC)}: the '{label}' subgraph is not placed.")
        values = holder.get("widgets_values") or []
        if len(values) < minimum:
            raise SystemExit(f"{os.path.basename(SRC)}: '{label}' has {len(values)} promoted "
                             f"widgets, expected at least {minimum} — the export changed.")
        return dict(zip((i["name"] for i in graph.get("inputs", [])), values))

    top = ui
    loader = sub("Main Model Loader")
    cond = sub("Conditioning - Sampler")
    enh = sub("Enhancements")
    ups = sub("upscale settings inside")

    # ── the model set ────────────────────────────────────────────────────────
    # The Main Model Loader subgraph PROMOTES its loaders' widgets, so the values that actually
    # render are the ones on the placed node, not the defaults still sitting on the inner
    # VAELoader/CLIPLoader/UNETLoader. Reading the inner nodes gets you the subgraph author's
    # leftovers — an obsolete gemma text encoder, in this export's case.
    ml = promoted(top, loader, "Main Model Loader", minimum=4)
    video_vae = ml["vae_name"]
    audio_vae = ml["vae_name_1"]
    clip_name = ml["clip_name"]
    clip_type = (widgets(loader, "CLIPLoader") + ["", "minimax"])[1]
    if "audio" in video_vae.lower() or "audio" not in audio_vae.lower():
        raise SystemExit(f"{os.path.basename(SRC)}: the Main Model Loader's two VAE slots do not "
                         "look like a video/audio pair — the export changed.")

    # ── the model wire ───────────────────────────────────────────────────────
    ff = widgets(loader, "MiniMaxChunkFeedForward")          # [chunks, seq_threshold]
    lowvram = widgets(loader, "MiniMaxLowVRAMAttention")     # [head_chunks]
    sla = widgets(loader, "H3SLAAttention")

    # ── the Parasyte turbo LoRA ──────────────────────────────────────────────
    # LTX_lora_loader carries its stack as a JSON string; take the entries the author left ON.
    stack_raw = (widgets(top, "LTX_lora_loader") + ["", "[]"])[1]
    try:
        entries = json.loads(stack_raw)
    except (TypeError, ValueError):
        raise SystemExit(f"{os.path.basename(SRC)}: LTX_lora_loader's stack is not JSON.")
    active = [e for e in entries if e.get("on")]
    if len(active) != 1:
        raise SystemExit(f"{os.path.basename(SRC)}: expected exactly one enabled LoRA in the "
                         f"LTX_lora_loader stack, found {len(active)} — the export changed.")
    turbo_name = str(active[0]["lora"]).replace("\\", "/")
    turbo_strength = float(active[0].get("str", 1.0))

    # ── sampler, scheduler, steps ────────────────────────────────────────────
    # The Conditioning-Sampler subgraph promotes these, so the *parent* node's widget values win
    # over the inner KSamplerSelect/BasicScheduler defaults. The promoted inputs are, in order:
    #   width, height, sampler_name, scheduler, steps, value(seconds), ref_image_size, switch,
    #   prompt, vlm_resolution, vlm_video_resolution, reference_fps
    cs = promoted(top, cond, "Conditioning - Sampler", minimum=5)
    sampler_name, scheduler, steps = str(cs["sampler_name"]), str(cs["scheduler"]), int(cs["steps"])

    # ── the finish ───────────────────────────────────────────────────────────
    up = widgets(ups, "MMH3LatentUpscaleWithModelParams")    # [model_name, w, h, device, precision]
    sigmas_default = (widgets(ups, "ManualSigmas") + [""])[0]
    film_model = (widgets(enh, "FrameInterpolationModelLoader") + [""])[0]
    film_mult = int((widgets(enh, "FrameInterpolate") + [2])[0])

    # The frame-count expression, read off the authored graph rather than written here.
    frames_expr = (widgets(cond, "ComfyMathExpression") + [""])[0]
    if "17" not in frames_expr:
        raise SystemExit(f"{os.path.basename(SRC)}: the frame-count expression does not mention the "
                         "17-frame stride — the export changed.")

    return {
        "unet": REFERENCE_MODEL,
        "clip_name": clip_name,
        "clip_type": clip_type or "minimax",
        "video_vae": video_vae,
        "audio_vae": audio_vae,
        "ff_chunks": int(ff[0]) if ff else 2,
        "ff_threshold": int(ff[1]) if len(ff) > 1 else 4096,
        "lowvram_chunks": int(lowvram[0]) if lowvram else 4,
        "authored_sparsity": float(ml["sparsity_ratio"]),
        "sla_dense_backend": str(sla[7]) if len(sla) > 7 else "comfy_kitchen",
        "sla_engine": str(sla[13]) if len(sla) > 13 else "comfy_kitchen",
        "sla_int8_qk": bool(sla[12]) if len(sla) > 12 else False,
        "turbo_name": turbo_name,
        "turbo_strength": turbo_strength,
        "sampler_name": sampler_name,
        "scheduler": scheduler,
        "steps": steps,
        "upscaler_model": up[0] if up else "minimax_h3_latent_upscaler_3d_bf16.safetensors",
        "upscale_precision": up[4] if len(up) > 4 else "bf16",
        "sigmas_default": sigmas_default,
        "film_model": film_model,
        "film_multiplier": film_mult,
        "frames_expr": frames_expr,
    }


# ── emitting the API graph ──────────────────────────────────────────────────────────────────────

def node(class_type, title, inputs):
    return {"inputs": inputs, "class_type": class_type, "_meta": {"title": title}}


def build(src):
    g = {}

    # ── loaders ──────────────────────────────────────────────────────────────
    g["171:4"] = node("UNETLoader", "UNETLoader",
                      {"unet_name": src["unet"], "weight_dtype": "default"})
    g["171:3"] = node("CLIPLoader", "CLIPLoader",
                      {"clip_name": src["clip_name"], "type": src["clip_type"], "device": "default"})
    g["171:2"] = node("VAELoader", "VAELoader", {"vae_name": src["video_vae"]})
    g["171:1"] = node("VAELoader", "VAELoader", {"vae_name": src["audio_vae"]})

    # ── the model wire ───────────────────────────────────────────────────────
    # 171:4 -> 193 -> 305 -> 196 -> 21 (the tab's LoRA seat) -> pk:turbo -> pk:sla -> consumers.
    # SLA goes LAST, feeding the guiders and schedulers directly: that placement is the whole
    # reason the patch does anything.
    g["193"] = node("MiniMaxChunkFeedForward", "MiniMaxChunkFeedForward",
                    {"chunks": src["ff_chunks"], "seq_threshold": src["ff_threshold"],
                     "model": ["171:4", 0]})
    g["305"] = node("MiniMaxLowVRAMAttention", "MiniMaxLowVRAMAttention",
                    {"head_chunks": src["lowvram_chunks"], "model": ["193", 0]})
    g["196"] = node("ModelAttentionBackend", "ModelAttentionBackend",
                    {"attention": "comfy kitchen attention", "model": ["305", 0]})
    g["21"] = node("Power Lora Loader (rgthree)", "Power Lora Loader (rgthree)",
                   {"PowerLoraLoaderHeaderWidget": {"type": "PowerLoraLoaderHeaderWidget"},
                    "➕ Add Lora": "", "model": ["196", 0]})
    g["pk:turbo"] = node("LoraLoaderModelOnly",
                         f"Parasyte turbo {src['turbo_strength']:.2f}",
                         {"lora_name": src["turbo_name"],
                          "strength_model": src["turbo_strength"],
                          "model": ["21", 0]})
    g["pk:sla"] = node("H3SLAAttention", "H3 sparse attention",
                       {"model": ["pk:turbo", 0],
                        "sparsity_ratio": SLA_SPARSITY,
                        "block_size": SLA_BLOCK_SIZE,
                        "min_seq_len": SLA_MIN_SEQ_LEN,
                        "dense_last_steps": 0,
                        "protect_audio": False,
                        "enabled": True,
                        "dense_steps": "0",
                        "dense_backend": src["sla_dense_backend"],
                        "disable_fp16_accum": True,
                        "stabilize_motion": False,
                        "reference_protection": "Off",
                        "tail_correction": False,
                        "use_int8_qk": src["sla_int8_qk"],
                        "engine": src["sla_engine"]})
    wire = ["pk:sla", 0]

    # ── prompt, length, canvas, steps ────────────────────────────────────────
    g["22:11"] = node("PrimitiveStringMultiline", "Prompt", {"value": PROMPT_PLACEHOLDER})
    g["22:23"] = node("PrimitiveFloat", "Video Length (seconds)", {"value": 6})
    g["22:24"] = node("ComfyMathExpression", "ComfyMathExpression",
                      {"expression": src["frames_expr"], "values.a": ["22:23", 0]})
    g["22:9"] = node("ResolutionSelector", "Resolution Selector (Size)",
                     {"aspect_ratio": "9:16 (Portrait Widescreen)", "megapixels": 0.3,
                      "multiple": 32})
    g["22:8"] = node("INTConstant", "TOTAL STEPS", {"value": src["steps"]})
    g["22:7"] = node("BasicScheduler", "BasicScheduler",
                     {"scheduler": src["scheduler"], "steps": ["22:8", 0], "denoise": 1,
                      "model": wire})
    g["22:6"] = node("KSamplerSelect", "KSamplerSelect", {"sampler_name": src["sampler_name"]})

    # ── conditioning ─────────────────────────────────────────────────────────
    # No ref_image_N inputs: the tab injects one LoadImage per cast panel at submit time.
    g["5"] = node("MiniMaxH3ReferenceToVideo", "MiniMaxH3ReferenceToVideo",
                  {"prompt": ["22:11", 0], "width": ["22:9", 0], "height": ["22:9", 1],
                   "length": ["22:24", 1], "ref_image_size": "match",
                   "clip": ["171:3", 0], "audio_vae": ["171:1", 0], "vae": ["171:2", 0]})

    # ── the three hunt branches ──────────────────────────────────────────────
    for slot, (noise, sampler, guider, vdec, adec, sink) in enumerate(BRANCHES, start=1):
        g[noise] = node("RandomNoise", "RandomNoise", {"noise_seed": 0})
        g[guider] = node("BasicGuider", "BasicGuider",
                         {"model": wire, "conditioning": ["5", 0]})
        g[sampler] = node("SamplerCustomAdvanced", f"Sampler #{slot}",
                          {"noise": [noise, 0], "guider": [guider, 0], "latent_image": ["5", 1],
                           "sampler": ["22:6", 0], "sigmas": ["22:7", 0]})
        g[vdec] = node("VAEDecode", "VAEDecode", {"samples": [sampler, 0], "vae": ["171:2", 0]})
        g[adec] = node("VAEDecodeAudio", "VAEDecodeAudio",
                       {"samples": [sampler, 0], "vae": ["171:1", 0]})
        g[sink] = node("VHS_VideoCombine", f"Preview {slot}",
                       {"frame_rate": 24, "loop_count": 0,
                        "filename_prefix": f"{OUT_SUBFOLDER}/preview_{slot}",
                        "format": "video/h264-mp4", "pix_fmt": "yuv420p", "crf": 19,
                        "save_metadata": False, "trim_to_audio": False, "pingpong": False,
                        "save_output": True, "images": [vdec, 0], "audio": [adec, 0]})

    # ── the finish ───────────────────────────────────────────────────────────
    # MMH3UltimateUpscale is the upscale AND the second pass in one node, and it hands back an AV
    # latent — so both decodes read its single output and there is no concat.
    for nid, (title, sigmas) in SIGMA_SCHEDULES.items():
        g[nid] = node("ManualSigmas", title, {"sigmas": sigmas})

    g["135:30"] = node("KSamplerSelect", "KSamplerSelect", {"sampler_name": src["sampler_name"]})
    g["135:27"] = node("RandomNoise", "RandomNoise", {"noise_seed": 0})
    g["pk:upmodel"] = node("MMH3LatentUpscaleWithModelParams", "Latent upscale (finished canvas)",
                           {"model_name": src["upscaler_model"], "width": 1280, "height": 704,
                            "device": "cuda", "precision": src["upscale_precision"]})
    g["pk:ult"] = node("MMH3UltimateUpscale", "Upscale Pass",
                       {"model": wire, "conditioning": ["5", 0],
                        "latent": [BRANCHES[0][1], 1], "noise": ["135:27", 0],
                        "sampler": ["135:30", 0], "sigmas": ["221", 0], "cfg": 1.0,
                        "latent_upscale_param": ["pk:upmodel", 0]})

    g["189"] = node("VAEDecode", "Final Decode", {"samples": ["pk:ult", 0], "vae": ["171:2", 0]})
    g["190"] = node("VAEDecodeAudio", "Final Audio Decode",
                    {"samples": ["pk:ult", 0], "vae": ["171:1", 0]})

    # The graph's "skip the upscale" decode of the picked latent. Nothing consumes it here, so the
    # tab's prune removes it; it exists so BuildFinish can repoint it like every other stack's.
    g["259"] = node("VAEDecode", "Single-pass Decode",
                    {"samples": [BRANCHES[0][1], 1], "vae": ["171:2", 0]})
    g["258"] = node("VAEDecodeAudio", "Single-pass Audio Decode",
                    {"samples": [BRANCHES[0][1], 1], "vae": ["171:1", 0]})

    # ── the fps checkbox ─────────────────────────────────────────────────────
    # `165` is the id the shared WireSink links in; here it is FILM rather than RIFE.
    g["pk:film"] = node("FrameInterpolationModelLoader", "FILM weights",
                        {"model_name": src["film_model"]})
    g["165"] = node("FrameInterpolate", f"FILM x{src['film_multiplier']}",
                    {"interp_model": ["pk:film", 0], "images": ["189", 0],
                     "multiplier": src["film_multiplier"]})

    g["34"] = node("VHS_VideoCombine", "Final Video",
                   {"frame_rate": 48, "loop_count": 0,
                    "filename_prefix": f"{OUT_SUBFOLDER}/final",
                    "format": "video/h264-mp4", "pix_fmt": "yuv420p", "crf": 16,
                    "save_metadata": False, "trim_to_audio": False, "pingpong": False,
                    "save_output": True, "images": ["165", 0], "audio": ["190", 0]})
    return g


# ── validation ──────────────────────────────────────────────────────────────────────────────────

def load_object_info(where):
    if where.startswith("http"):
        with urllib.request.urlopen(where, timeout=120) as fh:
            return json.load(fh)
    with open(where, encoding="utf-8") as fh:
        return json.load(fh)


def reference_inputs():
    """Input keys h3-eros.json — the shipped, known-good file — uses on each class.

    /object_info does not describe everything a node accepts: VHS_VideoCombine's `pix_fmt`, `crf`,
    `save_metadata` and `trim_to_audio` only exist once `format` selects a video container, and
    ComfyMathExpression takes its operands as `values.a` rather than a `values` object. Both are
    house style in every H3 graph here, so the reference file is what says a key is legitimate.
    """
    path = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-eros.json")
    seen = {}
    try:
        with open(path, encoding="utf-8") as fh:
            for n in json.load(fh).values():
                if isinstance(n, dict) and "class_type" in n:
                    seen.setdefault(n["class_type"], set()).update(n.get("inputs", {}))
    except OSError:
        pass
    return seen


def check(graph, obj):
    """Every class exists, every declared input is real, every link points somewhere."""
    problems = []
    reference = reference_inputs()
    for nid, n in graph.items():
        ct = n["class_type"]
        spec = obj.get(ct)
        if spec is None:
            problems.append(f"{nid}: class '{ct}' is not on the server")
            continue
        known = set(spec["input"].get("required", {})) | set(spec["input"].get("optional", {}))
        known |= WIDGET_ONLY_INPUTS.get(ct, set())
        known |= reference.get(ct, set())
        for key, val in n["inputs"].items():
            base = key.split(".")[0]
            if base not in known and key not in known:
                problems.append(f"{nid} ({ct}): input '{key}' is not in the node's schema")
            if isinstance(val, list) and len(val) == 2 and isinstance(val[0], str):
                if val[0] not in graph:
                    problems.append(f"{nid} ({ct}): input '{key}' links to missing node '{val[0]}'")
        # A dotted sub-widget satisfies its base: `values.a` is how ComfyMathExpression is fed.
        supplied = set(n["inputs"]) | {k.split(".")[0] for k in n["inputs"]}
        for key in spec["input"].get("required", {}):
            if key not in supplied:
                problems.append(f"{nid} ({ct}): required input '{key}' is not set")
    return problems


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    ap.add_argument("--object-info", default=OBJ_URL,
                    help="URL or path of a ComfyUI /object_info dump (default: %(default)s)")
    ap.add_argument("--check", action="store_true",
                    help="validate against /object_info and do not write the file")
    args = ap.parse_args()

    src = load_source()
    graph = build(src)

    print(f"Parasyte: {len(graph)} nodes  |  {src['sampler_name']}/{src['scheduler']} "
          f"at {src['steps']} steps")
    print(f"  checkpoint  {src['unet']}")
    print(f"  turbo LoRA  {src['turbo_name']} at {src['turbo_strength']:.2f}")
    print(f"  SLA         sparsity {SLA_SPARSITY} (author's promoted value: "
          f"{src['authored_sparsity']}) block {SLA_BLOCK_SIZE} "
          f"min_seq_len {SLA_MIN_SEQ_LEN} engine {src['sla_engine']}")
    print(f"  finish      MMH3UltimateUpscale + FILM x{src['film_multiplier']} "
          f"({src['film_model']})")

    try:
        obj = load_object_info(args.object_info)
    except Exception as exc:                                    # noqa: BLE001
        if args.check:
            raise SystemExit(f"cannot read {args.object_info}: {exc}")
        print(f"  (skipping validation: {exc})")
        obj = None

    if obj is not None:
        problems = check(graph, obj)
        if problems:
            print(f"\n{len(problems)} problem(s):")
            for p in problems:
                print("  " + p)
            raise SystemExit(1)
        print("  validated against /object_info: OK")

    if args.check:
        print("\n--check: not written.")
        return

    with open(DST, "w", encoding="utf-8") as fh:
        json.dump(graph, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    print(f"\nwrote {os.path.relpath(DST, ROOT)}")


if __name__ == "__main__":
    sys.exit(main())
