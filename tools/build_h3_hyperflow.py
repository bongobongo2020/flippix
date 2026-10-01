#!/usr/bin/env python3
r"""
Builds the "HyperFlow" graph from the HyperFlow 8-step A/B render.

Source: workflow/video/h3-minimax/hyperflow.json   (a ComfyUI *authored* graph, no subgraphs)
Output: workflow/video/h3-minimax/h3-hyperflow.json (what H3 Express submits with 🌊 picked)

WHAT THE AUTHORED GRAPH IS
--------------------------
An A/B comparison, not a render: one reference picture, one prompt, and the same seed sampled twice
on the ref2va pruned checkpoint through two `ApplyHyperFlow` nodes that differ in exactly one widget —
`experimental_curve_refit`. The two takes are labelled ("curve_refit" / "Backbone_only"), stacked
and saved side by side. What this script keeps is the branch the comparison was made to test, the one
with the refit ON:

    UNETLoader            h3-minimax/minimax_h3_ref2va_pruned_int8_convrot
    ModelAttentionBackend comfy kitchen attention
    BlockSparseAttention  sol-attn, tau 1.0, start 0.16, dense_blocks "0,1", no extra tokens, no sinks
    ApplyHyperFlow        the pruned 8-step adapter at 1.00, merge, curve refit ON
    KSamplerSelect        euler
    SamplerCustomAdvanced reading ApplyHyperFlow's SIGMAS output — no scheduler anywhere

HyperFlow is Video Rebirth's 8-step LoRA for MiniMax-H3 plus a two-time (t, r) conditioning that only
the node can apply. The node hands back the trained 9-point sigma grid, and that grid IS the schedule:
there is no step count to choose, which is why the tab's steps dial is inert on this stack.

THE ACTION RECIPE LAYERED OVER THE EXPORT (2026-09-29)
-----------------------------------------------------
The export as authored rendered badly through H3 Express and MiniMax I2V. What replaced it is a recipe
measured by hand on fight clips, and every constant below that says RECIPE is from it:

    base            Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8   (curve fit bundled for it)
    HyperFlow       pruned build, curve refit ON, lora_mode BYPASS   (the export said merge)
    sparse attn     H3SLAAttention at 0.90, last on the model wire  (the export used Sol-Attn)
    upscale pass    the TaoMate 3-step LoRA REPLACES the HyperFlow engine, two steps
    user LoRAs      none recommended

"Replace engine LoRA" is the load-bearing line: the upscale pass is not sampled on the HyperFlow model.
It forks off the attention backend, loads `H3/taomate_h3_3step_comfy` instead, and runs a two-step
schedule. Asking for four upscale steps in the tool the recipe came from halved to one and softened
everything, which is why the pass has its own fixed schedule here (`hf:p2sigmas`) and the tab's
upscale-steps dial does not reach it.

WHY `ApplyHyperFlowH3` AND NOT THE EXPORT'S `ApplyHyperFlow`
-----------------------------------------------------------
The export was made with Saganaki22/ComfyUI-Hyperflow, whose repository is gone. The server runs its
continuation, Adudeguyman/ComfyUI-HyperFlow-H3, which renamed the node ids (`ApplyHyperFlowH3`, so the
two can be installed side by side) and kept the inputs: hyperflow_file, strength, lora_mode, variant,
download_if_missing, verbose, experimental_curve_refit — the export's seven widgets, in that order.
Its README says a workflow made with the original nodes "needs those nodes swapped", and the swap is
all this is.

The weights live in `models/hyperflow/`, a folder of the pack's own. A server without them lists a
placeholder in the combo and ComfyUI rejects the submit before the node's `download_if_missing` could
run, so the app's missing-model resolver is what fetches them (ModelCatalog knows both files).

WHY IT IS WRITTEN ON h3-eros.json's NODE IDS
--------------------------------------------
Same reason as every other Express stack: `22:11` prompt, `22:23`/`22:24` length, `22:8` steps, `22:9`
the draft canvas, `5` the reference node, `21` the LoRA seat, `171:1-4` the loaders, the three hunt
triples, `242`/`243`/`244` the latent upscale, `135:26`/`135:27` the finish pass, `165` RIFE, `34` the
sink. That is what keeps 🌊 a change of `WorkflowFileName`, `ShippedModel` and the authored step count
— the base `BuildFinish` drives it unchanged, and so does 🔗 chaining.

The stack's own nodes get readable ids:

    "hf:apply"    ApplyHyperFlowH3     MODEL out to the LoRA seat, SIGMAS out to the three drafts
    "hf:sla"      H3SLAAttention       last on the draft wire, after the LoRA seat
    "hf:p2lora"   LoraLoaderModelOnly  TaoMate 3-step - the upscale pass's engine instead of HyperFlow
    "hf:p2sla"    H3SLAAttention       last on the upscale pass's wire
    "hf:p2sigmas" ManualSigmas         the upscale pass's two steps

`22:7` is still a BasicScheduler so the file has the family's shape, but nothing reads it: the drafts
take `hf:apply`'s SIGMAS, and the tab's prune removes the scheduler.

WHERE THE TAB'S LoRA SEAT GOES
------------------------------
**After** the adapter. The curve refit is checkpoint-bound — the pack hashes the model it is handed and
falls back to backbone-only on anything it does not recognise, including "a modified MODEL". A user
LoRA spliced above `hf:apply` would silently turn the refit off; below it, the refit applies and the
LoRA rides on top.

DELIBERATE DIFFERENCES FROM THE AUTHORED GRAPH
----------------------------------------------
  * **One branch, not two.** The backbone-only branch, the labels, the side-by-side stack and both
    saves are the comparison, not the render.
  * **The three hunt branches and the latent-upscale finish added.** The author renders one take at
    1.5 MP. H3 Express hunts at a draft canvas and lifts the take to the Quality canvas with
    `MinimaxH3LatentUpscaler3D` and the short fixed-sigma pass every other stack uses; that pass runs
    on the HyperFlow model with euler, as authored. H3 Express prunes to one branch before submitting.
  * **`ModelPreviewOverrideKJ` dropped** — a browser preview of each step, nothing the render needs.
  * **`MiniMaxChunkFeedForward` dropped** — authored bypassed.
  * **The reference loader and the audio pair dropped.** The tab injects one `LoadImage` per cast
    panel; the `LoadAudio`/`TrimAudioDuration` pair feeds nothing in the export.
  * **The prompt is a placeholder**, overwritten on every clip.
  * **RIFE at `165`**, as on every stack but 🦠; the export has no interpolation at all.

Run:  python tools/build_h3_hyperflow.py [--object-info URL_OR_PATH] [--check]
"""
import argparse
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax", "hyperflow.json")
DST = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-hyperflow.json")
REFERENCE = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-singularity.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

