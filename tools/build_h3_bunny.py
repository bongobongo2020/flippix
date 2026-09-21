#!/usr/bin/env python3
r"""
Builds the "BUNNY" graph from the author's MiniMax H3 12GB Universal FL2VA render.

Source: workflow/video/h3-minimax/bunny-authored.json  (ComfyUI UI graph, 33 nodes)
Output: workflow/video/h3-minimax/h3-bunny.json        (what H3 Express submits with 🐰 picked)

WHAT THE AUTHORED GRAPH IS
--------------------------
A single-take reference-to-video render tuned for fight choreography. Its novelty is a **sigma
split**: one schedule is built once and sampled in two halves by two `SamplerCustomAdvanced` nodes.

    BasicScheduler            simple, 8 steps, denoise 1
    ExtendIntermediateSigmas  3 extra steps inserted between sigma 0.65 and 0.28, sine spacing
    SplitSigmasDenoise        0.25 -> high_sigmas (the first 75%), low_sigmas (the last 25%)

    stage 1  "武打动作结构" — the action structure.  RandomNoise, the high sigmas, the Combat
             LoRA at 1.00.  This is the pass that decides the choreography, and the author's own
             tuning note is: do not touch it first, because a fixed seed's motion trajectory is
             what it produces.
    stage 2  "清噪与细节收敛" — noise, ghosting and texture smear cleaned off the tail.
             `DisableNoise`, the low sigmas, the same Combat LoRA at 0.65, and the stage-1 latent
             as its `latent_image`.  It does not re-noise: it simply finishes the schedule.

Both the video and the audio are decoded from **stage 2's** latent.

There is exactly one seed in the whole graph, and no upscale of any kind.

WHAT THIS BUILD IS FOR
----------------------
⚡ H3 Express renders folders of story files unattended: one seed per clip, chosen for it, no board
and nobody picking. So the two things the authored graph is missing for that are

  * **the cast.** The author loads two `LoadImage` nodes by hand (their own
    `cast_1_…png` / `cast_2_…png`) into `MiniMaxH3ReferenceToVideo`'s autogrow `ref_images` slots.
    The tab uploads a panel per cast member per clip and injects one `LoadImage` per slot at submit
    time, so no reference loaders are shipped here at all.
  * **the upscale.** The authored graph muxes whatever the sampler painted. Every H3 Express clip is
    composed at the cheap draft canvas and lifted to the Quality one by `MinimaxH3LatentUpscaler3D`
    followed by a short fixed-sigma pass — the same finish the H3 Eros and ✴️ Singularity stacks use,
    which is why it is grafted on here rather than invented (see tools/build_h3_singularity.py,
    which did the same to the author's same-canvas denoise-0.2 refine).

So: the author's sigma-split relay becomes the first pass, its stage-2 latent is what the tab's
finish upscales, and the clip's one seed still sits in one `RandomNoise`.

NODE IDS ARE DELIBERATELY h3-eros.json's
----------------------------------------
`H3ErosViewModel.ApplyCommonInputs` and `BuildFinish` are shared by every stack, so every id they
write to carries the class they expect: `22:11` prompt, `22:23` seconds, `22:24` the frame-count
expression, `22:8` steps, `22:9` the draft canvas, `22:6`/`22:7` the sampler and scheduler, `5` the
reference node, the three (noise, sampler, sink) triples, `242`/`243`/`244`, `135:26`/`135:27`,
`222`/`221`/`220`, `189`/`190`, `259`/`258`, `165`, `34`, `171:1-4` the loaders, and `21` the empty
Power Lora Loader seat the tab's optional LoRA is spliced into. That is what makes 🐰 a change of
`WorkflowFileName`, `ShippedModel` and `FirstPassSteps` and **nothing else** — no second render path,
and no `BuildFinish` override: the id the tab thinks of as "the sampler that made the take"
(`125:12` for slot 1) is this stack's stage-2 sampler, which is exactly the latent to upscale.

The stack's own nodes, which have no counterpart in h3-eros.json, get readable ids:

    "bn:ext"      ExtendIntermediateSigmas   3 steps between 0.65 and 0.28, sine
    "bn:split"    SplitSigmasDenoise         0.25 — where stage 1 stops and stage 2 starts
    "bn:nonoise"  DisableNoise               stage 2 continues, it does not re-noise
    "bn:combat1"  LoraLoaderModelOnly        the Combat LoRA at 1.00 — stage 1's model
    "bn:combat2"  LoraLoaderModelOnly        the Combat LoRA at 0.65 — stage 2's, and the finish's
    "bn:guide1"   BasicGuider                stage 1's guider, shared by the three branches
    "bn:guide2"   BasicGuider                stage 2's, likewise
    "bn:s1a/2a/3a" SamplerCustomAdvanced     each branch's stage 1
    "bn:clean"    easy cleanGpuUsed          the sampler's VRAM freed before the final decode

THREE BRANCHES, ONE OF THEM EVER SUBMITTED
------------------------------------------
The file ships the same three (noise, sampler, sink) triples every other stack in this family does,
each one a full stage-1 → stage-2 relay off the one conditioning. H3 Express never hunts: it seeds
slot 1 and prunes the graph to the final sink, so the other two are deleted before anything is sent
and the clip still costs one seed and one submission. They are here so the file is the same shape as
h3-eros.json and h3-singularity.json — a stack that only one tab can drive is a stack that has to be
rebuilt before any other tab can.

DELIBERATE DIFFERENCES FROM THE AUTHORED GRAPH
----------------------------------------------
  * **The hybrid checkpoint, not the export's own selection.** See `REFERENCE_MODEL` below: the
    author's note node says the all-reference edition of this workflow defaults to the fl2va/ref2va
    hybrid and the FL2VA edition keeps whatever FL2VA model the loader was left on, which is what the
    export carries (with the loader still titled "Hybrid H3 UNet"). Every H3 Express clip conditions
    on cast panels, never on a first or last frame, so this ships the hybrid; the export's own pick is
    one move of the tab's Model dropdown away.
  * **The latent-upscale finish added**, as above. `CreateVideo` + `SaveVideo` are dropped with it:
    the tab's sink is `VHS_VideoCombine` (`34`), which is what it prunes to and names the file on.
  * **RIFE added.** The authored graph muxes the sampler's own 24 fps. H3 Express has an fps
    checkbox, and a checkbox that silently does nothing is worse than a net the author did not pick;
    the finish relinks `165` out of the chain when it is unticked.
  * **The two bypassed turbo LoRA loaders dropped.** `minimax_h3_fl2v_lightx2v_turbo_4step_v0.1_comfy`
    at 0.70 on stage 1 and 0.20 on stage 2, both authored bypassed — and that file is not on this
    server under any spelling, so shipping them switched off would be shipping a graph that cannot be
    switched on. The author's tuning guide treats stage 2's turbo weight as the dial for "image too
    soft"; the equivalent here is the tab's own LoRA dropdown, which reaches both stages.
  * **`MiniMaxH3MemoryEfficientSageAttentionPatch` dropped.** Authored bypassed, between the
    attention backend and the LoRAs. It is the one patch in the file aimed at the 12 GB card the
    graph is named for, so it is worth knowing it is missing rather than off; a stack switch should
    not quietly change the attention either way.
  * **The reference loaders dropped**, and the bypassed third `LoadImage`. The tab uploads a cast
    panel per `ref_image_N` slot instead; nothing here reads `ref_videos`/`ref_audios`.
  * **One `BasicGuider` per stage, not per branch.** The guiders depend only on the model wire and
    the conditioning, both of which every branch shares.
  * **The prompt is a placeholder.** The authored graph ships one ten-second fight prompt in the six
    section spec format; the tab overwrites `22:11` on every clip.
  * **The `MarkdownNote` dropped.** Browser-side only — its contents are quoted through this
    docstring and the C# instead.

Run:  python tools/build_h3_bunny.py [--object-info URL_OR_PATH] [--check]
"""
import argparse
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax", "bunny-authored.json")
DST = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-bunny.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

