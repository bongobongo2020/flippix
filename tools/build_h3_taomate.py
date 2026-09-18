#!/usr/bin/env python3
r"""
Builds the "TaoMate" graph from the author's TaoMate render.

Source: workflow/video/h3-minimax/taomate-authored.json  (ComfyUI UI graph, 44 nodes + 1 subgraph)
Output: workflow/video/h3-minimax/h3-taomate.json        (what the H3 Express tab submits with TaoMate picked)

WHAT THE AUTHORED GRAPH IS
--------------------------
A single-take reference-to-video render whose novelty is a **two-model relay**: one ten-step schedule
split across two `ClownsharKSampler_Beta` nodes.

    pass 1   the base checkpoint, comfy-kitchen attention + sigma shift 12/6,
             steps 10, steps_to_run 6, sampler_mode "standard"
    pass 2   the SAME checkpoint loaded fresh with H3/taomate_h3_3step_comfy at 0.65,
             steps 10, steps_to_run -1, sampler_mode "resample"

RES4LYF's `steps_to_run` stops a sampler early and hands the schedule's position on in the latent's
state, so pass 2 does not re-noise: it picks the same schedule up where pass 1 left it and runs it out
on the distilled TaoMate weights. Both passes sample eta 0.5, linear/euler, beta57, cfg 1, bongmath on.

Then, instead of a latent upscale, the decoded frames go through **RTXVideoSuperResolution** -- so the
sampled canvas is the real canvas and the finish is a frame-space 2x.

Unlike the Singularity export (see tools/build_h3_singularity.py), this graph was already cast-shaped:
it drives `MiniMaxH3ReferenceToVideo` with six `LoadImage` "Picture N" nodes and an H3PromptIDE prompt
full of <Subject N> / <Picture N> tags -- which is exactly what the H3 Express clip writer produces. So
nothing about the conditioning had to be invented here: the six loaders (authored bypassed, naming the
author's own files) are dropped, and the tab injects one LoadImage per cast panel at submit time.

NODE IDS ARE h3-eros.json's WHERE THE TAB DRIVES THEM
-----------------------------------------------------
`H3ErosViewModel.ApplyCommonInputs` is shared by every stack, so every id it writes to carries the
class it expects: `22:11` prompt, `22:23` seconds, `22:24` the frame-count expression, `22:8` steps,
`22:9` the canvas, `5` the reference node, `171:4` the UNet, `21` the Power Lora Loader seat the tab's
optional LoRA is spliced into, `189`/`190` the final decodes, `165` RIFE, `34` the sink. The stack's own
nodes, which have no counterpart in h3-eros.json, get readable ids:

    "tm:attn"   ModelAttentionBackend    comfy kitchen attention
    "tm:shift"  MiniMaxH3SigmaShift      12 / 6, last on pass 1's MODEL wire
    "tm:tao"    LoraLoaderModelOnly      the TaoMate 3-step LoRA, pass 2's model
    "tm:neg"    CLIPTextEncode           the empty negative both passes share
    "tm:pass1"  ClownsharKSampler_Beta   the first steps, on the base weights
    "tm:pass2"  ClownsharKSampler_Beta   the rest of the schedule, resampled on TaoMate
    "tm:rtx"    RTXVideoSuperResolution  the frame-space finish

DELIBERATE DIFFERENCES FROM THE AUTHORED GRAPH
----------------------------------------------
  * **One UNETLoader, not two.** Pass 2's model came from a subgraph holding a second `UNETLoader` of
    the same file, and pass 1's from a `DiffusionModelLoaderKJ` of it at every default (which is a
    plain load). Both are `171:4` here, so the tab's model dropdown moves both legs of the relay
    together -- two legs on different checkpoints is not this stack. The two MODEL wires stay distinct:
    pass 1 keeps the attention backend and the sigma shift, pass 2 is the bare load plus the TaoMate
    LoRA, exactly as authored.
  * **The Power Lora Loader seat moved above the split**, so the tab's optional LoRA reaches both
    passes. The authored graph has two such loaders, both with every entry switched off, one of them on
    the pass-1 wire only; a pass-2 refine sampling without the LoRA the first pass had would undo it.
    H3SLAAttention's own tooltip puts the LoRAs before the attention patches, which is this order.
  * **RIFE added.** The authored graph muxes the sampler's own 24 fps. H3 Express has an fps checkbox,
    and a checkbox that silently does nothing is worse than a net the author did not pick; the finish
    relinks `165` out of the chain when it is unticked. It sits AFTER the RTX upscale, so the upscaler
    still sees the frame count the sampler produced.
  * **`H3SLAAttention` dropped.** Authored bypassed. It is worth 1.4-1.63x on other H3 stacks, but it
    was never measured on this relay, and a stack switch should not quietly change the attention too.
  * **`ModelPreviewOverrideKJ` dropped.** Its `preview_frames` is wired to the clip's whole frame
    count, so every sampler step decodes every frame for a live widget nobody is watching: this tab
    renders folders of films unattended.
  * **`H3PromptReferenceInputs` dropped and `H3PromptIDE` became `PrimitiveStringMultiline`.** The IDE
    node is a text passthrough with an authoring-only reference palette hanging off it; the palette is
    those six bypassed loaders, and the tab writes the prompt itself.
  * **The reference media dropped.** Six `LoadImage` + `ImageScaleToTotalPixels` pairs, a
    `VHS_LoadVideo` + `ImageScaleToMaxDimension` and a `LoadAudioUI`, all authored bypassed. The tab
    uploads a cast panel per `ref_image_N` slot instead; nothing here reads `ref_videos`/`ref_audios`.
  * **`Fast Groups Bypasser (rgthree)` and the three notes dropped.** Browser-side only.
  * **The prompt is a placeholder.** The authored graph ships one clip's six-section spec prompt; the
    tab overwrites `22:11` on every submission.

Run:  python tools/build_h3_taomate.py [--object-info URL_OR_PATH] [--check]
"""
import argparse
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax", "taomate-authored.json")
DST = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-taomate.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