OUT_SUBFOLDER = "h3_hyperflow"

# The server's node id for the export's ApplyHyperFlow. See the docstring.
APPLY_CLASS = "ApplyHyperFlowH3"
EXPORT_APPLY_CLASSES = ("ApplyHyperFlow", "ApplyHyperFlowH3")

# The three hunt branches, exactly as H3ErosViewModel.SampleBranches names them:
#   slot -> (noise, sampler, guider, video decode, audio decode, sink)
BRANCHES = [
    ("125:17", "125:12", "125:14", "176", "177", "18"),
    ("133:128", "133:129", "133:130", "187", "188", "134"),
    ("143:138", "143:139", "143:140", "180", "181", "144"),
]

# Fixed schedules for the upscale pass, by step count — the family's own values.
SIGMA_SCHEDULES = {
    "222": ("3 step Sigmas", "0.9035, 0.6316, 0.3158, 0.0000"),
    "221": ("4 step Sigmas", "0.9035, 0.8000, 0.6316, 0.3158, 0.0000"),
    "220": ("5 step Sigmas", "0.9231, 0.8780, 0.8000, 0.6316, 0.3158, 0.0000"),
}

# The length of HyperFlow's trained grid: nine sigmas, eight steps. Written into 22:8 for the family's
# shape; nothing reads it.
HYPERFLOW_STEPS = 8