OUT_SUBFOLDER = "h3_bunny"

# The checkpoint this build ships, which is NOT the one the export has selected. The author's own note
# node is explicit that the all-reference edition of this workflow defaults to the fl2va/ref2va hybrid
# ("全能参考版本默认使用该 Hybrid 模型") and that the FL2VA edition simply keeps whatever FL2VA model was
# last picked in the loader -- which is what the export is carrying, with the loader still titled
# "Hybrid H3 UNet". H3 Express is always all-reference: every clip conditions on cast panels wired into
# MiniMaxH3ReferenceToVideo's ref_images slots, never on a first or last frame. So the hybrid is what
# this stack loads, and the export's own pick is one move of the tab's Model dropdown away.
REFERENCE_MODEL = "h3-minimax/minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors"

# The frame-count expression: seconds * 24, floored at 5, rounded up to the 17-frame VAE stride.
# Read off the authored graph rather than written here, so a re-export that changes it changes this.
FRAMES_EXPR_FALLBACK = "max(5, round(a * 24)) + (5 - (max(5, round(a * 24)) % 17)) % 17"

# The three hunt branches, exactly as H3ErosViewModel.SampleBranches names them:
#   slot -> (noise, stage-1 sampler, stage-2 sampler, video decode, audio decode, sink)
# The STAGE-2 sampler carries the id the tab knows as "the sampler": it is the latent the finish
# upscales, and the one the previews would decode.
BRANCHES = [
    ("125:17", "bn:s1a", "125:12", "176", "177", "18"),
    ("133:128", "bn:s2a", "133:129", "187", "188", "134"),
    ("143:138", "bn:s3a", "143:139", "180", "181", "144"),
]

