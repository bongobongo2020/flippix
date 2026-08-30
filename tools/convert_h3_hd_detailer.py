#!/usr/bin/env python3
"""
One-off converter: the authored "MiniMax H3 HD / 2K Detailer" graph -> flat API-format JSON.

Source: workflow/video/h3-minimax/zuanfilm_H3_HD_2K_detailer_v3 (1).json   (ComfyUI UI graph)
Output: workflow/video/h3-minimax/h3-hd-detailer.json                      (what FlipPix submits)

What the graph does, and what the "Enhance HD" tab drives:

    load a finished low-res H3 clip -> resize it to the base canvas -> VAEEncode ->
    MinimaxH3LatentUpscaler3D lifts that latent to the detail megapixels -> concat with the
    clip's own audio latent -> one ClownsharKSampler_Beta pass at partial denoise, conditioned
    by MiniMaxH3ReferenceToVideo (the same references and prompt the clip was rendered from) ->
    decode -> Orion4D tone map / texture / sharpen -> save.

    So it is not an upscaler: the H3 model re-renders the clip at HD from its own latent, with
    the reference identity still in the conditioning. Denoise is the dial that decides how much
    of the original survives.

The authored file carries the whole H3 suite - the low-res T2V/FFLF base branch and the REF2VA
base branch are both in it, bypassed, along with four bypassed reference loaders and three
bypassed sinks. Only the HD / 2K DETAILER group is live, and only that group is exported.

Cleanups applied on the way through:
- The sigmas preview branch (SigmasSchedulePreview + the BasicScheduler that only feeds it) is
  dropped. It is an output node, so it survives the reachability prune on its own and would
  render a matplotlib plot into the history of every run; ClownsharKSampler_Beta builds its own
  schedule from its scheduler/steps/denoise widgets and never reads those sigmas.
- The audio switch collapses onto the source clip. ImpactSwitch 795 picks between the loaded
  video's own audio and an uploaded wav (VHS_LoadAudioUpload -> TrimAudioDuration); the tab
  always re-renders a clip that already has its soundtrack, and keeping the loader would make
  every run depend on the author's GATONIEL_2.wav still being on the server. Both consumers
  (the ref2video conditioning and VAEEncodeAudio) are wired straight to the clip instead.
- The unused halves go: the FL2VA UNETLoader (701) the detailer never samples with, the
  low-resolution ResolutionSelector (727), and the duration primitive + expression (594/604)
  that only fed the bypassed base branch. Length comes from the loaded clip via 776/777.

Node 711 is the one node whose widgets_values cannot be mapped positionally: its `mode` is a V3
dynamic combo whose selected branch adds an input of its own, so the seven saved values do not
line up with the seven widget names in any consistent order (keep_proportion lands on "cuda").
WIDGET_OVERRIDES pins it to what the server actually recorded for that node instead. Run with
--check-against to diff a converted graph against a real /history prompt, which is what proved
every other node maps cleanly.

Run:  python tools/convert_h3_hd_detailer.py [--object-info URL_OR_PATH]
"""
import argparse
import json
import os
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax",
                   "zuanfilm_H3_HD_2K_detailer_v3 (1).json")