# Inputs a node carries as browser-side widgets and never declares in /object_info.
WIDGET_ONLY_INPUTS = {
    "Power Lora Loader (rgthree)": {"PowerLoraLoaderHeaderWidget", "➕ Add Lora"},
}

# Combo values the server may legitimately not list yet: the weights are fetched by the app's
# missing-model resolver on first submit.
DOWNLOADED_ON_DEMAND = {("hyperflow_file",)}

PROMPT_PLACEHOLDER = (
    "The story's own clip prompt is written into this node at submit time — H3 Express overwrites it "
    "on every clip. Whatever is left here is never rendered."
)

BYPASSED = (2, 4)

# -- The action recipe (see the docstring). These override the export on purpose. --
RECIPE_UNET = "h3-minimax/Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8.safetensors"
RECIPE_LORA_MODE = "bypass"
RECIPE_SLA_SPARSITY = 0.90
PASS2_LORA = "H3/taomate_h3_3step_comfy.safetensors"
PASS2_LORA_STRENGTH = 1.0
# Two steps from the family's upscale re-noise level: the first two points of the 3-step schedule and
# then zero, so the pass keeps the draft's composition and the TaoMate LoRA resolves the detail.
PASS2_SIGMAS = "0.9035, 0.6316, 0.0000"

# H3SLAAttention's widgets, as every SLA call site in the app pins them (h3-parasyte.json's pk:sla):
# block 64 because H3 packs audio at 80 rows/s, min_seq_len 8192 so a draft canvas is not left dense.
SLA_INPUTS = {
    "block_size": "64", "min_seq_len": 8192, "dense_last_steps": 0, "protect_audio": False,
    "enabled": True, "dense_steps": "0", "dense_backend": "comfy_kitchen", "disable_fp16_accum": True,
    "stabilize_motion": False, "reference_protection": "Off", "tail_correction": False,
    "use_int8_qk": False, "engine": "comfy_kitchen",
}


# ── reading the authored graph ──────────────────────────────────────────────────────────────────