# Fixed schedules for the upscale pass, by step count. Same values h3-eros.json ships; the tab links
# one of them into the second-pass sampler from its ⬆ steps dial.
SIGMA_SCHEDULES = {
    "222": ("3 step Sigmas", "0.9035, 0.6316, 0.3158, 0.0000"),
    "221": ("4 step Sigmas", "0.9035, 0.8000, 0.6316, 0.3158, 0.0000"),
    "220": ("5 step Sigmas", "0.9231, 0.8780, 0.8000, 0.6316, 0.3158, 0.0000"),
}

# Inputs a node carries as browser-side widgets and never declares in /object_info. rgthree's Power
# Lora Loader keeps its header and its "add" button there; h3-eros.json ships both and the server
# ignores them, so the seat is written the same way here rather than differently for no reason.
WIDGET_ONLY_INPUTS = {
    "Power Lora Loader (rgthree)": {"PowerLoraLoaderHeaderWidget", "âž• Add Lora"},
}

PROMPT_PLACEHOLDER = (
    "The story's own clip prompt is written into this node at submit time — H3 Express overwrites it "
    "on every clip. Whatever is left here is never rendered."
)

# ComfyUI serialises a bypassed node as mode 4 and a muted one as 2. Everything this build drops on
# purpose is one of those in the authored file, and the drop is only honest while it stays that way.
BYPASSED = (2, 4)


# ── reading the authored graph ──────────────────────────────────────────────────────────────────

