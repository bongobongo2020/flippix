#!/usr/bin/env python3
"""
One-off converter: the authored "Qwen Image 2.1 + Prompt Enhancer" graph -> flat API-format JSON.

Source: workflow/image/qwen/qwen21WithPromptEnchancer_v12 (1).json  (ComfyUI UI graph, v12)
Output: workflow/image/qwen/qwen21-prompt-enhancer.json             (what FlipPix submits)

What the graph does, and what the Model Workflow entry drives:

    a user prompt is optionally rewritten by a local Qwen3-VL 8B text model (CLIPLoader ->
    TextGenerate, steered by a long "Image Prompt Rewriting Expert" system prompt) and the
    ORIGINAL / ENHANCED switch picks which of the two strings reaches TextEncodeQwenImage21.
    From there it is a plain Qwen Image 2.1 text-to-image pass: UNETLoader ->
    ModelAttentionBackend -> EasyCache -> KSampler -> VAEDecode -> save.

ComfySwitchNode's two branches are lazy, so with the switch off the 8B enhancer never loads —
the toggle costs nothing when unused.

Changed on the way across, and why:

- The whole "Text to Image (Qwen Image 2.1)" subgraph is flattened. Node ids follow ComfyUI's own
  subgraph convention, "<instance>:<inner>", so the ids in the file match what the server's
  history shows. The instance's promoted widget values win over the inner defaults, which matters
  for clip_name: the subgraph defaults to qwen3vl_8b_bf16.safetensors, the instance overrides it
  with qwen3vl_8b_fp8_scaled.safetensors, and only the fp8 build is on the server.
- GoogleTranslateTextNode 468 becomes a PrimitiveStringMultiline. It was only there to translate
  the author's Portuguese into English; FlipPix writes English prompts and the node makes a live
  call out to Google on every render. Its two consumers (the concatenate's string_b and the
  switch's on_false) are unchanged, so this stays the single prompt injection point.
- ResolutionSelector 13 is dropped and its width/height baked into EmptyLatentImage, which is
  where every other FlipPix image workflow carries the canvas and where the tab patches it.
- SaveImageAdvanced 461 becomes a plain SaveImage. SaveImageAdvanced's format is a
  COMFY_DYNAMICCOMBO_V3, and the rest of the app's image path is built around SaveImage's
  ui.images / prefix_NNNNN_.png naming.
- StringConcatenate's delimiter becomes "\n\n". The system prompt ends on "User input:" with no
  trailing break, so the authored empty delimiter glued the user's text onto the colon.
- The two note nodes (463 MarkdownNote, 464 Note) are dropped; they are UI-only.

The KSampler keeps the authored schedule (45 steps, cfg 3.5, res_2m / beta57) as the file's
defaults; the tab overwrites steps/cfg/denoise/seed from its sliders.

Run:  python tools/convert_qwen21_enhancer.py [--object-info URL_OR_PATH]
"""
import argparse
import json
import os
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "image", "qwen", "qwen21WithPromptEnchancer_v12 (1).json")
DST = os.path.join(ROOT, "workflow", "image", "qwen", "qwen21-prompt-enhancer.json")

# The subgraph instance on the top-level graph, and the order of its promoted inputs. The
# instance's widgets_values line up with the subgraph's `inputs` list, skipping any entry that
# carries no link (here: "seed", which stays on the inner KSampler widget).
INSTANCE = "459"


def load_source():
    with open(SRC, encoding="utf-8") as f:
        return json.load(f)


def promoted_values(graph):
    """instance input name -> value, from the subgraph instance's widgets_values."""
    inst = next(n for n in graph["nodes"] if str(n["id"]) == INSTANCE)
    sub = graph["definitions"]["subgraphs"][0]
    names = [i["name"] for i in sub["inputs"] if i.get("linkIds")]
    values = inst.get("widgets_values") or []
    if len(names) != len(values):
        raise SystemExit(f"promoted input/value mismatch: {names} vs {values}")
    return dict(zip(names, values))