def load_source():
    """Every value the HyperFlow stack is, read out of the author's export."""
    with open(SRC, encoding="utf-8") as fh:
        ui = json.load(fh)

    name = os.path.basename(SRC)
    nodes = {n["id"]: n for n in ui["nodes"]}
    links = {l[0]: l for l in ui["links"]}          # id -> [id, from, from_slot, to, to_slot, type]

    def live(node_type):
        found = [n for n in ui["nodes"]
                 if n["type"] == node_type and n.get("mode", 0) not in BYPASSED]
        if not found:
            raise SystemExit(f"{name}: no live {node_type} node — the export changed.")
        return found

    def upstream(node, input_name):
        """The node feeding one of this node's inputs, or None."""
        for inp in node.get("inputs", []):
            if inp["name"] == input_name and inp.get("link") is not None:
                return nodes[links[inp["link"]][1]]
        return None

    # ── the branch under test: the adapter with the curve refit ON ───────────
    applies = [n for n in ui["nodes"]
               if n["type"] in EXPORT_APPLY_CLASSES and n.get("mode", 0) not in BYPASSED]
    if not applies:
        raise SystemExit(f"{name}: no live ApplyHyperFlow node — the export changed.")
    refit = [n for n in applies if len(n.get("widgets_values") or []) >= 7 and n["widgets_values"][6] is True]
    if len(refit) != 1:
        raise SystemExit(f"{name}: expected exactly one ApplyHyperFlow with experimental_curve_refit on, "
                         f"found {len(refit)} — the export changed.")
    apply = refit[0]
    hf_file, hf_strength, hf_mode, hf_variant, hf_download, hf_verbose, hf_refit = apply["widgets_values"][:7]

    # ── the model wire above it, walked rather than assumed ──────────────────
    chain = []
    cursor = upstream(apply, "model")
    while cursor is not None:
        chain.append(cursor)
        cursor = upstream(cursor, "model")
    live_chain = [n for n in chain if n.get("mode", 0) not in BYPASSED]
    types = [n["type"] for n in live_chain]
    for needed in ("UNETLoader", "ModelAttentionBackend", "BlockSparseAttention"):
        if needed not in types:
            raise SystemExit(f"{name}: the adapter's model wire has no live {needed} — the export changed "
                             f"(found {' <- '.join(types)}).")

    unet = next(n for n in live_chain if n["type"] == "UNETLoader")["widgets_values"]
    backend = next(n for n in live_chain if n["type"] == "ModelAttentionBackend")["widgets_values"]
    sparse = next(n for n in live_chain if n["type"] == "BlockSparseAttention")["widgets_values"]
    # [method, tau, start_percent, end_percent, dense_blocks, min_tokens, extra_tokens,
    #  sink_conditioning, verbose]
    if len(sparse) < 9 or sparse[0] != "sol-attn":
        raise SystemExit(f"{name}: BlockSparseAttention is not the sol-attn recipe — the export changed.")

    # ── sampler: the one KSamplerSelect the refit branch's sampler reads ─────
    sampler_node = next((n for n in live("SamplerCustomAdvanced")
                         if (s := upstream(n, "sigmas")) is not None and s["id"] == apply["id"]), None)
    if sampler_node is None:
        raise SystemExit(f"{name}: no sampler reads the refit adapter's SIGMAS — the export changed.")
    sampler_name = upstream(sampler_node, "sampler")["widgets_values"][0]

    clip = live("CLIPLoader")[0]["widgets_values"]
    vaes = [n["widgets_values"][0] for n in live("VAELoader")]
    video_vae = next((v for v in vaes if "audio" not in v.lower()), None)
    audio_vae = next((v for v in vaes if "audio" in v.lower()), None)
    if not video_vae or not audio_vae:
        raise SystemExit(f"{name}: the two VAELoaders do not look like a video/audio pair — the export changed.")

    frames_expr = live("ComfyMathExpression")[0]["widgets_values"][0]
    if "17" not in frames_expr:
        raise SystemExit(f"{name}: the frame-count expression does not mention the 17-frame stride — "
                         "the export changed.")

    # The author's install keeps H3 checkpoints at the top of diffusion_models; this server files them
    # under h3-minimax/, which is the name every other stack and the model dropdown use.
    unet_name = str(unet[0]).replace("\\", "/")
    if "/" not in unet_name:
        unet_name = f"h3-minimax/{unet_name}"

    return {
        "unet": unet_name,
        "weight_dtype": unet[1] if len(unet) > 1 else "default",
        "clip_name": clip[0],
        "clip_type": clip[1] if len(clip) > 1 else "minimax",
        "video_vae": video_vae,
        "audio_vae": audio_vae,
        "attention": backend[0],
        "sparse": {
            "selection": sparse[0], "selection.tau": float(sparse[1]),
            "start_percent": float(sparse[2]), "end_percent": float(sparse[3]),
            "dense_blocks": str(sparse[4]), "min_tokens": int(sparse[5]),
            "extra_tokens": int(sparse[6]), "sink_conditioning": str(sparse[7]),
            "verbose": bool(sparse[8]),
        },
        "hf_file": hf_file,
        "hf_strength": float(hf_strength),
        "hf_mode": hf_mode,
        "hf_variant": hf_variant,
        "hf_download": bool(hf_download),
        "hf_verbose": bool(hf_verbose),
        "hf_refit": bool(hf_refit),
        "sampler_name": sampler_name,
        "frames_expr": frames_expr,
    }


def load_reference():
    """h3-singularity.json's finish-side nodes, copied rather than restated: the latent upscaler, RIFE
    and the sinks carry widget values measured on this server that nothing here should drift from."""
    with open(REFERENCE, encoding="utf-8") as fh:
        return json.load(fh)


# ── emitting the API graph ──────────────────────────────────────────────────────────────────────

def node(class_type, title, inputs):
    return {"inputs": inputs, "class_type": class_type, "_meta": {"title": title}}