def load_source():
    """Every widget value the BUNNY relay is, read out of the author's export."""
    with open(SRC, encoding="utf-8") as fh:
        ui = json.load(fh)

    nodes = list(ui["nodes"])
    live = [n for n in nodes if n.get("mode", 0) not in BYPASSED]

    by_type, live_by_type = {}, {}
    for n in nodes:
        by_type.setdefault(n["type"], []).append(n)
    for n in live:
        live_by_type.setdefault(n["type"], []).append(n)

    def nodes_of(node_type, only_live=True):
        found = (live_by_type if only_live else by_type).get(node_type)
        if not found:
            raise SystemExit(f"{os.path.basename(SRC)}: no {'live ' if only_live else ''}{node_type} "
                             f"node — the authored graph changed.")
        return found

    def widgets(node_type, index=0):
        return nodes_of(node_type)[index].get("widgets_values") or []

    # Two VAELoaders; read them by name rather than by order, so a re-export that swaps them does not
    # swap them here.
    vaes = [(n.get("widgets_values") or [""])[0] for n in nodes_of("VAELoader")]
    video_vae = next(v for v in vaes if "audio" not in v.lower())
    audio_vae = next(v for v in vaes if "audio" in v.lower())

    # The two Combat LoRA loaders, told apart by strength rather than by order: stage 1 samples the
    # choreography at the higher weight, stage 2 cleans the tail up at the lower one. The bypassed
    # turbo pair is excluded by `live`, and the author's own tuning guide is written in terms of
    # exactly these two numbers, so a re-export that moves them moves this file.
    combat = sorted(
        ((w[0].replace("\\", "/"), float(w[1]))
         for w in ((n.get("widgets_values") or ["", 0]) for n in nodes_of("LoraLoaderModelOnly"))),
        key=lambda w: -w[1],
    )
    if len(combat) != 2:
        raise SystemExit(f"{os.path.basename(SRC)}: expected two live LoraLoaderModelOnly nodes "
                         f"(the Combat LoRA at two strengths), found {len(combat)}.")
    if combat[0][0] != combat[1][0]:
        raise SystemExit(f"{os.path.basename(SRC)}: the two stages load different LoRAs "
                         f"({combat[0][0]} / {combat[1][0]}) — this is no longer one LoRA at two "
                         f"strengths, and the second stage would be a different model.")

    # The schedule. One BasicScheduler at denoise 1, extended and then split.
    sched = widgets("BasicScheduler")
    if float(sched[2]) != 1.0:
        raise SystemExit(f"{os.path.basename(SRC)}: the BasicScheduler is at denoise {sched[2]}, not 1 "
                         f"— the split below assumes it builds the whole schedule.")
    extend = widgets("ExtendIntermediateSigmas")
    split = widgets("SplitSigmasDenoise")
    if not 0.0 < float(split[0]) < 1.0:
        raise SystemExit(f"{os.path.basename(SRC)}: SplitSigmasDenoise is at {split[0]} — that is not a "
                         f"split, and one of the two stages would get the whole schedule.")

    # Two SamplerCustomAdvanced nodes and one KSamplerSelect shared by both: the split is a split of
    # the sigmas, not of the sampler.
    if len(nodes_of("SamplerCustomAdvanced")) != 2:
        raise SystemExit(f"{os.path.basename(SRC)}: expected two SamplerCustomAdvanced nodes — the "
                         f"authored graph is no longer a two-stage relay.")
    if not nodes_of("DisableNoise"):
        raise SystemExit(f"{os.path.basename(SRC)}: no DisableNoise — stage 2 would re-noise the latent.")

    frames_expr = FRAMES_EXPR_FALLBACK
    for n in nodes_of("ComfyMathExpression"):
        expr = (n.get("widgets_values") or [""])[0]
        if "17" in expr:
            frames_expr = expr
            break

    authored_unet = (widgets("UNETLoader")[0]).replace("\\", "/")
    if not authored_unet.startswith("h3-minimax/"):
        raise SystemExit(f"{os.path.basename(SRC)}: the UNETLoader names {authored_unet!r}, which is not in "
                         f"the h3-minimax folder this tab's Model dropdown lists.")

    return {
        "unet": REFERENCE_MODEL,
        "authored_unet": authored_unet,
        "weight_dtype": widgets("UNETLoader")[1],
        "clip": widgets("CLIPLoader")[0],
        "clip_type": widgets("CLIPLoader")[1],
        "clip_device": widgets("CLIPLoader")[2],
        "video_vae": video_vae,
        "audio_vae": audio_vae,
        "attention": widgets("ModelAttentionBackend")[0],
        "lora": combat[0][0],
        "lora_stage1": combat[0][1],
        "lora_stage2": combat[1][1],
        "sampler_name": widgets("KSamplerSelect")[0],
        "scheduler": sched[0],
        "steps": int(sched[1]),
        "denoise": float(sched[2]),
        "extend_steps": int(extend[0]),
        "extend_start": float(extend[1]),
        "extend_end": float(extend[2]),
        "extend_spacing": extend[3],
        "split_denoise": float(split[0]),
        "aspect": widgets("ResolutionSelector")[0],
        "megapixels": float(widgets("ResolutionSelector")[1]),
        "multiple": int(widgets("ResolutionSelector")[2]),
        "seconds": float(widgets("PrimitiveFloat")[0]),
        "ref_image_size": (widgets("MiniMaxH3ReferenceToVideo") or ["", 0, 0, 0, "match"])[4],
        "fps": int(widgets("CreateVideo")[0]),
        "frames_expr": frames_expr,
    }


# ── building the API graph ──────────────────────────────────────────────────────────────────────

def node(class_type, title, **inputs):
    return {"inputs": inputs, "class_type": class_type, "_meta": {"title": title}}