def build(graph):
    p = promoted_values(graph)
    sub = graph["definitions"]["subgraphs"][0]
    inner = {str(n["id"]): n for n in sub["nodes"]}

    def sid(node_id):
        return f"{INSTANCE}:{node_id}"

    def node(class_type, title, inputs):
        return {"inputs": inputs, "class_type": class_type, "_meta": {"title": title}}

    system_prompt = next(n for n in graph["nodes"] if str(n["id"]) == "470")["widgets_values"][0]
    # TextGenerate's widgets_values, in signature order:
    #   prompt, max_length, sampling_mode, temperature, top_k, top_p, min_p,
    #   repetition_penalty, seed, presence_penalty, thinking, use_default_template, mtp
    tg = next(n for n in graph["nodes"] if str(n["id"]) == "472")["widgets_values"]
    easycache = inner["484"]["widgets_values"]
    ksampler = inner["458"]["widgets_values"]  # seed, control, steps, cfg, sampler, sched, denoise

    wf = {}

    # ---- prompt side -------------------------------------------------------------------
    wf["468"] = node("PrimitiveStringMultiline", "USER PROMPT", {"value": ""})
    wf["470"] = node("PrimitiveStringMultiline", "SYSTEM PROMPT - Qwen Image 2.1",
                     {"value": system_prompt})
    wf["471"] = node("StringConcatenate", "System Prompt + User Prompt", {
        "string_a": ["470", 0],
        "string_b": ["468", 0],
        "delimiter": "\n\n",
    })
    wf["469"] = node("CLIPLoader", "Qwen 8B - Prompt Enhancer", {
        "clip_name": "qwen3vl_8b_fp8_scaled.safetensors",
        "type": "boogu",
        "device": "default",
    })
    wf["472"] = node("TextGenerate", "Qwen 8B - Enhance Prompt", {
        "clip": ["469", 0],
        "prompt": ["471", 0],
        "max_length": tg[1],
        "sampling_mode": tg[2],
        "sampling_mode.temperature": tg[3],
        "sampling_mode.top_k": tg[4],
        "sampling_mode.top_p": tg[5],
        "sampling_mode.min_p": tg[6],
        "sampling_mode.repetition_penalty": tg[7],
        "sampling_mode.seed": tg[8],
        "sampling_mode.presence_penalty": tg[9],
        "thinking": tg[10],
        "use_default_template": tg[11],
        "mtp": tg[12],
    })
    wf["473"] = node("PrimitiveBoolean", "USE PROMPT ENHANCER?", {"value": True})
    wf["474"] = node("ComfySwitchNode", "ORIGINAL / ENHANCED", {
        "switch": ["473", 0],
        "on_false": ["468", 0],
        "on_true": ["472", 0],
    })
    wf["475"] = node("PreviewAny", "FINAL PROMPT SENT TO QWEN 2.1", {"source": ["474", 0]})

    # ---- Text to Image (Qwen Image 2.1), flattened ------------------------------------
    wf[sid("451")] = node("UNETLoader", "Load Diffusion Model", {
        "unet_name": p["unet_name"],
        "weight_dtype": inner["451"]["widgets_values"][1],
    })
    wf[sid("485")] = node("ModelAttentionBackend", "Model Attention Backend", {
        "model": [sid("451"), 0],
        "attention": inner["485"]["widgets_values"][0],
    })
    wf[sid("484")] = node("EasyCache", "EasyCache", {
        "model": [sid("485"), 0],
        "reuse_threshold": easycache[0],
        "start_percent": easycache[1],
        "end_percent": easycache[2],
        "verbose": easycache[3],
    })
    wf[sid("453")] = node("CLIPLoader", "Load CLIP", {
        "clip_name": p["clip_name"],
        "type": inner["453"]["widgets_values"][1],
        "device": inner["453"]["widgets_values"][2],
    })
    wf[sid("454")] = node("VAELoader", "Load VAE", {"vae_name": p["vae_name"]})
    wf[sid("452")] = node("TextEncodeQwenImage21", "TextEncodeQwenImage21", {
        "clip": [sid("453"), 0],
        "prompt": ["474", 0],
        "negative_prompt": p["negative_prompt"],
        "resolution": inner["452"]["widgets_values"][2],
    })
    wf[sid("456")] = node("EmptyLatentImage", "Empty Latent Image", {
        "width": p["width"],
        "height": p["height"],
        "batch_size": inner["456"]["widgets_values"][2],
    })
    wf[sid("458")] = node("KSampler", "KSampler", {
        "model": [sid("484"), 0],
        "positive": [sid("452"), 0],
        "negative": [sid("452"), 1],
        "latent_image": [sid("456"), 0],
        "seed": ksampler[0],
        "steps": p["steps"],
        "cfg": round(float(p["cfg"]), 4),   # the author's 3.500000000000002
        "sampler_name": p["scheduler"],      # the subgraph's "scheduler" slot is labelled "sampler"
        "scheduler": p["scheduler_1"],       # ... and "scheduler_1" is the real scheduler
        "denoise": ksampler[6],
    })
    wf[sid("457")] = node("VAEDecode", "VAE Decode", {
        "samples": [sid("458"), 0],
        "vae": [sid("454"), 0],
    })

    wf["461"] = node("SaveImage", "Save Image", {
        "filename_prefix": "Qwen21",
        "images": [sid("457"), 0],
    })
    return wf