def build(src, ref):
    g = {}

    def copy(nid, **overrides):
        n = json.loads(json.dumps(ref[nid]))
        n["inputs"].update(overrides)
        return n

    # ── loaders ──────────────────────────────────────────────────────────────
    g["171:4"] = node("UNETLoader", "UNETLoader",
                      {"unet_name": RECIPE_UNET, "weight_dtype": src["weight_dtype"]})
    g["171:3"] = node("CLIPLoader", "CLIPLoader",
                      {"clip_name": src["clip_name"], "type": src["clip_type"], "device": "default"})
    g["171:2"] = node("VAELoader", "VAELoader", {"vae_name": src["video_vae"]})
    g["171:1"] = node("VAELoader", "VAELoader", {"vae_name": src["audio_vae"]})

    # ── the draft wire ───────────────────────────────────────────────────────
    # 171:4 -> 196 -> hf:apply -> 21 (the tab's LoRA seat) -> hf:sla -> the three drafts.
    # The LoRA seat sits after the adapter so a user LoRA never reaches the model the curve refit
    # fingerprints; SLA sits last, which is the only place it does anything.
    g["196"] = node("ModelAttentionBackend", "ModelAttentionBackend",
                    {"attention": src["attention"], "model": ["171:4", 0]})
    g["hf:apply"] = node(APPLY_CLASS, f"HyperFlow 8-step{' + curve refit' if src['hf_refit'] else ''}",
                         {"model": ["196", 0],
                          "hyperflow_file": src["hf_file"],
                          "strength": src["hf_strength"],
                          "lora_mode": RECIPE_LORA_MODE,
                          "variant": src["hf_variant"],
                          "download_if_missing": src["hf_download"],
                          "verbose": src["hf_verbose"],
                          "experimental_curve_refit": src["hf_refit"]})
    g["21"] = node("Power Lora Loader (rgthree)", "Power Lora Loader (rgthree)",
                   {"PowerLoraLoaderHeaderWidget": {"type": "PowerLoraLoaderHeaderWidget"},
                    "➕ Add Lora": "", "model": ["hf:apply", 0]})
    g["hf:sla"] = node("H3SLAAttention", f"H3 sparse attention {RECIPE_SLA_SPARSITY:.2f}",
                       dict(SLA_INPUTS, model=["21", 0], sparsity_ratio=RECIPE_SLA_SPARSITY))
    wire = ["hf:sla", 0]

    # ── the upscale pass's wire: TaoMate in place of the HyperFlow engine ────
    g["hf:p2lora"] = node("LoraLoaderModelOnly", f"TaoMate 3-step {PASS2_LORA_STRENGTH:.2f} (upscale pass)",
                          {"lora_name": PASS2_LORA, "strength_model": PASS2_LORA_STRENGTH,
                           "model": ["196", 0]})
    g["hf:p2sla"] = node("H3SLAAttention", f"H3 sparse attention {RECIPE_SLA_SPARSITY:.2f} (upscale pass)",
                         dict(SLA_INPUTS, model=["hf:p2lora", 0], sparsity_ratio=RECIPE_SLA_SPARSITY))
    hf_sigmas = ["hf:apply", 1]

    # ── prompt, length, canvas, steps ────────────────────────────────────────
    g["22:11"] = node("PrimitiveStringMultiline", "Prompt", {"value": PROMPT_PLACEHOLDER})
    g["22:23"] = node("PrimitiveFloat", "Video Length (seconds)", {"value": 8})
    g["22:24"] = node("ComfyMathExpression", "ComfyMathExpression",
                      {"expression": src["frames_expr"], "values.a": ["22:23", 0]})
    g["22:9"] = node("ResolutionSelector", "Resolution Selector (Size)",
                     {"aspect_ratio": "16:9 (Widescreen)", "megapixels": 0.3, "multiple": 32})
    g["22:8"] = node("INTConstant", "TOTAL STEPS (inert: HyperFlow's grid is the schedule)",
                     {"value": HYPERFLOW_STEPS})
    g["22:7"] = node("BasicScheduler", "BasicScheduler (unused — the drafts read HyperFlow's SIGMAS)",
                     {"scheduler": "simple", "steps": ["22:8", 0], "denoise": 1, "model": wire})
    g["22:6"] = node("KSamplerSelect", "KSamplerSelect", {"sampler_name": src["sampler_name"]})

    # ── conditioning ─────────────────────────────────────────────────────────
    g["5"] = node("MiniMaxH3ReferenceToVideo", "MiniMaxH3ReferenceToVideo",
                  {"prompt": ["22:11", 0], "width": ["22:9", 0], "height": ["22:9", 1],
                   "length": ["22:24", 1], "ref_image_size": "match",
                   "clip": ["171:3", 0], "audio_vae": ["171:1", 0], "vae": ["171:2", 0]})

    # ── the three hunt branches ──────────────────────────────────────────────
    for slot, (noise, sampler, guider, vdec, adec, sink) in enumerate(BRANCHES, start=1):
        g[noise] = node("RandomNoise", "RandomNoise", {"noise_seed": slot - 1})
        g[guider] = node("BasicGuider", "BasicGuider", {"model": wire, "conditioning": ["5", 0]})
        g[sampler] = node("SamplerCustomAdvanced", f"Sampler #{slot}",
                          {"noise": [noise, 0], "guider": [guider, 0], "latent_image": ["5", 1],
                           "sampler": ["22:6", 0], "sigmas": hf_sigmas})
        g[vdec] = node("VAEDecode", "VAEDecode", {"samples": [sampler, 0], "vae": ["171:2", 0]})
        g[adec] = node("VAEDecodeAudio", "VAEDecodeAudio",
                       {"samples": [sampler, 0], "vae": ["171:1", 0]})
        g[sink] = copy(BRANCHES[slot - 1][5],
                       filename_prefix=f"{OUT_SUBFOLDER}/preview_{slot}",
                       images=[vdec, 0], audio=[adec, 0])

    # ── the finish: the family's latent upscale + short fixed-sigma pass ─────
    g["242"] = node("LTXVSeparateAVLatent", "LTXVSeparateAVLatent", {"av_latent": [BRANCHES[0][1], 1]})
    g["243"] = copy("243", latent=["242", 0])
    g["244"] = node("LTXVConcatAVLatent", "LTXVConcatAVLatent",
                    {"video_latent": ["243", 0], "audio_latent": ["242", 1]})
    for nid, (title, sigmas) in SIGMA_SCHEDULES.items():
        g[nid] = node("ManualSigmas", title, {"sigmas": sigmas})
    g["135:30"] = node("KSamplerSelect", "KSamplerSelect", {"sampler_name": src["sampler_name"]})
    g["135:27"] = node("RandomNoise", "RandomNoise", {"noise_seed": 0})
    g["hf:p2sigmas"] = node("ManualSigmas", "Upscale pass - 2 steps (TaoMate)", {"sigmas": PASS2_SIGMAS})
    g["135:29"] = node("BasicGuider", "Upscale Guider (TaoMate)",
                       {"model": ["hf:p2sla", 0], "conditioning": ["5", 0]})
    g["135:26"] = node("SamplerCustomAdvanced", "Upscale Pass",
                       {"noise": ["135:27", 0], "guider": ["135:29", 0], "sampler": ["135:30", 0],
                        "sigmas": ["hf:p2sigmas", 0], "latent_image": ["244", 0]})
    g["clean"] = node("easy cleanGpuUsed", "Free VRAM before the decode", {"anything": ["135:26", 0]})
    g["189"] = node("VAEDecode", "Final Decode", {"samples": ["clean", 0], "vae": ["171:2", 0]})
    g["190"] = node("VAEDecodeAudio", "Final Audio Decode", {"samples": ["clean", 0], "vae": ["171:1", 0]})
    g["259"] = node("VAEDecode", "Single-pass Decode", {"samples": [BRANCHES[0][1], 1], "vae": ["171:2", 0]})
    g["258"] = node("VAEDecodeAudio", "Single-pass Audio Decode",
                    {"samples": [BRANCHES[0][1], 1], "vae": ["171:1", 0]})
    g["165"] = copy("165", images=["189", 0])
    g["34"] = copy("34", filename_prefix=f"{OUT_SUBFOLDER}/final", images=["165", 0], audio=["190", 0])
    return g