OUT_SUBFOLDER = "h3_taomate"

# The frame-count expression: seconds * 24, floored at 5, rounded up to the 17-frame VAE stride. Read
# off the authored graph rather than written here, so a re-export that changes it changes this.
FRAMES_EXPR_FALLBACK = "max(5, round(a * 24)) + (5 - (max(5, round(a * 24)) % 17)) % 17"

# Where the relay's second leg gets its weights, if the authored graph ever stops saying.
TAOMATE_LORA = "H3/taomate_h3_3step_comfy.safetensors"

# Inputs a node carries as browser-side widgets and never declares in /object_info. rgthree's Power
# Lora Loader keeps its header and its "add" button there; h3-eros.json ships both and the server
# ignores them, so the seat is written the same way here rather than differently for no reason.
WIDGET_ONLY_INPUTS = {
    "Power Lora Loader (rgthree)": {"PowerLoraLoaderHeaderWidget", "âž• Add Lora"},
}

PROMPT_PLACEHOLDER = (
    "The story's own clip prompt is written into this node at submit time - H3 Express overwrites it "
    "on every clip. Whatever is left here is never rendered."
)

# ClownsharKSampler_Beta's widgets, in the order the UI serialises them. `seed` is followed by the
# frontend's control_after_generate widget, which is not a server input.
CLOWNSHARK_WIDGETS = [
    "eta", "sampler_name", "scheduler", "steps", "steps_to_run", "denoise", "cfg", "seed",
    "_control_after_generate", "sampler_mode", "bongmath",
]


# -- reading the authored graph -------------------------------------------------------------------