def sink(prefix, fps, crf, title, images, audio):
    return node(
        "VHS_VideoCombine", title,
        frame_rate=fps, loop_count=0, filename_prefix=prefix, format="video/h264-mp4",
        pix_fmt="yuv420p", crf=crf, save_metadata=False, trim_to_audio=False,
        pingpong=False, save_output=True, images=images, audio=audio,
    )


def build(src):
    g = {}
    fps = src["fps"]

    # ── loaders ─────────────────────────────────────────────────────────────
    g["171:4"] = node("UNETLoader", "UNETLoader",
                      unet_name=src["unet"], weight_dtype=src["weight_dtype"])
    g["171:3"] = node("CLIPLoader", "CLIPLoader",
                      clip_name=src["clip"], type=src["clip_type"], device=src["clip_device"])
    g["171:2"] = node("VAELoader", "VAELoader", vae_name=src["video_vae"])
    g["171:1"] = node("VAELoader", "VAELoader", vae_name=src["audio_vae"])

    # ── the MODEL wire ──────────────────────────────────────────────────────
    # The author's attention backend, then the empty Power Lora Loader seat, then the Combat LoRA at
    # its two strengths — one wire per stage. The seat is ABOVE the split on purpose: the tab's
    # optional LoRA is spliced into it, and a second stage cleaning up a latent the first stage
    # sampled through a LoRA it does not have would undo it.
    g["196"] = node("ModelAttentionBackend", "ModelAttentionBackend",
                    model=["171:4", 0], attention=src["attention"])
    g["21"] = node("Power Lora Loader (rgthree)", "Power Lora Loader (rgthree)",
                   model=["196", 0],
                   **{"PowerLoraLoaderHeaderWidget": {"type": "PowerLoraLoaderHeaderWidget"},
                      "âž• Add Lora": ""})
    g["bn:combat1"] = node("LoraLoaderModelOnly", "Combat LoRA — stage 1 (action structure)",
                           model=["21", 0], lora_name=src["lora"],
                           strength_model=src["lora_stage1"])
    g["bn:combat2"] = node("LoraLoaderModelOnly", "Combat LoRA — stage 2 (cleanup)",
                           model=["21", 0], lora_name=src["lora"],
                           strength_model=src["lora_stage2"])
    stage1_model = ["bn:combat1", 0]
    stage2_model = ["bn:combat2", 0]

    # ── prompt, length, canvas, steps ───────────────────────────────────────
    g["22:11"] = node("PrimitiveStringMultiline", "Prompt", value=PROMPT_PLACEHOLDER)
    g["22:23"] = node("PrimitiveFloat", "Video Length (seconds)", value=src["seconds"])
    g["22:24"] = node("ComfyMathExpression", "ComfyMathExpression",
                      expression=src["frames_expr"], **{"values.a": ["22:23", 0]})
    g["22:9"] = node("ResolutionSelector", "Resolution Selector (Size)",
                     aspect_ratio=src["aspect"], megapixels=src["megapixels"],
                     multiple=src["multiple"])
    g["22:8"] = node("INTConstant", "TOTAL STEPS", value=src["steps"])

    # ── the schedule, built once and split ──────────────────────────────────
    # Sampled off stage 1's model wire, which is where the authored graph reads it.
    g["22:7"] = node("BasicScheduler", "BasicScheduler",
                     model=stage1_model, scheduler=src["scheduler"], steps=["22:8", 0],
                     denoise=src["denoise"])
    g["22:6"] = node("KSamplerSelect", "KSamplerSelect", sampler_name=src["sampler_name"])
    g["bn:ext"] = node("ExtendIntermediateSigmas", "BUNNY sigma extension",
                       sigmas=["22:7", 0], steps=src["extend_steps"],
                       start_at_sigma=src["extend_start"], end_at_sigma=src["extend_end"],
                       spacing=src["extend_spacing"])
    g["bn:split"] = node("SplitSigmasDenoise", f"BUNNY split — the last {src['split_denoise']:.0%}",
                         sigmas=["bn:ext", 0], denoise=src["split_denoise"])
    g["bn:nonoise"] = node("DisableNoise", "BUNNY stage 2: do not re-noise")

    # ── the conditioning ────────────────────────────────────────────────────
    # No reference loaders are shipped: the tab uploads one panel per cast member per clip and injects
    # a LoadImage per `ref_images.ref_image_N` slot beside this node at submit time.
    g["5"] = node("MiniMaxH3ReferenceToVideo", "MiniMaxH3ReferenceToVideo",
                  clip=["171:3", 0], vae=["171:2", 0], audio_vae=["171:1", 0],
                  prompt=["22:11", 0], width=["22:9", 0], height=["22:9", 1],
                  length=["22:24", 1], ref_image_size=src["ref_image_size"])

    # Both stages' guiders. One each rather than one per branch: they read only the model wire and the
    # conditioning, and every branch shares both.
    g["bn:guide1"] = node("BasicGuider", "BUNNY stage 1 guider",
                          model=stage1_model, conditioning=["5", 0])
    g["bn:guide2"] = node("BasicGuider", "BUNNY stage 2 guider (low strength)",
                          model=stage2_model, conditioning=["5", 0])

    # ── the branches: one seed each, relayed across the split ───────────────
    for slot, (noise, first, second, vdec, adec, out) in enumerate(BRANCHES, start=1):
        g[noise] = node("RandomNoise", "RandomNoise", noise_seed=slot - 1)
        g[first] = node("SamplerCustomAdvanced", f"Stage 1 #{slot} — action structure",
                        noise=[noise, 0], guider=["bn:guide1", 0], sampler=["22:6", 0],
                        sigmas=["bn:split", 0], latent_image=["5", 1])
        g[second] = node("SamplerCustomAdvanced", f"Stage 2 #{slot} — cleanup",
                         noise=["bn:nonoise", 0], guider=["bn:guide2", 0], sampler=["22:6", 0],
                         sigmas=["bn:split", 1], latent_image=[first, 0])
        g[vdec] = node("VAEDecode", "VAEDecode", samples=[second, 0], vae=["171:2", 0])
        g[adec] = node("VAEDecodeAudio", "VAEDecodeAudio", samples=[second, 0], vae=["171:1", 0])
        g[out] = sink(f"{OUT_SUBFOLDER}/preview_{slot}", fps, 19, f"Preview {slot}", [vdec, 0], [adec, 0])

    take = BRANCHES[0][2]

    # ── the finish: the relayed latent, upscaled and re-sampled ─────────────
    # Slot 1 is denoised_output — the previews decode slot 0, and with a schedule ending at 0.0 the two
    # are the same tensor. The tab repoints `242`/`259`/`258` at whichever branch it kept.
    g["242"] = node("LTXVSeparateAVLatent", "LTXVSeparateAVLatent", av_latent=[take, 1])
    g["243"] = node("MinimaxH3LatentUpscaler3D", "MinimaxH3LatentUpscaler3D",
                    latent=["242", 0], model_name="minimax_h3_latent_upscaler_3d_bf16.safetensors",
                    mode="megapixels", align=32,
                    # Both added by a Comfyui_Minimax_h3_latent_Upscaler update; the pack declares them
                    # required, so a graph without them is dropped during validation.
                    enable_temporal_chunking=True, force_unload=True,
                    device="cuda", precision="fp16", **{"mode.megapixels": 1.0})
    g["244"] = node("LTXVConcatAVLatent", "LTXVConcatAVLatent",
                    video_latent=["243", 0], audio_latent=["242", 1])

    for nid, (title, sigmas) in SIGMA_SCHEDULES.items():
        g[nid] = node("ManualSigmas", title, sigmas=sigmas)

    # The upscale pass samples through stage 2's wire — the lower Combat weight. It is a cleanup pass
    # over a latent that already has its choreography in it, which is the job stage 2's weight was
    # chosen for; the full-strength wire would be asking for motion the frames no longer have room for.
    g["135:30"] = node("KSamplerSelect", "KSamplerSelect", sampler_name=src["sampler_name"])
    g["135:27"] = node("RandomNoise", "RandomNoise", noise_seed=0)
    g["135:29"] = node("BasicGuider", "Upscale Guider", model=stage2_model, conditioning=["5", 0])
    g["135:26"] = node("SamplerCustomAdvanced", "Upscale Pass",
                       noise=["135:27", 0], guider=["135:29", 0], sampler=["135:30", 0],
                       sigmas=["221", 0], latent_image=["244", 0])

    # This graph is the author's 12 GB build; freeing the sampler's weights before a whole clip is
    # decoded is in its spirit. It sits on the LATENT wire, which is the one place the tab's own
    # relinking (RIFE in or out) cannot delete a pass-through.
    g["bn:clean"] = node("easy cleanGpuUsed", "Free VRAM before the decode", anything=["135:26", 0])

    g["189"] = node("VAEDecode", "Final Decode", samples=["bn:clean", 0], vae=["171:2", 0])
    g["190"] = node("VAEDecodeAudio", "Final Audio Decode", samples=["bn:clean", 0], vae=["171:1", 0])

    # The "skip the upscale" decode of the relayed latent, for a finish that wants the take at the size
    # it was sampled. Nothing consumes it, so the prune deletes it unless the tab wires it to the sink.
    g["259"] = node("VAEDecode", "Single-pass Decode", samples=[take, 1], vae=["171:2", 0])
    g["258"] = node("VAEDecodeAudio", "Single-pass Audio Decode", samples=[take, 1], vae=["171:1", 0])

    g["165"] = node("RIFEInterpolation", "RIFEInterpolation",
                    images=["189", 0], source_fps=float(fps), target_fps=float(fps * 2), scale=1,
                    model_name="flownet.pkl", batch_size=8, use_fp16=True)
    g["34"] = sink(f"{OUT_SUBFOLDER}/final", fps * 2, 16, "Final Video", ["165", 0], ["190", 0])
    return g