def validate(wf, object_info):
    """Every class_type installed, every combo widget a value the server offers."""
    problems = []
    for nid, n in wf.items():
        ct = n["class_type"]
        spec = object_info.get(ct)
        if spec is None:
            problems.append(f"{nid}: class_type {ct} is not installed on the server")
            continue
        fields = {}
        for grp in ("required", "optional"):
            fields.update(spec["input"].get(grp, {}))
        for key, val in n["inputs"].items():
            base = key.split(".", 1)[0]
            if base not in fields:
                problems.append(f"{nid} ({ct}): unknown input '{key}'")
                continue
            if isinstance(val, list):
                continue  # a link
            decl = fields[base][0]
            opts = decl if isinstance(decl, list) else fields[base][1].get("options") \
                if len(fields[base]) > 1 and decl == "COMBO" else None
            if opts and "." not in key and val not in opts:
                problems.append(f"{nid} ({ct}): {key}={val!r} is not one of {opts[:8]}...")
        for key, decl in fields.items():
            required = key in spec["input"].get("required", {})
            if required and key not in n["inputs"] and not decl[0].startswith("COMFY_"):
                problems.append(f"{nid} ({ct}): required input '{key}' is missing")
    for nid, n in wf.items():
        for key, val in n["inputs"].items():
            if isinstance(val, list) and val[0] not in wf:
                problems.append(f"{nid}: input '{key}' links to missing node {val[0]}")
    return problems


def load_object_info(where):
    if where.startswith("http"):
        with urllib.request.urlopen(where, timeout=30) as r:
            return json.load(r)
    with open(where, encoding="utf-8") as f:
        return json.load(f)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--object-info", default="http://10.0.0.10:8188/object_info",
                    help="ComfyUI /object_info URL or a saved copy; '' to skip validation")
    args = ap.parse_args()

    wf = build(load_source())

    if args.object_info:
        try:
            oi = load_object_info(args.object_info)
        except Exception as e:
            print(f"! could not reach {args.object_info} ({e}) - skipping validation")
        else:
            problems = validate(wf, oi)
            for p in problems:
                print("!", p)
            if problems:
                raise SystemExit(f"{len(problems)} problem(s); not writing {DST}")
            print("validated against the server: all node types and combo values resolve")

    with open(DST, "w", encoding="utf-8") as f:
        json.dump(wf, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"wrote {DST} ({len(wf)} nodes)")


if __name__ == "__main__":
    main()