def load_source():
    """Every widget value the TaoMate relay is, read out of the author's export."""
    with open(SRC, encoding="utf-8") as fh:
        ui = json.load(fh)

    nodes = list(ui["nodes"])
    for sub in (ui.get("definitions") or {}).get("subgraphs") or []:
        nodes += list(sub.get("nodes") or [])

    by_type = {}
    for n in nodes:
        by_type.setdefault(n["type"], []).append(n)

    def nodes_of(node_type):
        found = by_type.get(node_type)
        if not found:
            raise SystemExit(f"{os.path.basename(SRC)}: no {node_type} node - the authored graph changed.")
        return found

    def widgets(node_type, index=0):
        return nodes_of(node_type)[index].get("widgets_values") or []

    # Two VAELoaders; read them by name rather than by order, so a re-export that swaps them does not
    # swap them here.
    vaes = [(n.get("widgets_values") or [""])[0] for n in nodes_of("VAELoader")]
    video_vae = next(v for v in vaes if "audio" not in v.lower())
    audio_vae = next(v for v in vaes if "audio" in v.lower())

    # The checkpoint. The author loads it twice - a DiffusionModelLoaderKJ at every default on pass 1,
    # a plain UNETLoader in a subgraph on pass 2 - and both must name the same file, or "one
    # UNETLoader, not two" would be a change of render rather than of node count.
    kj = widgets("DiffusionModelLoaderKJ")
    unet = widgets("UNETLoader")[0]
    if kj[0] != unet:
        raise SystemExit(f"{os.path.basename(SRC)}: the two passes load different checkpoints "
                         f"({kj[0]} / {unet}) - the relay is no longer one model.")
    for name, value, expected in (("weight_dtype", kj[1], "default"),
                                  ("compute_dtype", kj[2], "default"),
                                  ("patch_cublaslinear", kj[3], False),
                                  ("sage_attention", kj[4], "disabled"),
                                  ("enable_fp16_accumulation", kj[5], False)):
        if value != expected:
            raise SystemExit(f"{os.path.basename(SRC)}: DiffusionModelLoaderKJ.{name} is {value!r}, not "
                             f"{expected!r} - pass 1's load is no longer a plain UNETLoader.")

    # The two relay legs, told apart by sampler_mode rather than by order.
    passes = {}
    for n in nodes_of("ClownsharKSampler_Beta"):
        w = dict(zip(CLOWNSHARK_WIDGETS, n.get("widgets_values") or []))
        passes.setdefault(w.get("sampler_mode"), w)
    if set(passes) != {"standard", "resample"}:
        raise SystemExit(f"{os.path.basename(SRC)}: expected one standard and one resample "
                         f"ClownsharKSampler_Beta, found {sorted(passes)}.")
    first, second = passes["standard"], passes["resample"]
    if int(first["steps"]) != int(second["steps"]):
        raise SystemExit(f"{os.path.basename(SRC)}: the two passes disagree on the schedule length "
                         f"({first['steps']} / {second['steps']}) - a resample cannot continue it.")
    if not 0 < int(first["steps_to_run"]) < int(first["steps"]):
        raise SystemExit(f"{os.path.basename(SRC)}: pass 1 runs {first['steps_to_run']} of "
                         f"{first['steps']} steps - that is not a handoff.")

    # The TaoMate LoRA: the LoraLoaderModelOnly on pass 2's wire.
    tao = nodes_of("LoraLoaderModelOnly")[0]
    tao_w = tao.get("widgets_values") or [TAOMATE_LORA, 0.65]

    # Both Power Lora Loaders are pass-throughs in the authored file. If one ever ships a LoRA switched
    # on, collapsing the two seats into one would silently drop it.
    for n in nodes_of("Power Lora Loader (rgthree)"):
        for entry in n.get("widgets_values") or []:
            if isinstance(entry, dict) and entry.get("lora") and entry.get("on"):
                raise SystemExit(f"{os.path.basename(SRC)}: Power Lora Loader entry "
                                 f"{entry['lora']!r} is switched ON - the seat is no longer empty and "
                                 f"dropping the second loader would drop a LoRA.")

    frames_expr = FRAMES_EXPR_FALLBACK
    for n in nodes_of("ComfyMathExpression"):
        expr = (n.get("widgets_values") or [""])[0]
        if "17" in expr:
            frames_expr = expr
            break

    shift = widgets("MiniMaxH3SigmaShift")
    rtx = widgets("RTXVideoSuperResolution")
    resolution = widgets("ResolutionSelector")
    sink = widgets("VHS_VideoCombine")
    return {
        "unet": unet,
        "clip": widgets("CLIPLoader")[0],
        "clip_type": widgets("CLIPLoader")[1],
        "clip_device": widgets("CLIPLoader")[2],
        "video_vae": video_vae,
        "audio_vae": audio_vae,
        "attention": widgets("ModelAttentionBackend")[0],
        "shift_video": float(shift[0]),
        "shift_audio": float(shift[1]),
        "first": first,
        "second": second,
        "tao_lora": str(tao_w[0]).replace("\\", "/"),
        "tao_strength": float(tao_w[1]),
        "negative": widgets("CLIPTextEncode")[0],
        "rtx_scale": float(rtx[1]),
        "rtx_quality": rtx[2],
        "aspect": resolution[0],
        "megapixels": float(resolution[1]),
        "multiple": int(resolution[2]),
        "seconds": float(widgets("PrimitiveFloat")[0]),
        "frames_expr": frames_expr,
        "fps": int((sink or {}).get("frame_rate", 24)) if isinstance(sink, dict) else 24,
    }


