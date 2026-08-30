#!/usr/bin/env python3
"""
One-off converter: the authored "3 Steps Wan Enhance" graph -> flat API-format JSON.

Source: workflow/video/wan/3StepsWanEnhanceCleanGithub.json  (ComfyUI UI graph: SetNode/GetNode
        broadcast pairs, rgthree switches, bypassed branches, a dozen preview/compare outputs)
Output: workflow/video/wan/targeted-wan-enhance.json          (what FlipPix submits)

What the graph does, and what the FlipPix tab drives:

    load clip -> resize to the phase-1 canvas -> SAM3 tracks the named targets across every frame
    -> that mask gates three WanVideo 2.2 T2V low-noise passes at falling denoise and rising
    resolution (0.4 @ 432x768 -> 0.2 @ 576x1024 -> 0.1 @ 720x1280), each pass composited back
    into the untouched source through the feathered mask -> remux the original audio.

Cleanups applied on the way through:
- Every preview/compare branch is dropped: the mask overlay renders (DrawMaskOnImage +
  SolidColorBatched and their VHS_VideoCombine sinks), the PreviewImage, the 4-up compare strip
  (ImageConcanate x3 -> VHS_VideoCombine) and the AILab_MaskPreview passthrough. Only the final
  VHS_VideoCombine survives, so a run encodes one video instead of six.
- The alternate mask sources (a pre-rendered alpha-mask video, the SAM3 negative-subtract branch)
  are authored bypassed; they stay dropped and the rgthree Any Switch collapses onto the live
  SAM3 branch, which is the only one the tab can feed.
- WanVideoTorchCompileSettings is dropped. The three phases sample at three different canvases,
  so inductor recompiles for every one of them and never amortises the cost.
- attention_mode sageattn -> comfy, and quantization -> fp8_e4m3fn_scaled_fast with merged
  LoRAs: 1.63x on the sampler, measured. See the speed-settings block below for the numbers and
  for how to put the slower, higher-precision settings back.
- base_precision fp16_fast -> bf16, which is what the scaled fp8 checkpoint on this box actually
  loads under; see apply_cleanups.
- Model/LoRA paths are rewritten from the author's Windows layout to the names the server
  publishes; the one LoRA it has no copy of (PAWG) is left as "none" for the user to fill from
  the tab's optional slot.
- LoadSAM3Model points at the absolute /mnt/storage1/ai-models/sam3/sam3.pt rather than a path
  relative to the ComfyUI root, which is not where this box keeps its models.

Run:  python tools/convert_targeted_wan_enhance.py [--object-info URL_OR_PATH]
"""
import argparse
import json
import os
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "wan", "3StepsWanEnhanceCleanGithub.json")
DST = os.path.join(ROOT, "workflow", "video", "wan", "targeted-wan-enhance.json")
OBJ_CACHE = os.path.join(ROOT, "tools", ".cache", "object_info_wanenhance.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

# Virtual/UI-only node types: no backend implementation at all.
UI_ONLY_TYPES = {
    "Note", "MarkdownNote", "SetNode", "GetNode", "Reroute",
    "Fast Groups Bypasser (rgthree)", "PreviewImage", "ShowText|pysssss",
}

# Preview, overlay and compare-only nodes. Dropping the sinks is what makes the reachability
# prune below strip the branches that feed them.
DROP_TYPES = {"DrawMaskOnImage", "SolidColorBatched", "AILab_MaskPreview",
              "WanVideoTorchCompileSettings"}

# Types that forward one input rather than vanishing when they are dropped.
PASSTHROUGH_TYPES = {"AILab_MaskPreview": 0}

# The one output that is kept: VHS_VideoCombine "Enhanced/Final".
OUTPUT_NODES = {276}

# Nodes authored bypassed that the tab needs live.
#   145 - WanVideoContextOptions for phase one. Phases two and three already window their
#         sampling; phase one did not, so any clip longer than one context window sampled the
#         whole latent in a single pass and died on VRAM. The tab accepts arbitrary clips, so
#         phase one windows the same way the other two do.
FORCE_ENABLE = {145}

# rgthree Any Switch returns its first non-null input, so a switch left with two live branches
# still resolves - but which one wins then depends on socket naming rather than on anything
# visible in the file. 218 picks the mask source; pin it to the SAM3 branch and drop the
# whole-frame SolidMask fallback the tab never selects.
# 308/309 pick what the next phase samples from - the composited frame, or the raw decode if the
# composite branch were bypassed - and the composite is always live here, so pin those too.
DROP_SWITCH_INPUTS = {218: {"any_03"}, 308: {"any_02"}, 309: {"any_02"}}

# Widget-backed inputs the frontend adds a second "control_after_generate" widget for.
SEED_NAMES = {"seed", "noise_seed", "rand_seed"}

PRIMITIVE = {"INT", "FLOAT", "STRING", "BOOLEAN", "COMBO"}
DROP_WIDGET_KEYS = {"videopreview", "choose video to upload"}

# -- speed settings, measured on this box --------------------------------------------------------
# tools/bench_wan_enhance_attention.py, 81 frames @ 768x432, 6 steps, RTX 4090:
#
#   sdpa   + fp8_e4m3fn_scaled        66.1s   1.00x   (what the authored file implies)
#   comfy  + fp8_e4m3fn_scaled        50.1s   1.32x
#   sdpa   + fp8_e4m3fn_scaled_fast   54.4s   1.22x
#   comfy  + fp8_e4m3fn_scaled_fast   40.4s   1.63x   <- what is shipped
#
# "comfy" routes the wrapper's attention to comfy.ldm.modules.attention.optimized_attention,
# which on this server is Comfy Kitchen int8 attention (ComfyUI is started --use-ck-attention).
# sageattn, which the file was authored with, is not installed here and falls back silently.
#
# "_fast" is the real fp8 matmul (torch._scaled_mm) rather than upcasting the fp8 weights to
# bf16 for every layer; it needs compute capability >= 8.9 (4090 = 8.9) and refuses unmerged
# LoRAs, hence MERGE_LORAS. Both are lower-precision math than the baseline: if a pass ever
# looks degraded, set these back to "sdpa" / "fp8_e4m3fn_scaled" and re-run the converter.
ATTENTION_MODE = "comfy"
QUANTIZATION = "fp8_e4m3fn_scaled_fast"
MERGE_LORAS = True

# -- server-specific rewrites ------------------------------------------------------------------
SAM3_CHECKPOINT = "/mnt/storage1/ai-models/sam3/sam3.pt"
WAN_T2V_LOW = "wan/Wan2_2-T2V-A14B-LOW_fp8_e4m3fn_scaled_KJ.safetensors"
LORA_REWRITES = {
    "WanModels\\wanrealism\\want2v4steps_low_noise_lora.safetensors":
        "WAN/wan2.2_t2v_A14b_low_noise_lora_rank64_lightx2v_4step_1217.safetensors",
    "WanModels\\wanrealism\\Wan14B_RealismBoost.safetensors":
        "WAN/Wan14B_RealismBoost.safetensors",
    "WanModels\\wanrealism\\Wan2.2-Fun-A14B-InP-low-noise-HPS2.1.safetensors":
        "WAN/Wan2.2-Fun-A14B-InP-LOW-HPS2.1_resized_dynamic_avg_rank_15_bf16.safetensors",
    # No copy of this one on the box; the tab exposes the slot instead.
    "WanModels\\WAN2.2-T2V-LowNoise_PAWG.safetensors": "none",
}


def load_object_info(src):
    if src and os.path.exists(src):
        with open(src, encoding="utf-8") as f:
            return json.load(f)
    if src is None and os.path.exists(OBJ_CACHE):
        with open(OBJ_CACHE, encoding="utf-8") as f:
            return json.load(f)
    url = src or OBJ_URL
    print("Fetching object_info from %s ..." % url)
    with urllib.request.urlopen(url, timeout=300) as r:
        data = json.loads(r.read().decode("utf-8"))
    os.makedirs(os.path.dirname(OBJ_CACHE), exist_ok=True)
    with open(OBJ_CACHE, "w", encoding="utf-8") as f:
        json.dump(data, f)
    return data


def spec_type(spec):
    t = spec[0] if isinstance(spec, list) and spec else spec
    return "COMBO" if isinstance(t, list) else t


def spec_opts(spec):
    return spec[1] if isinstance(spec, list) and len(spec) > 1 and isinstance(spec[1], dict) else {}


class Flattener:
    def __init__(self, ui, obj):
        self.ui = ui
        self.obj = obj
        self.nodes = {n["id"]: n for n in ui["nodes"]}
        self.links = {l[0]: l for l in ui["links"] if isinstance(l, list)}
        self.set_sources = {}   # SetNode variable -> (node_id, slot)
        self.warnings = []
        for n in ui["nodes"]:
            if n.get("type") != "SetNode":
                continue
            name = (n.get("widgets_values") or [""])[0]
            inp = (n.get("inputs") or [None])[0]
            link = inp.get("link") if inp else None
            if link is not None and link in self.links:
                l = self.links[link]
                self.set_sources[name] = (l[1], l[2])

    # -- node state -----------------------------------------------------------------------
    def mode(self, nid):
        return self.nodes[nid].get("mode", 0)

    def is_live(self, nid):
        n = self.nodes.get(nid)
        if n is None:
            return False
        t = n.get("type", "")
        if nid in FORCE_ENABLE:
            return True
        return (t not in UI_ONLY_TYPES and t not in DROP_TYPES
                and self.mode(nid) not in (2, 4))

    def output_type(self, nid, slot):
        outs = self.nodes[nid].get("outputs") or []
        return outs[slot].get("type") if slot < len(outs) else None

    def input_link(self, nid, index):
        inputs = self.nodes[nid].get("inputs") or []
        return inputs[index].get("link") if index < len(inputs) else None

    # -- producer resolution --------------------------------------------------------------
    def resolve(self, nid, slot, depth=0):
        """(node, slot) -> ["<api id>", slot] for the nearest live producer, or None."""
        if depth > 100:
            self.warnings.append("resolve depth exceeded at %s" % nid)
            return None
        n = self.nodes.get(nid)
        if n is None:
            return None
        t = n.get("type", "")

        if t in ("Reroute", "SetNode"):
            link = self.input_link(nid, 0)
            return self._follow(link, depth) if link is not None else None

        if t == "GetNode":
            name = (n.get("widgets_values") or [""])[0]
            src = self.set_sources.get(name)
            if src is None:
                self.warnings.append("GetNode '%s' has no SetNode source" % name)
                return None
            return self.resolve(src[0], src[1], depth + 1)

        if t in PASSTHROUGH_TYPES:
            link = self.input_link(nid, PASSTHROUGH_TYPES[t])
            return self._follow(link, depth) if link is not None else None

        if nid in FORCE_ENABLE:
            return [str(nid), slot]

        if self.mode(nid) == 2 or t in UI_ONLY_TYPES or t in DROP_TYPES:
            return None

        if self.mode(nid) == 4:
            # Bypass forwards the first input whose type matches the requested output.
            want = self.output_type(nid, slot)
            for inp in n.get("inputs") or []:
                if inp.get("link") is None:
                    continue
                itype = inp.get("type")
                if itype == want or (isinstance(itype, str) and want
                                     and want in str(itype).split(",")):
                    return self._follow(inp["link"], depth)
            return None

        return [str(nid), slot]

    def _follow(self, link, depth):
        l = self.links.get(link)
        return self.resolve(l[1], l[2], depth + 1) if l else None

    # -- widget mapping -------------------------------------------------------------------
    def widget_names(self, node):
        """Widget-backed input names in frontend order.

        The node's own `inputs` array is authoritative: object_info declaration order does not
        match it (WanVideoSampler alone reorders six widgets), and mapping against the wrong one
        silently writes `scheduler` into `force_offload`.
        """
        names = [inp["name"] for inp in (node.get("inputs") or []) if "widget" in inp]
        if names:
            return names
        info = self.obj.get(node.get("type", ""), {}).get("input", {})
        return [name for sect in ("required", "optional")
                for name, spec in info.get(sect, {}).items()
                if spec_type(spec) in PRIMITIVE]

    def widget_inputs(self, node, wv):
        out, i = {}, 0
        info = self.obj.get(node.get("type", ""), {}).get("input", {})
        specs = {n: s for sect in ("required", "optional")
                 for n, s in info.get(sect, {}).items()}
        for name in self.widget_names(node):
            if i >= len(wv):
                break
            out[name] = wv[i]
            i += 1
            opts = spec_opts(specs.get(name, []))
            if name in SEED_NAMES or opts.get("control_after_generate"):
                i += 1                       # the frontend's control_after_generate widget
        if i != len(wv):
            self.warnings.append("node %s (%s): consumed %d of %d widget values"
                                 % (node["id"], node.get("type"), i, len(wv)))
        return out

    # -- build ----------------------------------------------------------------------------
    def build(self):
        api = {}
        for nid, node in self.nodes.items():
            if not self.is_live(nid):
                continue
            wv = node.get("widgets_values")
            if isinstance(wv, dict):
                inputs = {k: v for k, v in wv.items() if k not in DROP_WIDGET_KEYS}
            elif isinstance(wv, list):
                inputs = self.widget_inputs(node, wv)
            else:
                inputs = {}
            for inp in node.get("inputs") or []:
                link = inp.get("link")
                if link is None:
                    continue
                r = self._follow(link, 0)
                if r is not None:
                    inputs[inp["name"]] = r
                elif "widget" not in inp:
                    inputs.pop(inp["name"], None)
            api[str(nid)] = {"inputs": inputs, "class_type": node["type"],
                             "_meta": {"title": node.get("title") or node["type"]}}
        return api


def collapse_any_switch(api):
    """rgthree's Any Switch declares no inputs, so it cannot be validated the way a normal node
    is; once the dead branches are pruned it has exactly one live input anyway. Rewire its
    consumers straight onto that and delete it."""
    for _ in range(8):
        rewire = {}
        for nid, nd in api.items():
            if nd["class_type"] != "Any Switch (rgthree)":
                continue
            live = [v for v in nd["inputs"].values() if isinstance(v, list)]
            if len(live) == 1:
                rewire[nid] = live[0]
        if not rewire:
            break
        for nd in api.values():
            for name, val in list(nd["inputs"].items()):
                if isinstance(val, list) and len(val) == 2 and val[0] in rewire:
                    nd["inputs"][name] = rewire[val[0]]
        for nid in rewire:
            del api[nid]
    return api


def prune_unreachable(api, keep):
    """Keep only what the surviving output nodes actually consume."""
    alive, stack = set(), [str(k) for k in keep if str(k) in api]
    while stack:
        nid = stack.pop()
        if nid in alive:
            continue
        alive.add(nid)
        for val in api[nid]["inputs"].values():
            if isinstance(val, list) and len(val) == 2 and isinstance(val[0], str):
                stack.append(val[0])
    return {k: v for k, v in api.items() if k in alive}


def apply_cleanups(api):
    for nd in api.values():
        ct, inp = nd["class_type"], nd["inputs"]
        if ct == "LoadSAM3Model":
            inp["model_path"] = SAM3_CHECKPOINT
            inp["hf_token"] = ""
        elif ct == "WanVideoModelLoader":
            inp["model"] = WAN_T2V_LOW
            inp["attention_mode"] = ATTENTION_MODE
            # The checkpoint is a *scaled* fp8 export. Loading it as plain fp8_e4m3fn (what the
            # file was authored with) leaves the scale layers unapplied and the first sampler
            # step dies with "Unexpected floating ScalarType in at::autocast::prioritize";
            # fp16_fast compounds it. bf16 is what actually runs here.
            inp["base_precision"] = "bf16"
            inp["quantization"] = QUANTIZATION
            inp.pop("compile_args", None)
        elif ct == "WanVideoLoraSelect":
            inp["lora"] = LORA_REWRITES.get(inp.get("lora"), inp.get("lora"))
            inp["merge_loras"] = MERGE_LORAS
        elif ct == "WanVideoLoraSelectMulti":
            for i in range(5):
                key = "lora_%d" % i
                if key in inp:
                    inp[key] = LORA_REWRITES.get(inp[key], inp[key])
            inp["merge_loras"] = MERGE_LORAS
        elif ct == "VHS_VideoCombine":
            inp["save_output"] = True
            inp["filename_prefix"] = "TargetedEnhance/enhanced"
    return api


def validate(api, fl):
    print("\n=== VALIDATION ===")
    ids = set(api)
    dangling, missing, unknown = [], [], []
    for nid, nd in api.items():
        for name, val in nd["inputs"].items():
            if isinstance(val, list) and len(val) == 2 and isinstance(val[0], str) \
                    and isinstance(val[1], int) and val[0] not in ids:
                dangling.append((nid, name, val[0]))
        info = fl.obj.get(nd["class_type"])
        if info is None:
            unknown.append((nid, nd["class_type"]))
            continue
        for name in info["input"].get("required", {}):
            if name not in nd["inputs"]:
                missing.append((nid, nd["class_type"], name))
    print("nodes: %d" % len(api))
    for label, rows in (("DANGLING", dangling), ("MISSING", missing), ("UNKNOWN", unknown)):
        print("%s: %d" % (label, len(rows)))
        for r in rows[:40]:
            print("   ", r)
    if fl.warnings:
        print("warnings: %d" % len(fl.warnings))
        for w in fl.warnings[:40]:
            print("   ", w)
    return not (dangling or missing or unknown)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--object-info", default=None)
    ap.add_argument("--out", default=DST)
    args = ap.parse_args()

    with open(SRC, encoding="utf-8") as f:
        ui = json.load(f)
    obj = load_object_info(args.object_info)

    fl = Flattener(ui, obj)
    api = fl.build()
    print("live nodes before prune: %d" % len(api))
    for nid, names in DROP_SWITCH_INPUTS.items():
        for name in names:
            api.get(str(nid), {}).get("inputs", {}).pop(name, None)
    api = collapse_any_switch(api)
    api = prune_unreachable(api, OUTPUT_NODES)
    api = apply_cleanups(api)
    ok = validate(api, fl)

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(api, f, indent=2, ensure_ascii=False)
    print("\nwrote %s" % args.out)
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
