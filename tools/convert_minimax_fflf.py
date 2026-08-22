#!/usr/bin/env python3
"""
One-off converter: the authored MiniMax H3 FL2VA graph -> flat API-format JSON.

Source: workflow/video/h3-minimax/minimax-fflf.json  (ComfyUI UI graph, 9 subgraphs,
        SetNode/GetNode broadcast pairs, Reroutes, muted branches)
Output: workflow/video/h3-minimax/h3-minimax-fflf.json (what FlipPix submits)

FlipPix can only execute flat API-format workflows ({nodeId: {inputs, class_type}}), so this
flattens the graph the way ComfyUI's "Save (API format)" does, resolving promoted subgraph
widgets to literals and using a live /object_info dump to map positional widget arrays onto
named inputs.

Cleanups applied on the way through (same reasoning as the MiniMax I2V conversion):
- The in-graph DenoLocalLLMRefiner ("Generate Prompt") and its Ollama settings are stripped.
  FlipPix writes the FL2VA prompt itself so it can be read and edited before a render is spent.
- ModelPreviewOverrideKJ.preview_frames was wired to the clip's frame count, re-encoding the
  whole take as an animated preview every sampling step for a preview a headless client never
  sees. Pinned to 1.
- ResolutionSelector emits multiples of 64, not 32: the detail pass computes its 1.5x twice
  (once on pixel dims for the conditioning latent, once inside the 3D latent upscaler) and the
  two round apart whenever base*1.5 is not 32-aligned, which kills the sampler with a token
  mismatch on most aspect ratios.
- The "Switch Source" video branch (LoadVideoUI, muted as authored) is dropped; the extend loop
  always continues the base pass. FlipPix has no video-upload UI on this tab.
- The per-extension end-frame chain (Last Frame-1..3 + Last Frame Switch), muted as authored,
  is force-enabled: it is the whole point of the tab.

Run:  python tools/convert_minimax_fflf.py [--object-info URL_OR_PATH]
"""
import argparse
import json
import os
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax", "minimax-fflf.json")
DST = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-minimax-fflf.json")
OBJ_CACHE = os.path.join(ROOT, "tools", ".cache", "object_info_h3.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

# Virtual/UI-only node types: they carry no backend implementation.
UI_ONLY_TYPES = {"Note", "MarkdownNote", "Group Controller", "SetNode", "GetNode", "PreviewAny"}
# Nodes muted in the authored file that FlipPix drives per submission.
FORCE_ENABLE = {3477, 3478, 3479, 3480}
# Subgraphs collapsed to a passthrough: {subgraph name: {output slot: instance input slot}}.
PASSTHROUGH_SUBGRAPHS = {
    "Generate Prompt": {0: 0},      # LLM refiner -> the prompt that was fed to it
    "Switch Source": {0: 0, 1: 2},  # video branch dropped -> base pass images/audio
}
DROP_SUBGRAPHS = {"Ollama"}         # only feeds the stripped refiner
DROP_TOP_NODES = {3396}             # LoadVideoUI (the dropped Switch Source branch)

PRIMITIVE = {"INT", "FLOAT", "STRING", "BOOLEAN", "COMBO"}
AUTOGROW = "COMFY_AUTOGROW_V3"
DYNAMICCOMBO = "COMFY_DYNAMICCOMBO_V3"
DROP_WIDGET_KEYS = {"videopreview"}


def load_object_info(src):
    if src and os.path.exists(src):
        with open(src, encoding="utf-8") as f:
            return json.load(f)
    if os.path.exists(OBJ_CACHE):
        with open(OBJ_CACHE, encoding="utf-8") as f:
            return json.load(f)
    url = src or OBJ_URL
    print(f"Fetching object_info from {url} ...")
    with urllib.request.urlopen(url, timeout=300) as r:
        data = json.loads(r.read().decode("utf-8"))
    os.makedirs(os.path.dirname(OBJ_CACHE), exist_ok=True)
    with open(OBJ_CACHE, "w", encoding="utf-8") as f:
        json.dump(data, f)
    return data


def spec_type(spec):
    """Declared type of an object_info input spec: a list of options means COMBO."""
    t = spec[0] if isinstance(spec, list) and spec else spec
    return "COMBO" if isinstance(t, list) else t


def spec_opts(spec):
    return spec[1] if isinstance(spec, list) and len(spec) > 1 and isinstance(spec[1], dict) else {}


class Flattener:
    def __init__(self, ui, obj):
        self.ui = ui
        self.obj = obj
        self.subgraphs = {sg["id"]: sg for sg in ui["definitions"]["subgraphs"]}
        self.top = {n["id"]: n for n in ui["nodes"]}
        self.nodes = {}         # api id -> {type, node, scope}
        self.dropped = set()
        self.set_sources = {}   # SetNode variable name -> (scope, node_id, slot)
        self.warnings = []

    # ── scope helpers ────────────────────────────────────────────────────────────────────
    @staticmethod
    def sid(scope, nid):
        return f"{scope}:{nid}" if scope else str(nid)

    def is_sub(self, t):
        return t in self.subgraphs

    def sub_of_scope(self, scope):
        """The subgraph definition an inner scope belongs to (scope == chain of instance ids)."""
        outer, _, inst = scope.rpartition(":")
        node = self.node_in_scope(outer, int(inst))
        return self.subgraphs[node["type"]]

    def instance_of_scope(self, scope):
        outer, _, inst = scope.rpartition(":")
        return outer, self.node_in_scope(outer, int(inst))

    def node_in_scope(self, scope, nid):
        if scope == "":
            return self.top.get(nid)
        return next((n for n in self.sub_of_scope(scope)["nodes"] if n["id"] == nid), None)

    def link_origin(self, scope, link_id):
        if scope == "":
            for l in self.ui["links"]:
                if l[0] == link_id:
                    return l[1], l[2]
            return None
        for l in self.sub_of_scope(scope)["links"]:
            if l["id"] == link_id:
                return l["origin_id"], l["origin_slot"]
        return None

    def sub_output_driver(self, sg, slot):
        for l in sg["links"]:
            if l["target_id"] == -20 and l["target_slot"] == slot:
                return l["origin_id"], l["origin_slot"]
        return None

    # ── pass 1: collect concrete nodes ───────────────────────────────────────────────────
    def collect(self):
        for n in self.ui["nodes"]:
            self._collect(n, "")

    def _collect(self, n, scope):
        t = n.get("type", "")
        if self.is_sub(t):
            sg = self.subgraphs[t]
            if sg["name"] in DROP_SUBGRAPHS or sg["name"] in PASSTHROUGH_SUBGRAPHS:
                return
            inner_scope = self.sid(scope, n["id"])
            for inner in sg["nodes"]:
                self._collect(inner, inner_scope)
            return
        if t in UI_ONLY_TYPES or t == "Reroute":
            return
        if scope == "" and n["id"] in DROP_TOP_NODES:
            return
        self.nodes[self.sid(scope, n["id"])] = {"type": t, "node": n, "scope": scope}

    # ── pass 2: SetNode variables ────────────────────────────────────────────────────────
    def collect_set_nodes(self):
        for n in self.ui["nodes"]:
            if n.get("type") != "SetNode":
                continue
            name = (n.get("widgets_values") or [""])[0]
            inp = (n.get("inputs") or [None])[0]
            link = inp.get("link") if inp else None
            if link is None:
                continue
            oo = self.link_origin("", link)
            if oo:
                self.set_sources[name] = ("", oo[0], oo[1])

    # ── pass 3: muted nodes ──────────────────────────────────────────────────────────────
    def compute_dropped(self):
        for api_id, rec in self.nodes.items():
            chain = api_id.split(":")
            top_id = int(chain[0])
            if top_id in FORCE_ENABLE:
                continue
            muted = self.top[top_id].get("mode", 0) in (2, 4) or rec["node"].get("mode", 0) in (2, 4)
            if muted:
                self.dropped.add(api_id)

    # ── producer resolution ──────────────────────────────────────────────────────────────
    def resolve(self, scope, node_id, slot, depth=0):
        """Resolve (scope, node, slot) to an API [id, slot] pair, a literal, or None."""
        if depth > 80:
            self.warnings.append(f"resolve depth exceeded at {scope}:{node_id}")
            return None

        if node_id == -10:  # subgraph input boundary
            outer, inst = self.instance_of_scope(scope)
            inputs = inst.get("inputs", [])
            if slot < len(inputs) and inputs[slot].get("link") is not None:
                oo = self.link_origin(outer, inputs[slot]["link"])
                if oo is None:
                    return None
                return self.resolve(outer, oo[0], oo[1], depth + 1)
            # unconnected: the instance carries the promoted widget's literal value
            return self.promoted_widget(inst, slot)

        node = self.node_in_scope(scope, node_id)
        if node is None:
            return None
        t = node.get("type", "")

        if t == "Reroute":
            inp = (node.get("inputs") or [None])[0]
            link = inp.get("link") if inp else None
            if link is None:
                return None
            oo = self.link_origin(scope, link)
            return self.resolve(scope, oo[0], oo[1], depth + 1) if oo else None

        if t == "GetNode":
            name = (node.get("widgets_values") or [""])[0]
            src = self.set_sources.get(name)
            if src is None:
                self.warnings.append(f"GetNode '{name}' has no SetNode source")
                return None
            return self.resolve(src[0], src[1], src[2], depth + 1)

        if self.is_sub(t):
            sg = self.subgraphs[t]
            through = PASSTHROUGH_SUBGRAPHS.get(sg["name"])
            if through is not None:
                in_slot = through.get(slot)
                if in_slot is None:
                    return None
                return self.resolve(scope, node_id, -1, depth + 1) if False else \
                    self._instance_input(scope, node, in_slot, depth + 1)
            if sg["name"] in DROP_SUBGRAPHS:
                return None
            drv = self.sub_output_driver(sg, slot)
            if drv is None:
                return None
            return self.resolve(self.sid(scope, node_id), drv[0], drv[1], depth + 1)

        api_id = self.sid(scope, node_id)
        if api_id in self.dropped or api_id not in self.nodes:
            return None
        return [api_id, slot]

    def _instance_input(self, scope, inst, slot, depth):
        """Resolve what feeds a subgraph instance's input slot (link or promoted literal)."""
        inputs = inst.get("inputs", [])
        if slot < len(inputs) and inputs[slot].get("link") is not None:
            oo = self.link_origin(scope, inputs[slot]["link"])
            return self.resolve(scope, oo[0], oo[1], depth) if oo else None
        return self.promoted_widget(inst, slot)

    def promoted_widget(self, inst, slot):
        """Literal value of a subgraph instance's promoted widget for declared input `slot`."""
        sg = self.subgraphs[inst["type"]]
        decl = sg.get("inputs", [])
        if slot >= len(decl):
            return None
        widget_slots = [i for i, d in enumerate(decl)
                        if (d.get("type") or "").upper() in PRIMITIVE]
        if slot not in widget_slots:
            return None
        wv = inst.get("widgets_values") or []
        idx = widget_slots.index(slot)
        if idx >= len(wv):
            self.warnings.append(f"instance {inst['id']} missing widget value for "
                                 f"'{decl[slot].get('name')}'")
            return None
        return wv[idx]

    # ── widget mapping ───────────────────────────────────────────────────────────────────
    def widget_inputs(self, class_type, wv):
        """Positional widgets_values -> named inputs, honouring autogrow/dynamic-combo widgets."""
        info = self.obj.get(class_type, {}).get("input", {})
        out, i = {}, 0
        for sect in ("required", "optional"):
            for name, spec in info.get(sect, {}).items():
                t = spec_type(spec)
                if t == AUTOGROW:
                    continue                       # dynamic sockets, no positional value
                if isinstance(t, str) and t not in PRIMITIVE and t != DYNAMICCOMBO:
                    continue                       # a real socket
                if i >= len(wv):
                    continue
                if t == DYNAMICCOMBO:
                    key = wv[i]; i += 1
                    out[name] = key
                    opt = next((o for o in spec_opts(spec).get("options", [])
                                if o.get("key") == key), None)
                    for sub in (opt or {}).get("inputs", {}).get("required", {}):
                        if i < len(wv):
                            out[f"{name}.{sub}"] = wv[i]; i += 1
                    continue
                out[name] = wv[i]; i += 1
        return out

    def socket_inputs(self, class_type):
        info = self.obj.get(class_type, {}).get("input", {})
        sockets, required = {}, set()
        for sect in ("required", "optional"):
            for name, spec in info.get(sect, {}).items():
                t = spec_type(spec)
                if isinstance(t, str) and t not in PRIMITIVE and t not in (AUTOGROW, DYNAMICCOMBO):
                    sockets[name] = t
                    if sect == "required":
                        required.add(name)
        return sockets, required

    # ── build ────────────────────────────────────────────────────────────────────────────
    def build(self):
        api = {}
        for api_id, rec in self.nodes.items():
            if api_id in self.dropped:
                continue
            node, scope, t = rec["node"], rec["scope"], rec["type"]
            wv = node.get("widgets_values")
            if isinstance(wv, dict):
                inputs = {k: v for k, v in wv.items() if k not in DROP_WIDGET_KEYS}
            elif isinstance(wv, list):
                inputs = self.widget_inputs(t, wv)
            else:
                inputs = {}
            for inp in node.get("inputs", []):
                link = inp.get("link")
                if link is None:
                    continue
                oo = self.link_origin(scope, link)
                if oo is None:
                    continue
                r = self.resolve(scope, oo[0], oo[1])
                if r is not None:
                    inputs[inp["name"]] = r
            api[api_id] = {"inputs": inputs, "class_type": t,
                           "_meta": {"title": node.get("title", "") or t}}
        return api


def collapse_switches(api):
    """A ComfySwitchNode whose selected branch survived but whose other branch was dropped is
    rewritten as a passthrough, so the dead branch does not leave a required socket empty."""
    for _ in range(6):
        rewire = {}
        for nid, nd in list(api.items()):
            if nd["class_type"] != "ComfySwitchNode":
                continue
            have_f, have_t = "on_false" in nd["inputs"], "on_true" in nd["inputs"]
            if have_f and have_t:
                continue
            keep = nd["inputs"].get("on_true") if have_t else nd["inputs"].get("on_false")
            if keep is None:
                continue
            rewire[nid] = keep
        if not rewire:
            break
        for nid, nd in api.items():
            for name, val in list(nd["inputs"].items()):
                if isinstance(val, list) and len(val) == 2 and val[0] in rewire:
                    nd["inputs"][name] = rewire[val[0]]
        for nid in rewire:
            del api[nid]
    return api


def apply_cleanups(api):
    for nid, nd in api.items():
        if nd["class_type"] == "ModelPreviewOverrideKJ":
            nd["inputs"]["preview_frames"] = 1
        if nd["class_type"] == "ResolutionSelector":
            nd["inputs"]["multiple"] = 64
    return api


def validate(api, fl):
    print("\n=== VALIDATION ===")
    ids = set(api)
    dangling = [(nid, name, val[0])
                for nid, nd in api.items()
                for name, val in nd["inputs"].items()
                if isinstance(val, list) and len(val) == 2 and isinstance(val[0], str)
                and isinstance(val[1], int) and val[0] not in ids]
    missing = []
    unknown = []
    for nid, nd in api.items():
        if nd["class_type"] not in fl.obj:
            unknown.append((nid, nd["class_type"]))
            continue
        sockets, required = fl.socket_inputs(nd["class_type"])
        for rname in required:
            if rname not in nd["inputs"]:
                missing.append((nid, nd["class_type"], rname))
    print(f"nodes: {len(api)}")
    print(f"dangling refs: {len(dangling)}")
    for d in dangling[:40]:
        print("   DANGLING", d)
    print(f"missing required socket inputs: {len(missing)}")
    for m in missing[:60]:
        print("   MISSING", m)
    print(f"classes not on the server: {len(unknown)}")
    for u in unknown[:20]:
        print("   UNKNOWN", u)
    if fl.warnings:
        print(f"warnings ({len(fl.warnings)}):")
        for w in fl.warnings[:40]:
            print("   ", w)
    return dangling, missing, unknown


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--object-info", default=None, help="URL or path to an object_info dump")
    args = ap.parse_args()

    with open(SRC, encoding="utf-8") as f:
        ui = json.load(f)
    obj = load_object_info(args.object_info)
    print("object_info classes:", len(obj))

    fl = Flattener(ui, obj)
    fl.collect()
    fl.collect_set_nodes()
    fl.compute_dropped()
    print("concrete nodes:", len(fl.nodes), " dropped (muted):", len(fl.dropped))
    api = apply_cleanups(collapse_switches(fl.build()))
    validate(api, fl)

    with open(DST, "w", encoding="utf-8") as f:
        json.dump(api, f, indent=2)
    print(f"\nWROTE {DST}")
    counts = {}
    for nd in api.values():
        counts[nd["class_type"]] = counts.get(nd["class_type"], 0) + 1
    for c in ("LoadImage", "MiniMaxH3ImageToVideo", "VHS_VideoCombine", "easy forLoopStart",
              "easy anythingIndexSwitch", "DenoLocalLLMRefiner", "LoadVideoUI"):
        print(f"   {c}: {counts.get(c, 0)}")


if __name__ == "__main__":
    main()