# -- building the API graph ------------------------------------------------------------------------

def node(class_type, title, **inputs):
    return {"inputs": inputs, "class_type": class_type, "_meta": {"title": title}}


def clownshark(title, w, model, latent, mode, steps_to_run):
    """One leg of the relay. The schedule length is linked to `22:8` on both legs, because a resample
    that continues a different schedule from the one pass 1 stopped in the middle of is no
    continuation. The seed is written per clip by the tab."""
    return node(
        "ClownsharKSampler_Beta", title,
        model=model, positive=["5", 0], negative=["tm:neg", 0], latent_image=latent,
        eta=float(w["eta"]), sampler_name=w["sampler_name"], scheduler=w["scheduler"],
        steps=["22:8", 0], steps_to_run=steps_to_run, denoise=float(w["denoise"]),
        cfg=float(w["cfg"]), seed=0, sampler_mode=mode, bongmath=bool(w["bongmath"]),
    )


def build(src):
    g = {}

    # -- loaders ------------------------------------------------------------
    g["171:4"] = node("UNETLoader", "UNETLoader", unet_name=src["unet"], weight_dtype="default")
    g["171:3"] = node("CLIPLoader", "CLIPLoader",
                      clip_name=src["clip"], type=src["clip_type"], device=src["clip_device"])
    g["171:2"] = node("VAELoader", "VAELoader", vae_name=src["video_vae"])
    g["171:1"] = node("VAELoader", "VAELoader", vae_name=src["audio_vae"])

    # -- the MODEL wires ----------------------------------------------------
    # The seat first, so the tab's optional LoRA reaches both legs of the relay; then the two wires
    # part company, pass 1 through the author's attention backend and sigma shift, pass 2 through the
    # bare load plus the TaoMate LoRA.
    g["21"] = node("Power Lora Loader (rgthree)", "Power Lora Loader (rgthree)",
                   model=["171:4", 0],
                   **{"PowerLoraLoaderHeaderWidget": {"type": "PowerLoraLoaderHeaderWidget"},
                      "âž• Add Lora": ""})
    g["tm:attn"] = node("ModelAttentionBackend", "ModelAttentionBackend",
                        model=["21", 0], attention=src["attention"])
    g["tm:shift"] = node("MiniMaxH3SigmaShift", "MiniMaxH3SigmaShift",
                         model=["tm:attn", 0], shift_video=src["shift_video"],
                         shift_audio=src["shift_audio"])
    g["tm:tao"] = node("LoraLoaderModelOnly", "TaoMate 3-step",
                       model=["21", 0], lora_name=src["tao_lora"],
                       strength_model=src["tao_strength"])

    # -- prompt, length, canvas, steps --------------------------------------
    g["22:11"] = node("PrimitiveStringMultiline", "Prompt", value=PROMPT_PLACEHOLDER)
    g["22:23"] = node("PrimitiveFloat", "Video Length (seconds)", value=src["seconds"])
    g["22:24"] = node("ComfyMathExpression", "ComfyMathExpression",
                      expression=src["frames_expr"], **{"values.a": ["22:23", 0]})
    g["22:9"] = node("ResolutionSelector", "Resolution Selector (Size)",
                     aspect_ratio=src["aspect"], megapixels=src["megapixels"],
                     multiple=src["multiple"])
    g["22:8"] = node("INTConstant", "TOTAL STEPS", value=int(src["first"]["steps"]))

    # -- the conditioning ---------------------------------------------------
    # No reference loaders are shipped: the cast's panels are uploaded per clip and injected beside
    # this node, one LoadImage per ref_images.ref_image_N slot.
    g["5"] = node("MiniMaxH3ReferenceToVideo", "MiniMaxH3ReferenceToVideo",
                  clip=["171:3", 0], vae=["171:2", 0], audio_vae=["171:1", 0],
                  prompt=["22:11", 0], width=["22:9", 0], height=["22:9", 1],
                  length=["22:24", 1], ref_image_size="match")
    g["tm:neg"] = node("CLIPTextEncode", "Negative", clip=["171:3", 0], text=src["negative"])

    # -- the relay ----------------------------------------------------------
    g["tm:pass1"] = clownshark("Pass 1 - base weights", src["first"], ["tm:shift", 0], ["5", 1],
                               "standard", int(src["first"]["steps_to_run"]))
    g["tm:pass2"] = clownshark("Pass 2 - TaoMate resample", src["second"], ["tm:tao", 0],
                               ["tm:pass1", 0], "resample", int(src["second"]["steps_to_run"]))

    # -- the finish ---------------------------------------------------------
    g["189"] = node("VAEDecode", "Final Decode", samples=["tm:pass2", 0], vae=["171:2", 0])
    g["190"] = node("VAEDecodeAudio", "Final Audio Decode", samples=["tm:pass2", 0], vae=["171:1", 0])
    g["tm:rtx"] = node("RTXVideoSuperResolution", "RTX Video Super Resolution",
                       images=["189", 0], resize_type="scale by multiplier",
                       quality=src["rtx_quality"], **{"resize_type.scale": src["rtx_scale"]})
    g["165"] = node("RIFEInterpolation", "RIFEInterpolation",
                    images=["tm:rtx", 0], source_fps=float(src["fps"]),
                    target_fps=float(src["fps"] * 2), scale=1, model_name="flownet.pkl",
                    batch_size=8, use_fp16=True)
    g["34"] = node("VHS_VideoCombine", "Final Video",
                   frame_rate=src["fps"] * 2, loop_count=0,
                   filename_prefix=f"{OUT_SUBFOLDER}/final", format="video/h264-mp4",
                   pix_fmt="yuv420p", crf=16, save_metadata=False, trim_to_audio=False,
                   pingpong=False, save_output=True, images=["165", 0], audio=["190", 0])
    return g


