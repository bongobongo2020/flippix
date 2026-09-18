#!/usr/bin/env python3
"""
One-off converter: the authored "MiniMax SEEDHUNTER v122 EROS-Hybrid" graph -> flat API-format JSON.

Source: workflow/video/h3-minimax/minimaxSEEDHUNTERWorkflow_v122EROS-Hybrid.json  (ComfyUI UI graph)
Output: workflow/video/h3-minimax/h3-eros.json                                    (what FlipPix submits)

What the graph does, and what the "H3 Eros" tab drives:

    one MiniMaxH3ReferenceToVideo conditioning + latent, sampled THREE times at a small draft
    canvas with three different noise seeds -> three preview clips the user picks from ->
    the chosen sample's latent goes through MinimaxH3LatentUpscaler3D to the finished
    megapixels -> a short fixed-sigma "2nd pass" re-samples it there -> RIFE -> final mp4.

So the tab runs the file twice per clip: a HUNT submission pruned to the three preview sinks,
then a FINISH submission pruned to the final sink with the picked sampler wired into the upscale.

Everything the authored graph carries for the other modes is dropped here, because the tab drives
none of it and every one of them is a live node the server would have to resolve:

- the seamless video-continuation half (LoadVideoUI 308 and the whole overlap-frame / audio-splice
  arithmetic hung off it), the FFLF guide branch (MiniMaxH3AddGuide 272/279/296/302 and their
  resizers), the custom-audio latent-mask branch (256/255/257) and the LTXV separate/concat pair
  that only exists to carry it (253/254). All are authored bypassed, so dropping them is what the
  graph already resolves to.
- the reference loaders. LoadImageCrop is a custom node that is NOT installed on the server, and
  FlipPix wires its own LoadImage nodes into MiniMaxH3ReferenceToVideo's autogrow ref_images slots
  at submit time anyway.
- the reference video / reference audio loaders, for the same reason: the tab renders from the
  cast's stills, and LoadVideoUI/LoadAudioUI would pin the graph to files on the author's server.

Collapsed rather than dropped:

- Anything Everywhere (107) broadcasts MODEL / CONDITIONING / SAMPLER / SIGMAS / VAE into every
  unconnected socket of a matching type. The browser resolves that before submit, so a direct
  /prompt POST of the unbaked graph fails validation. Every broadcast is baked into a real link.
- rgthree "Any Switch" returns its first non-null input; each one is replaced by that input.
- ImpactSwitch 122 ("Selected Latent") throws KeyError on 'inputs' under raw /prompt submission (it
  wants UI metadata the browser sends). It is collapsed onto preview #1, and the tab retargets its
  three consumers (242 / 258 / 259) at whichever sampler the user picked.
- easy seed (16 / 103) + the three SimpleCalculatorKJ offsets (a, a+1, a+2) are folded away: each
  RandomNoise carries a literal noise_seed the tab writes per slot, so a re-roll can keep one
  sample and re-roll the other two.
- ModelPreviewOverrideKJ (106) is a live-preview helper on the MODEL wire - a pass-through for a
  headless submit, and it would pull taeh3.safetensors in for nothing.
- Power Lora Loader 333 (the upscale pass's own, empty) collapses onto the main one (21).

Node ids follow ComfyUI's own subgraph convention, "<instance>:<inner>", so the ids in the file
match what the server's history shows.

Run:  python tools/convert_h3_eros.py [--object-info URL_OR_PATH]
"""
import argparse
import json
import os
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax",
                   "minimaxSEEDHUNTERWorkflow_v122EROS-Hybrid.json")