# ── validation ──────────────────────────────────────────────────────────────────────────────────

def fetch_object_info(where):
    if where.startswith("http"):
        with urllib.request.urlopen(where, timeout=120) as fh:
            return json.load(fh)
    with open(where, encoding="utf-8") as fh:
        return json.load(fh)


def conditional_inputs(*sections):
    """Inputs a node only declares once a format is chosen — VHS_VideoCombine's pix_fmt / crf and
    friends live inside its `format` spec's `formats` map, not in required/optional."""
    names = set()
    for section in sections:
        for spec_v in section.values():
            meta = spec_v[1] if len(spec_v) > 1 and isinstance(spec_v[1], dict) else {}
            for entries in (meta.get("formats") or {}).values():
                for entry in entries:
                    if isinstance(entry, list) and entry and isinstance(entry[0], str):
                        names.add(entry[0])
    return names


def check(graph, obj):
    """Every class installed, every link resolvable, every required input present."""
    problems = []
    for nid, n in graph.items():
        ct = n["class_type"]
        spec = obj.get(ct)
        if spec is None:
            problems.append(f"{nid}: class '{ct}' is not installed on the server")
            continue
        required = (spec.get("input") or {}).get("required") or {}
        optional = (spec.get("input") or {}).get("optional") or {}
        known = (set(required) | set(optional) | conditional_inputs(required, optional)
                 | WIDGET_ONLY_INPUTS.get(ct, set()))
        for key, value in n["inputs"].items():
            base = key.split(".")[0]
            if base not in known and key not in known:
                problems.append(f"{nid} ({ct}): unknown input '{key}'")
            if isinstance(value, list) and len(value) == 2 and isinstance(value[1], int):
                if value[0] not in graph:
                    problems.append(f"{nid} ({ct}): input '{key}' links to missing node '{value[0]}'")
        for key in required:
            if key in n["inputs"] or any(k.split(".")[0] == key for k in n["inputs"]):
                continue
            problems.append(f"{nid} ({ct}): required input '{key}' is missing")
        for key, value in n["inputs"].items():
            if not isinstance(value, str):
                continue
            spec_v = required.get(key) or optional.get(key)
            if not spec_v:
                continue
            options = None
            if isinstance(spec_v[0], list):
                options = spec_v[0]
            elif len(spec_v) > 1 and isinstance(spec_v[1], dict) and "options" in spec_v[1]:
                options = spec_v[1]["options"]
            if options and all(isinstance(o, str) for o in options) and value not in options:
                problems.append(f"{nid} ({ct}): '{key}' = '{value}' is not one of the server's options")
    return problems