# -- validation ------------------------------------------------------------------------------------

def fetch_object_info(where):
    if where.startswith("http"):
        with urllib.request.urlopen(where, timeout=180) as fh:
            return json.load(fh)
    with open(where, encoding="utf-8") as fh:
        return json.load(fh)


def conditional_inputs(*sections):
    """Inputs a node only declares once a choice is made - VHS_VideoCombine's pix_fmt / crf live inside
    its `format` spec, RTXVideoSuperResolution's scale / width / height inside `resize_type`."""
    names = set()
    for section in sections:
        for spec_v in section.values():
            meta = spec_v[1] if len(spec_v) > 1 and isinstance(spec_v[1], dict) else {}
            for entries in (meta.get("formats") or {}).values():
                for entry in entries:
                    if isinstance(entry, list) and entry and isinstance(entry[0], str):
                        names.add(entry[0])
            for option in meta.get("options") or []:
                if not isinstance(option, dict):
                    continue
                for group in (option.get("inputs") or {}).values():
                    names.update(group)
    return names


def combo_options(spec_v):
    if isinstance(spec_v[0], list):
        return spec_v[0]
    meta = spec_v[1] if len(spec_v) > 1 and isinstance(spec_v[1], dict) else {}
    options = meta.get("options")
    if not options:
        return None
    if all(isinstance(o, str) for o in options):
        return options
    # A dynamic combo's choices are the keys of its per-choice input groups.
    return [o["key"] for o in options if isinstance(o, dict) and "key" in o]


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
            if key not in known and key.split(".")[0] not in known and key.split(".")[-1] not in known:
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
            options = combo_options(spec_v)
            if options and all(isinstance(o, str) for o in options) and value not in options:
                problems.append(f"{nid} ({ct}): '{key}' = '{value}' is not one of the server's options")
    return problems