DST = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-eros.json")
OBJ_CACHE = os.path.join(ROOT, "tools", ".cache", "object_info_eros.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

# Virtual/UI-only node types: no backend implementation at all.
UI_ONLY_TYPES = {
    "Note", "MarkdownNote", "SetNode", "GetNode", "Reroute",
    "Fast Groups Bypasser (rgthree)", "Label (rgthree)", "Anything Everywhere",
    "PreviewImage", "PreviewAnimation", "ShowText|pysssss",
}

# Types resolved by walking through them rather than emitting them.
PASSTHROUGH_TYPES = {
    "ModelPreviewOverrideKJ": "model",          # live-preview helper, MODEL in / MODEL out
}

# rgthree's Any Switch: first non-null input wins. Resolved at convert time.
ANY_SWITCH = "Any Switch (rgthree)"

# ImpactSwitch 122 collapses onto this producer - preview #1's denoised latent. The tab retargets.
IMPACT_SWITCH = 122
IMPACT_DEFAULT = (125, 0)     # (ui node id, output slot) -> #1's denoised_output

# Everything the tab never drives. Dropping them is what makes the reachability prune strip the
# arithmetic, resizers and loaders that only feed them.
FORCE_DROP = {
    # video-continuation half
    308, 290, 291, 294, 297, 299, 300, 311, 314, 316, 317, 318, 321, 322, 326, 327, 328, 330,
    334, 338, 339, 341, 342, 343, 344, 345, 346, 347, 349, 352, 261, 270,
    # reference loaders (LoadImageCrop is not installed; FlipPix injects LoadImage instead)
    123, 151, 152, 153, 154, 155, 157, 158, 160, 250, 251,
    # seeds + per-slot offsets, folded into literal RandomNoise widgets
    16, 103,
    # the upscale pass's own (empty) lora loader - its 2nd-pass model comes off the broadcast
    333,
    # the sample selector's INT sources - the tab retargets the consumers instead
    146, 147, 148, 149,
    # save_output toggles + fps switch, written as literals by the tab
    169, 170, 353, 354, 355, 356, 168,
}

# Authored bypassed, forced live here: the whole finalization half. The author leaves it off so a
# click of "Start New Gen" only spends GPU on the three previews; this tab drives both phases from
# the one file and prunes per submission instead, so every node of both paths has to exist.
#   242/243/244  - split the picked latent, upscale the video half, concat the audio half back
#   135:*        - the "2nd Pass" subgraph (RandomNoise / KSamplerSelect / BasicGuider / sampler)
#   189/190      - the upscaled decode  |  259/258 - the single-pass decode of the picked sample
#   220/222      - the 3- and 5-step manual sigma schedules the tab can point the 2nd pass at
FORCE_LIVE = {242, 243, 244, 135, 189, 190, 259, 258, 220, 222}
FORCE_LIVE_INNER = {"135:26", "135:27", "135:29", "135:30"}

# Written after the build, over whatever the authored switch chain resolved to. The author's
# finalization path runs through four rgthree Any Switches whose non-null input is, in the saved
# state, the EmptyImage failsafe - collapsing them faithfully would wire the final sink to a blank
# frame. These are the links the graph has when the upscale pass is the one that ran.
FORCE_LINKS = {
    "165": {"images": ["189", 0]},
    "34": {"images": ["165", 0], "audio": ["190", 0]},
    # 4-step manual sigmas: the schedule the author left live. 224 (the rgthree switch that picks
    # between 3/4/5) collapses onto whichever schedule is first-and-live, which is not the same
    # thing once all three are forced live here.
    "135:26": {"sigmas": ["221", 0]},
}

# Nodes kept through the reachability prune even though nothing consumes them yet - the tab links
# the 2nd pass's sigmas to one of the three schedules at submit time.
#   220/221/222 - the 3/4/5-step schedules
#   258/259     - the single-pass decode of the picked sample, for a run with the upscale off
KEEP_EXTRA = ["220", "221", "222", "258", "259"]

# Subgraph-inner nodes to drop, keyed by "<instance>:<inner>".
FORCE_DROP_INNER = {
    "125:126", "133:127", "143:137",   # SimpleCalculatorKJ seed offsets (a, a+1, a+2)
}

# Roots of the reachability prune: the three preview sinks and the final sink. Nothing else in the
# converted graph is an output node.
OUTPUT_NODES = ["18", "134", "144", "34"]

SEED_NAMES = {"seed", "noise_seed", "rand_seed"}
PRIMITIVE = {"INT", "FLOAT", "STRING", "BOOLEAN", "COMBO"}
DROP_WIDGET_KEYS = {"videopreview", "choose video to upload", "audiopreview",
                    "choose audio to upload"}

# Nodes whose widgets_values do not map positionally - a COMFY_DYNAMICCOMBO_V3 adds an input of its
# own once a branch is picked, so the saved values do not line up with the declared widget order.
WIDGET_OVERRIDES = {
    # rgthree's loader declares no primitive inputs at all, so there is nothing to map its four
    # saved widget values against. This is the shape every other Power Lora Loader in the repo's
    # API workflows has, with no loras on.
    "21": {"PowerLoraLoaderHeaderWidget": {"type": "PowerLoraLoaderHeaderWidget"},
           "➕ Add Lora": ""},
    "243": {"model_name": "minimax_h3_latent_upscaler_3d_bf16.safetensors",
            "mode": "megapixels", "mode.megapixels": 1.0, "align": 32,
            "keep_proportion": True, "device": "cuda", "precision": "fp16"},
}

# Literal values written over whatever the author left in the file. The tab overwrites most of
# these per run; they are set here so the file is submittable as it stands.
FINAL_INPUTS = {
    # the three preview sinks, saved so /view can fetch them by type=output
    "18":  {"filename_prefix": "h3_eros/preview_1", "save_output": True, "frame_rate": 24},
    "134": {"filename_prefix": "h3_eros/preview_2", "save_output": True, "frame_rate": 24},
    "144": {"filename_prefix": "h3_eros/preview_3", "save_output": True, "frame_rate": 24},
    "34":  {"filename_prefix": "h3_eros/final", "save_output": True, "frame_rate": 48},
    "165": {"source_fps": 24.0, "target_fps": 48.0},
    "135:27": {"noise_seed": 0},
    "125:17": {"noise_seed": 0},
    "133:128": {"noise_seed": 1},
    "143:138": {"noise_seed": 2},
    # the reference node ships with no pictures at all - the tab attaches them
    "5": {"ref_image_size": "match"},
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
    with urllib.request.urlopen(url, timeout=600) as r:
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
    """Flattens the UI graph (subgraph instances included) into {api_id: node} records.

    Top-level nodes keep their id; a node inside subgraph instance I gets "I:innerid", matching
    what ComfyUI's own API export writes. Link ids are per-graph, so every lookup carries the
    scope key it belongs to.
    """

    def __init__(self, ui, obj):
        self.obj = obj
        self.warnings = []
        self.subgraphs = {s["id"]: s for s in ui.get("definitions", {}).get("subgraphs", [])}
        self.records = {}   # api_id -> dict(node=, scope=)
        self.scopes = {}    # scope key -> dict(nodes=, links=, parent=, inst=)
        self._add_scope("", ui, parent=None, inst=None)
        self._expand("")

    # -- scope construction ---------------------------------------------------------------
    def _add_scope(self, key, graph, parent, inst):
        links = {}
        for l in graph.get("links", []) or []:
            if isinstance(l, list):
                links[l[0]] = (l[1], l[2], l[3], l[4])          # origin, oslot, target, tslot
            elif isinstance(l, dict):
                links[l["id"]] = (l["origin_id"], l["origin_slot"],
                                  l["target_id"], l["target_slot"])
        self.scopes[key] = {
            "nodes": {n["id"]: n for n in graph.get("nodes", [])},
            "links": links,
            "parent": parent,
            "inst": inst,          # the ui id of the instance node, in the PARENT scope
            "def_inputs": graph.get("inputs") or [],
        }

    def _expand(self, key):
        for nid, node in self.scopes[key]["nodes"].items():
            api = self.api_id(key, nid)
            if node.get("type", "") in self.subgraphs:
                child = api + ":"
                self._add_scope(child, self.subgraphs[node["type"]], parent=key, inst=nid)
                self._expand(child)
            else:
                self.records[api] = {"node": node, "scope": key}

    @staticmethod
    def api_id(scope_key, nid):
        return "%s%s" % (scope_key, nid)

    # -- node state -----------------------------------------------------------------------
    def _dropped(self, scope_key, nid):
        if scope_key == "" and nid in FORCE_DROP:
            return True
        return self.api_id(scope_key, nid) in FORCE_DROP_INNER

    def _forced_live(self, scope_key, nid):
        return ((scope_key == "" and nid in FORCE_LIVE)
                or self.api_id(scope_key, nid) in FORCE_LIVE_INNER)

    def mode(self, scope_key, nid):
        node = self.scopes[scope_key]["nodes"].get(nid)
        if node is None:
            return 2
        return 0 if self._forced_live(scope_key, nid) else node.get("mode", 0)

    def is_live(self, scope_key, nid):
        node = self.scopes[scope_key]["nodes"].get(nid)
        if node is None or self._dropped(scope_key, nid):
            return False
        t = node.get("type", "")
        if t in self.subgraphs:
            return False        # instances are expanded, never emitted
        if scope_key == "" and nid == IMPACT_SWITCH:
            return False
        return (t not in UI_ONLY_TYPES and t not in PASSTHROUGH_TYPES and t != ANY_SWITCH
                and self.mode(scope_key, nid) not in (2, 4))

    # -- producer resolution --------------------------------------------------------------
    def resolve(self, scope_key, nid, slot, depth=0):
        """(scope, node, output slot) -> ["<api id>", slot] for the nearest live producer."""
        if depth > 200:
            self.warnings.append("resolve depth exceeded at %s%s" % (scope_key, nid))
            return None
        scope = self.scopes[scope_key]

        # A subgraph's input node: hop out to the instance's own input socket in the parent scope.
        if nid == -10:
            return self._resolve_instance_input(scope_key, slot, depth)

        node = scope["nodes"].get(nid)
        if node is None:
            return None
        t = node.get("type", "")

        if self._dropped(scope_key, nid) or self.mode(scope_key, nid) == 2:
            return None

        if t in self.subgraphs:
            if self.mode(scope_key, nid) == 4:
                return self._bypass(scope_key, node, slot, depth)
            child = self.api_id(scope_key, nid) + ":"
            for (o, os_, tg, ts) in self.scopes[child]["links"].values():
                if tg == -20 and ts == slot:
                    return self.resolve(child, o, os_, depth + 1)
            self.warnings.append("subgraph %s has no producer for output %d"
                                 % (self.api_id(scope_key, nid), slot))
            return None

        if t == "Reroute":
            return self._follow(scope_key, self._link_at(scope_key, nid, 0), depth)

        if t == ANY_SWITCH:
            for inp in node.get("inputs") or []:
                r = self._follow(scope_key, inp.get("link"), depth)
                if r is not None:
                    return r
            return None

        if t in PASSTHROUGH_TYPES:
            return self._follow(scope_key,
                                self._named_link(scope_key, nid, PASSTHROUGH_TYPES[t]), depth)

        if t in UI_ONLY_TYPES:
            return None

        if scope_key == "" and nid == IMPACT_SWITCH:
            return self.resolve("", IMPACT_DEFAULT[0], IMPACT_DEFAULT[1], depth + 1)

        if self.mode(scope_key, nid) == 4:
            return self._bypass(scope_key, node, slot, depth)

        return [self.api_id(scope_key, nid), slot]

    def _bypass(self, scope_key, node, slot, depth):
        """Bypass forwards the first input whose type matches the requested output."""
        outs = node.get("outputs") or []
        want = outs[slot].get("type") if slot < len(outs) else None
        for inp in node.get("inputs") or []:
            if inp.get("link") is None:
                continue
            itype = inp.get("type")
            if itype == want or (want and isinstance(itype, str) and want in str(itype).split(",")):
                return self._follow(scope_key, inp["link"], depth)
        return None

    def _resolve_instance_input(self, scope_key, slot, depth):
        scope = self.scopes[scope_key]
        parent, inst = scope["parent"], scope["inst"]
        if parent is None:
            return None
        link = self._link_at(parent, inst, slot)
        return self._follow(parent, link, depth) if link is not None else None

    def _link_at(self, scope_key, nid, index):
        node = self.scopes[scope_key]["nodes"].get(nid)
        inputs = (node.get("inputs") or []) if node else []
        return inputs[index].get("link") if index < len(inputs) else None

    def promoted(self, scope_key):
        """Widget values the subgraph INSTANCE carries, by definition-input slot.

        A promoted widget lives in two places at once - on the instance and on the inner node it
        drives - and this file's two copies disagree (the instance says the 10Eros hybrid UNET and
        12 steps at 0.3 MP; the inner nodes still say the fl2va UNET and 8 steps at 0.2 MP). The
        instance is the one the frontend sends, so it wins.
        """
        scope = self.scopes[scope_key]
        parent, inst = scope["parent"], scope["inst"]
        if parent is None:
            return {}
        node = self.scopes[parent]["nodes"].get(inst)
        wv = node.get("widgets_values") if node else None
        if not isinstance(wv, list):
            return {}
        out, i = {}, 0
        for slot, spec in enumerate(scope["def_inputs"]):
            if spec.get("type") not in ("STRING", "INT", "FLOAT", "BOOLEAN", "COMBO"):
                continue
            if i < len(wv):
                out[slot] = wv[i]
            i += 1
        return out

    def promoted_value(self, scope_key, link):
        """The instance widget value behind an inner link that came from an unwired subgraph input."""
        l = self.scopes[scope_key]["links"].get(link)
        if not l or l[0] != -10:
            return None, False
        scope = self.scopes[scope_key]
        if self._link_at(scope["parent"], scope["inst"], l[1]) is not None:
            return None, False          # the socket is wired at the instance; not a widget
        vals = self.promoted(scope_key)
        return (vals[l[1]], True) if l[1] in vals else (None, False)

    def _named_link(self, scope_key, nid, name):
        node = self.scopes[scope_key]["nodes"].get(nid)
        for inp in (node.get("inputs") or []) if node else []:
            if inp.get("name") == name:
                return inp.get("link")
        return None

    def _follow(self, scope_key, link, depth):
        if link is None:
            return None
        l = self.scopes[scope_key]["links"].get(link)
        return self.resolve(scope_key, l[0], l[1], depth + 1) if l else None

    # -- widget mapping -------------------------------------------------------------------
    def widget_names(self, node):
        info = self.obj.get(node.get("type", ""), {}).get("input", {})
        names = [name for sect in ("required", "optional")
                 for name, spec in info.get(sect, {}).items()
                 if spec_type(spec) in PRIMITIVE]
        if names:
            return names
        return [inp["name"] for inp in (node.get("inputs") or []) if "widget" in inp]

    def widget_inputs(self, node, api_id, wv):
        out, i = {}, 0
        info = self.obj.get(node.get("type", ""), {}).get("input", {})
        specs = {n: s for sect in ("required", "optional") for n, s in info.get(sect, {}).items()}
        for name in self.widget_names(node):
            if i >= len(wv):
                break
            out[name] = wv[i]
            i += 1
            opts = spec_opts(specs.get(name, []))
            if name in SEED_NAMES or opts.get("control_after_generate"):
                i += 1                       # the frontend's control_after_generate widget
        if api_id in WIDGET_OVERRIDES:
            out = dict(WIDGET_OVERRIDES[api_id])
        elif i != len(wv):
            self.warnings.append("node %s (%s): consumed %d of %d widget values"
                                 % (api_id, node.get("type"), i, len(wv)))
        return out

    # -- build ----------------------------------------------------------------------------
    def build(self):
        api = {}
        for api_id, rec in self.records.items():
            node, scope_key = rec["node"], rec["scope"]
            if not self.is_live(scope_key, node["id"]):
                continue
            wv = node.get("widgets_values")
            if isinstance(wv, dict):
                inputs = {k: v for k, v in wv.items() if k not in DROP_WIDGET_KEYS}
            elif isinstance(wv, list):
                inputs = self.widget_inputs(node, api_id, wv)
            else:
                inputs = {}
            for inp in node.get("inputs") or []:
                name = inp.get("name")
                link = inp.get("link")
                r = self._follow(scope_key, link, 0) if link is not None else None
                if r is not None:
                    inputs[name] = r
                    continue
                if link is None:
                    continue
                value, promoted = self.promoted_value(scope_key, link)
                if promoted:
                    inputs[name] = value
                elif "widget" not in inp:
                    inputs.pop(name, None)
            api[api_id] = {"inputs": inputs, "class_type": node["type"],
                           "_meta": {"title": node.get("title") or node["type"]}}
        return api

    # -- Anything Everywhere --------------------------------------------------------------
    def broadcasts(self):
        """type -> ["api id", slot], from every live 'Anything Everywhere' node."""
        out = {}
        for rec in self.records.values():
            node, scope_key = rec["node"], rec["scope"]
            if node.get("type") != "Anything Everywhere" or node.get("mode", 0) in (2, 4):
                continue
            for inp in node.get("inputs") or []:
                link = inp.get("link")
                if link is None:
                    continue
                l = self.scopes[scope_key]["links"].get(link)
                if not l:
                    continue
                r = self.resolve(scope_key, l[0], l[1], 0)
                if r is None:
                    continue
                # the broadcast's type is the producing socket's declared type
                t = inp.get("type")
                src = self.scopes[scope_key]["nodes"].get(l[0])
                if t in (None, "*", "") and src:
                    outs = src.get("outputs") or []
                    t = outs[l[1]].get("type") if l[1] < len(outs) else None
                if t:
                    out[t] = r
        return out


def bake_broadcasts(api, casts, obj):
    """Fill every unconnected REQUIRED socket whose declared type matches a broadcast.

    Required only, deliberately. cg-use-everywhere would fill the optional ones too, and the one
    that matters is VHS_VideoCombine's `vae` - present so the node can take latents instead of
    images, and a VAE sitting in it alongside an IMAGE batch is at best noise in the graph.
    """
    filled = 0
    for nd in api.values():
        info = obj.get(nd["class_type"])
        if info is None:
            continue
        for name, spec in info["input"].get("required", {}).items():
            t = spec_type(spec)
            if t in PRIMITIVE or t not in casts or name in nd["inputs"]:
                continue
            nd["inputs"][name] = list(casts[t])
            filled += 1
    return filled


def prune_unreachable(api, keep):
    alive, stack = set(), [k for k in keep if k in api]
    while stack:
        nid = stack.pop()
        if nid in alive:
            continue
        alive.add(nid)
        for val in api[nid]["inputs"].values():
            if isinstance(val, list) and len(val) == 2 and isinstance(val[0], str):
                stack.append(val[0])
    return {k: v for k, v in api.items() if k in alive}


def apply_final_inputs(api, warnings):
    for nid, links in FORCE_LINKS.items():
        if nid not in api:
            warnings.append("FORCE_LINKS: node %s is not in the graph" % nid)
            continue
        api[nid]["inputs"].update({k: list(v) for k, v in links.items()})
    for nid, inputs in FINAL_INPUTS.items():
        if nid not in api:
            warnings.append("FINAL_INPUTS: node %s is not in the graph" % nid)
            continue
        api[nid]["inputs"].update(inputs)
    # The autogrow reference slots are the tab's to fill; drop whatever survived the authored file.
    for nd in api.values():
        if nd["class_type"] == "MiniMaxH3ReferenceToVideo":
            for k in [k for k in nd["inputs"]
                      if k.startswith(("ref_images.", "ref_videos.", "ref_video_audios.",
                                       "ref_audios."))]:
                nd["inputs"].pop(k)
    return api


def validate(api, obj, warnings):
    print("\n=== VALIDATION ===")
    ids = set(api)
    dangling, missing, unknown = [], [], []
    for nid, nd in api.items():
        for name, val in nd["inputs"].items():
            if (isinstance(val, list) and len(val) == 2 and isinstance(val[0], str)
                    and isinstance(val[1], int) and val[0] not in ids):
                dangling.append((nid, name, val[0]))
        info = obj.get(nd["class_type"])
        if info is None:
            unknown.append((nid, nd["class_type"]))
            continue
        for name, spec in info["input"].get("required", {}).items():
            if name in nd["inputs"]:
                continue
            if any(k.startswith(name + ".") for k in nd["inputs"]):
                continue
            if spec_type(spec) == "COMFY_AUTOGROW_V3":
                continue
            missing.append((nid, nd["class_type"], name))
    print("nodes: %d" % len(api))
    for label, rows in (("DANGLING", dangling), ("MISSING", missing), ("UNKNOWN", unknown)):
        print("%s: %d" % (label, len(rows)))
        for r in rows[:40]:
            print("   ", r)
    if warnings:
        print("warnings: %d" % len(warnings))
        for w in warnings[:60]:
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

    casts = fl.broadcasts()
    print("broadcasts: %s" % casts)
    print("broadcast sockets baked: %d" % bake_broadcasts(api, casts, obj))

    for nid, links in FORCE_LINKS.items():
        if nid in api:
            api[nid]["inputs"].update({k: list(v) for k, v in links.items()})
    api = prune_unreachable(api, OUTPUT_NODES + KEEP_EXTRA)
    api = apply_final_inputs(api, fl.warnings)
    ok = validate(api, obj, fl.warnings)

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(api, f, indent=2, ensure_ascii=False)
    print("\nwrote %s (%d nodes)" % (args.out, len(api)))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