def check_contract(graph):
    """Every id H3ErosViewModel drives has to be here, carrying the class it expects. This is the whole
    reason 🐰 is a change of file name and nothing else."""
    expected = {
        "22:11": "PrimitiveStringMultiline", "22:23": "PrimitiveFloat", "22:8": "INTConstant",
        "22:9": "ResolutionSelector", "5": "MiniMaxH3ReferenceToVideo",
        "22:6": "KSamplerSelect", "22:7": "BasicScheduler",
        "242": "LTXVSeparateAVLatent", "243": "MinimaxH3LatentUpscaler3D",
        "135:26": "SamplerCustomAdvanced", "135:27": "RandomNoise", "135:30": "KSamplerSelect",
        "259": "VAEDecode", "258": "VAEDecodeAudio", "189": "VAEDecode", "190": "VAEDecodeAudio",
        "165": "RIFEInterpolation", "34": "VHS_VideoCombine", "171:4": "UNETLoader",
        "21": "Power Lora Loader (rgthree)",
        "222": "ManualSigmas", "221": "ManualSigmas", "220": "ManualSigmas",
    }
    for noise, _first, second, _v, _a, out in BRANCHES:
        expected[noise] = "RandomNoise"
        expected[second] = "SamplerCustomAdvanced"
        expected[out] = "VHS_VideoCombine"

    problems = []
    for nid, ct in expected.items():
        if nid not in graph:
            problems.append(f"h3-eros.json contract: node '{nid}' ({ct}) is missing")
        elif graph[nid]["class_type"] != ct:
            problems.append(f"h3-eros.json contract: node '{nid}' is {graph[nid]['class_type']}, expected {ct}")

    # The one thing the shared BuildFinish assumes about this stack specifically: the id it upscales
    # off is the END of the relay, not the middle of it. A build that wired them the other way round
    # would upscale a latent that is still 25% un-denoised and look like a broken checkpoint.
    take = BRANCHES[0][2]
    if graph["242"]["inputs"]["av_latent"][0] != take:
        problems.append(f"242 reads {graph['242']['inputs']['av_latent'][0]}, not the stage-2 sampler {take}")
    for noise, first, second, _v, _a, _o in BRANCHES:
        if graph[first]["inputs"]["noise"][0] != noise:
            problems.append(f"{first}: stage 1 does not read the branch's own RandomNoise")
        if graph[second]["inputs"]["noise"][0] != "bn:nonoise":
            problems.append(f"{second}: stage 2 re-noises — it must read DisableNoise")
        if graph[second]["inputs"]["latent_image"][0] != first:
            problems.append(f"{second}: stage 2 does not continue stage 1's latent")
        if graph[first]["inputs"]["sigmas"][1] != 0 or graph[second]["inputs"]["sigmas"][1] != 1:
            problems.append(f"{first}/{second}: the sigma halves are the wrong way round")
    return problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--object-info", default=OBJ_URL,
                    help="URL or path of a ComfyUI /object_info dump for validation")
    ap.add_argument("--check", action="store_true", help="validate only, do not write")
    args = ap.parse_args()

    src = load_source()
    graph = build(src)

    problems = check_contract(graph)
    try:
        obj = fetch_object_info(args.object_info)
    except Exception as exc:                                     # noqa: BLE001 — advisory only
        print(f"! could not read object_info ({exc}); skipping server validation")
        obj = None
    if obj:
        problems += check(graph, obj)

    for p in problems:
        print(f"! {p}")
    if problems:
        return 1
    print(f"validated {os.path.basename(DST)}: {len(graph)} nodes")

    if args.check:
        return 0

    with open(DST, "w", encoding="utf-8") as fh:
        json.dump(graph, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    print(f"wrote {DST} ({len(graph)} nodes)")
    print(f"  model    {src['unet']}")
    print(f"           (the export itself selects {src['authored_unet']} — see REFERENCE_MODEL)")
    print(f"  clip     {src['clip']}")
    print(f"  sampling {src['sampler_name']} / {src['scheduler']} / {src['steps']} steps, "
          f"+{src['extend_steps']} between {src['extend_start']} and {src['extend_end']} "
          f"({src['extend_spacing']}), split at {src['split_denoise']:.0%}")
    print(f"  lora     {src['lora']} at {src['lora_stage1']} / {src['lora_stage2']}")
    print(f"  patches  {src['attention']}")
    print(f"  authored canvas {src['aspect']} at {src['megapixels']} MP, {src['seconds']}s, "
          f"{src['fps']} fps (the tab writes its own)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