def check_contract(graph):
    """Every id the shared ApplyCommonInputs and the TaoMate finish drive has to be here, carrying the
    class they expect. That contract is what makes TaoMate a change of stack rather than of tab."""
    expected = {
        # written by the shared H3ErosViewModel.ApplyCommonInputs
        "22:11": "PrimitiveStringMultiline", "22:23": "PrimitiveFloat", "22:8": "INTConstant",
        "22:9": "ResolutionSelector", "5": "MiniMaxH3ReferenceToVideo", "171:4": "UNETLoader",
        "21": "Power Lora Loader (rgthree)",
        # written by H3ExpressViewModel's TaoMate finish
        "tm:pass1": "ClownsharKSampler_Beta", "tm:pass2": "ClownsharKSampler_Beta",
        "tm:rtx": "RTXVideoSuperResolution", "189": "VAEDecode", "190": "VAEDecodeAudio",
        "165": "RIFEInterpolation", "34": "VHS_VideoCombine",
    }
    problems = []
    for nid, ct in expected.items():
        if nid not in graph:
            problems.append(f"contract: node '{nid}' ({ct}) is missing")
        elif graph[nid]["class_type"] != ct:
            problems.append(f"contract: node '{nid}' is {graph[nid]['class_type']}, expected {ct}")

    # The relay only relays if both legs read the same schedule length.
    for nid in ("tm:pass1", "tm:pass2"):
        if graph.get(nid, {}).get("inputs", {}).get("steps") != ["22:8", 0]:
            problems.append(f"contract: {nid}'s steps is not linked to 22:8 - the tab's step count "
                            f"would reach one leg of the relay only")
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
    except Exception as exc:                                     # noqa: BLE001 - advisory only
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
    first = src["first"]
    print(f"  model    {src['unet']}  (both legs)")
    print(f"  clip     {src['clip']}")
    print(f"  relay    {first['sampler_name']} / {first['scheduler']}, eta {first['eta']}, "
          f"cfg {first['cfg']}, {first['steps']} steps: {first['steps_to_run']} on the base weights, "
          f"the rest resampled on {os.path.basename(src['tao_lora'])} at {src['tao_strength']}")
    print(f"  patches  {src['attention']}, sigma shift {src['shift_video']}/{src['shift_audio']} "
          f"(pass 1 only, as authored)")
    print(f"  finish   RTX {src['rtx_quality']} x{src['rtx_scale']}, RIFE "
          f"{src['fps']}->{src['fps'] * 2} fps (the tab relinks it out when unticked)")
    print(f"  authored canvas {src['aspect']} at {src['megapixels']} MP, {src['seconds']}s "
          f"(the tab writes its own)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