# ── validation ──────────────────────────────────────────────────────────────────────────────────

def load_object_info(where):
    if where.startswith("http"):
        with urllib.request.urlopen(where, timeout=120) as fh:
            return json.load(fh)
    with open(where, encoding="utf-8") as fh:
        return json.load(fh)


def reference_inputs():
    """Input keys the shipped h3 graphs use on each class — see build_h3_parasyte.py for why
    /object_info alone does not say every key is legitimate."""
    seen = {}
    for f in ("h3-eros.json", "h3-singularity.json"):
        try:
            with open(os.path.join(ROOT, "workflow", "video", "h3-minimax", f), encoding="utf-8") as fh:
                for n in json.load(fh).values():
                    if isinstance(n, dict) and "class_type" in n:
                        seen.setdefault(n["class_type"], set()).update(n.get("inputs", {}))
        except OSError:
            pass
    return seen


def check(graph, obj):
    """Every class exists, every declared input is real, every link points somewhere."""
    problems, notes = [], []
    reference = reference_inputs()
    for nid, n in graph.items():
        ct = n["class_type"]
        spec = obj.get(ct)
        if spec is None:
            problems.append(f"{nid}: class '{ct}' is not on the server")
            continue
        req, opt = spec["input"].get("required", {}), spec["input"].get("optional", {})
        known = set(req) | set(opt) | WIDGET_ONLY_INPUTS.get(ct, set()) | reference.get(ct, set())
        for key, val in n["inputs"].items():
            base = key.split(".")[0]
            if base not in known and key not in known:
                problems.append(f"{nid} ({ct}): input '{key}' is not in the node's schema")
            if isinstance(val, list) and len(val) == 2 and isinstance(val[0], str):
                if val[0] not in graph:
                    problems.append(f"{nid} ({ct}): input '{key}' links to missing node '{val[0]}'")
            # A combo whose value the server does not list.
            decl = req.get(key) or opt.get(key)
            if isinstance(val, str) and decl and isinstance(decl[0], list) and val not in decl[0]:
                msg = f"{nid} ({ct}): '{key}' = '{val}' is not in the server's list"
                (notes if (key,) in DOWNLOADED_ON_DEMAND else problems).append(msg)
        supplied = set(n["inputs"]) | {k.split(".")[0] for k in n["inputs"]}
        for key in req:
            if key not in supplied:
                problems.append(f"{nid} ({ct}): required input '{key}' is not set")
    return problems, notes


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    ap.add_argument("--object-info", default=OBJ_URL,
                    help="URL or path of a ComfyUI /object_info dump (default: %(default)s)")
    ap.add_argument("--check", action="store_true",
                    help="validate against /object_info and do not write the file")
    args = ap.parse_args()

    src = load_source()
    graph = build(src, load_reference())

    print(f"HyperFlow: {len(graph)} nodes  |  {src['sampler_name']} on the adapter's own "
          f"{HYPERFLOW_STEPS}-step grid")
    print(f"  checkpoint  {RECIPE_UNET}  (export: {src['unet']})")
    print(f"  adapter     {src['hf_file']} at {src['hf_strength']:.2f}, {RECIPE_LORA_MODE} "
          f"(export: {src['hf_mode']}), curve refit {'ON' if src['hf_refit'] else 'off'}  ({APPLY_CLASS})")
    print(f"  SLA         {RECIPE_SLA_SPARSITY:.2f}, last on both wires (the export's Sol-Attn is not used)")
    print(f"  upscale     {PASS2_LORA} at {PASS2_LORA_STRENGTH:.2f} in place of HyperFlow, "
          f"sigmas {PASS2_SIGMAS}")

    try:
        obj = load_object_info(args.object_info)
    except Exception as exc:                                    # noqa: BLE001
        if args.check:
            raise SystemExit(f"cannot read {args.object_info}: {exc}")
        print(f"  (skipping validation: {exc})")
        obj = None

    if obj is not None:
        problems, notes = check(graph, obj)
        for n in notes:
            print(f"  note: {n} — fetched by the app's missing-model resolver on first submit")
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