DST = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-hd-detailer.json")
OBJ_CACHE = os.path.join(ROOT, "tools", ".cache", "object_info_h3hd.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

# Virtual/UI-only node types: no backend implementation at all.
UI_ONLY_TYPES = {
    "Note", "MarkdownNote", "SetNode", "GetNode", "Reroute",
    "Fast Groups Bypasser (rgthree)", "PreviewImage", "ShowText|pysssss",
}

# Preview-only sinks. Dropping them is what makes the reachability prune strip what feeds them.
DROP_TYPES = {"SigmasSchedulePreview"}

PASSTHROUGH_TYPES = {}

# Roots of the reachability prune. 389 is VHS_VideoCombine "SAVE DETAILED VIDEO", the only sink
# that writes a file; 654/655/709 are the RAM/VRAM cleanup nodes the author hung off the resize
# and the concatenated latent. They are output nodes with no consumers, so they need naming here
# or the prune drops them - and at 2K the H3 encode, the 3D latent upscale and the sampler are
# each large enough that the flush between them is the difference between a run and an OOM.
OUTPUT_NODES = {389, 654, 655, 709}

# Nodes authored live that nothing in the detailer needs.
#   594/604 - duration primitive + frame-count expression for the bypassed base branch; the
#             detailer takes its length from the loaded clip (776 -> 777) instead.
#   677     - BasicScheduler, feeding the dropped sigmas preview and nothing else.
#   701     - the FL2VA UNETLoader; the detailer samples with the REF2VA one (595).
#   727     - the low-resolution ResolutionSelector for the bypassed base branch.
#   779/781 - the external-wav branch behind the audio switch, see AUDIO_SOURCE.
FORCE_DROP = {594, 604, 677, 701, 727, 779, 781}

# ImpactSwitch 795 selects the audio. Collapse it onto slot 2 of the video loader - the clip's
# own soundtrack - and rewire its consumers directly.
AUDIO_SWITCH = 795
AUDIO_SOURCE = ["657", 2]

# Widget-backed inputs the frontend adds a second "control_after_generate" widget for.
SEED_NAMES = {"seed", "noise_seed", "rand_seed"}

PRIMITIVE = {"INT", "FLOAT", "STRING", "BOOLEAN", "COMBO"}
DROP_WIDGET_KEYS = {"videopreview", "choose video to upload", "audiopreview",
                    "choose audio to upload"}

# Nodes whose widgets_values cannot be mapped positionally - see the module docstring.
WIDGET_OVERRIDES = {
    711: {"model_name": "minimax_h3_latent_upscaler_3d_fp16.safetensors",
          "mode": "megapixels", "align": 32, "keep_proportion": True,
          "device": "cuda", "precision": "fp16"},
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
        if n is None or nid in FORCE_DROP:
            return False
        t = n.get("type", "")
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

        if nid == AUDIO_SWITCH:
            return list(AUDIO_SOURCE)

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

        if nid in FORCE_DROP or self.mode(nid) == 2 or t in UI_ONLY_TYPES or t in DROP_TYPES:
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
        """Widget-backed input names in frontend order, which here is object_info order.

        This export comes from a frontend that lists a node in `inputs` only when a socket is
        connected or promoted, while `widgets_values` still carries a (stale) value for every
        widget in declaration order. Mapping against the `inputs` array therefore lines the
        values up against the wrong names - ResolutionSelector 707 lists only `megapixels` and
        would take the aspect ratio string as its megapixel count.
        """
        info = self.obj.get(node.get("type", ""), {}).get("input", {})
        names = [name for sect in ("required", "optional")
                 for name, spec in info.get(sect, {}).items()
                 if spec_type(spec) in PRIMITIVE]
        if names:
            return names
        return [inp["name"] for inp in (node.get("inputs") or []) if "widget" in inp]

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
        if node["id"] in WIDGET_OVERRIDES:
            out.update(WIDGET_OVERRIDES[node["id"]])
        elif i != len(wv):
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
        if nd["class_type"] == "VHS_VideoCombine":
            nd["inputs"]["save_output"] = True
            nd["inputs"]["filename_prefix"] = "H3HDEnhance/h3hd"
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
            # V3 autogrow / dynamic-combo groups are declared required but are named
            # "<group>.<slot>" once instantiated, so a prefix match counts as present.
            if name in nd["inputs"]:
                continue
            if any(k.startswith(name + ".") for k in nd["inputs"]):
                continue
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


def check_against(api, path):
    """Diff the converted graph against a real /history prompt, node by node.

    Anything the converter got wrong about widget order shows up here as a value difference on
    a node both graphs have; the deliberate prunes show up as "only in reference".
    """
    with open(path, encoding="utf-8") as f:
        ref = json.load(f)
    if "prompt" in ref:
        ref = ref["prompt"][2]
    print("\n=== DIFF vs %s ===" % os.path.basename(path))
    print("only in converted:", sorted(set(api) - set(ref), key=int))
    print("only in reference:", sorted(set(ref) - set(api), key=int))
    for nid in sorted(set(api) & set(ref), key=int):
        a, b = api[nid]["inputs"], ref[nid]["inputs"]
        for k in sorted(set(a) | set(b)):
            if a.get(k) != b.get(k):
                print("  %s (%s) %s: converted=%r reference=%r"
                      % (nid, api[nid]["class_type"], k, a.get(k), b.get(k)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--object-info", default=None)
    ap.add_argument("--out", default=DST)
    ap.add_argument("--check-against", default=None,
                    help="a /history prompt JSON to diff the result against")
    args = ap.parse_args()

    with open(SRC, encoding="utf-8") as f:
        ui = json.load(f)
    obj = load_object_info(args.object_info)

    fl = Flattener(ui, obj)
    api = fl.build()
    print("live nodes before prune: %d" % len(api))
    api = prune_unreachable(api, OUTPUT_NODES)
    api = apply_cleanups(api)
    ok = validate(api, fl)
    if args.check_against:
        check_against(api, args.check_against)

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(api, f, indent=2, ensure_ascii=False)
    print("\nwrote %s" % args.out)
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
