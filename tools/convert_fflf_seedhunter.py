#!/usr/bin/env python3
"""
One-off converter: ComfyUI UI-graph -> flat API-format JSON for the FFLF Seed Hunter page.

The FlipPix runtime can only execute flat API-format workflows ({nodeId:{inputs,class_type}}).
The authored file `ltx23FFLFSeedHunter_v162STAGEUPDATE.json` is a UI graph with 8 subgraphs,
an "Anything Everywhere" broadcast node, rgthree group muters and Any-Switch nodes. This script
flattens it the way ComfyUI's "Save (API format)" does, using a live /object_info dump to map
positional widget arrays to named inputs.

Run once (ComfyUI reachable, or a cached tools/.cache/object_info.json present):
    python tools/convert_fflf_seedhunter.py

Output: workflow/video/ltx/ltx23fflf-seedhunter-api.json

Conventions:
- Inner subgraph nodes are renamed "<instanceId>:<innerId>" (matches seed-hunter-api.json).
- Subgraph boundary nodes use ComfyUI's inputNode id -10 and outputNode id -20.
"""
import json, os, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "ltx", "ltx23FFLFSeedHunter_v162STAGEUPDATE.json")
DST = os.path.join(ROOT, "workflow", "video", "ltx", "ltx23fflf-seedhunter-api.json")
REF = os.path.join(ROOT, "workflow", "video", "ltx", "seed-hunter-api.json")
OBJ_CACHE = os.path.join(ROOT, "tools", ".cache", "object_info.json")
OBJ_URL = "http://192.168.1.10:8188/object_info"

# Finish-mode nodes muted in the authored "hunt" state but kept so the ViewModel can re-enable
# them per phase (mirrors seed-hunter-api.json keeping all stage-2/3 nodes).
# Finish chain: ImpactSwitch 5173 -> Separate 5177 -> CropGuides 5207 -> Stage2 5012
# -> Decode 5027 -> Final 5033. (5125/5154 are the *video* end-frame counter, not used for
# image FFLF, so they stay dropped and the "Amt of End Frames" switch resolves to the image count.)
FORCE_ENABLE = {5012, 5027, 5033, 5173, 5177, 5207}
UI_ONLY_TYPES = {"Note", "MarkdownNote", "Anything Everywhere", "Anything Everywhere3",
                 "Fast Groups Muter (rgthree)", "Fast Groups Bypasser (rgthree)"}
AE_TYPES = {"Anything Everywhere", "Anything Everywhere3"}
ANY_SWITCH = "Any Switch (rgthree)"

PRIMITIVE = {"INT", "FLOAT", "STRING", "BOOLEAN", "COMBO"}
WIDGET_CUSTOM = {"COMFY_AUTOGROW_V3"}
DROP_WIDGET_KEYS = {"videopreview"}
SEED_CONTROL = {"fixed", "randomize", "increment", "decrement"}


def load_object_info():
    if os.path.exists(OBJ_CACHE):
        with open(OBJ_CACHE, encoding="utf-8") as f:
            return json.load(f)
    print(f"Fetching object_info from {OBJ_URL} ...")
    with urllib.request.urlopen(OBJ_URL, timeout=120) as r:
        data = json.loads(r.read().decode("utf-8"))
    os.makedirs(os.path.dirname(OBJ_CACHE), exist_ok=True)
    with open(OBJ_CACHE, "w", encoding="utf-8") as f:
        json.dump(data, f)
    return data


class Flattener:
    def __init__(self, ui, obj):
        self.ui = ui
        self.obj = obj
        self.subgraphs = {sg["id"]: sg for sg in ui["definitions"]["subgraphs"]}
        self.top = {n["id"]: n for n in ui["nodes"]}
        self.warnings = []
        self.dropped = set()        # api id strings removed (muted / UI-only)
        self.ae_sources = {}        # type -> [api_id, slot]
        self.ae_filled = []         # diagnostics: (api_id, class_type, input_name, type)

    # ---------- scope / lookup helpers ----------
    @staticmethod
    def sid(scope, nid):
        return f"{scope}:{nid}" if scope else str(nid)

    def is_sub(self, t):
        return t in self.subgraphs

    def scope_subgraph(self, scope):
        """The subgraph definition for an inner-node scope (== str(instance id))."""
        inst = self.top[int(scope)]
        return self.subgraphs[inst["type"]]

    def node_in_scope(self, scope, nid):
        if scope == "":
            return self.top.get(nid)
        return next((n for n in self.scope_subgraph(scope)["nodes"] if n["id"] == nid), None)

    def link_origin(self, scope, link_id):
        """(origin_node_id, origin_slot) for a link id within a scope's link space."""
        if scope == "":
            for l in self.ui["links"]:
                if l[0] == link_id:
                    return l[1], l[2]
            return None
        for l in self.scope_subgraph(scope)["links"]:
            if l["id"] == link_id:
                return l["origin_id"], l["origin_slot"]
        return None

    def subgraph_output_driver(self, sg, slot):
        """Inner (node_id, slot) that drives a subgraph instance output slot."""
        for l in sg["links"]:
            if l["target_id"] == -20 and l["target_slot"] == slot:
                return l["origin_id"], l["origin_slot"]
        return None

    # ---------- pass 1: collect concrete nodes ----------
    def collect(self):
        for n in self.ui["nodes"]:
            self._collect(n, "")

    def _collect(self, n, scope):
        t = n.get("type", "")
        if self.is_sub(t):
            inst_scope = self.sid(scope, n["id"])
            for inner in self.subgraphs[t]["nodes"]:
                self._collect(inner, inst_scope)
            return
        if t in UI_ONLY_TYPES:
            return
        self.registry_add(self.sid(scope, n["id"]), t, n, scope)

    def registry_add(self, api_id, t, node, scope):
        if not hasattr(self, "nodes"):
            self.nodes = {}
        self.nodes[api_id] = {"type": t, "node": node, "scope": scope}

    # ---------- pass 2: compute dropped (muted) set ----------
    def compute_dropped(self):
        for api_id, rec in self.nodes.items():
            scope = rec["scope"]
            # determine the top-level ancestor mode
            top_id = int(scope) if scope else rec["node"]["id"]
            top_mode = self.top[top_id].get("mode", 0)
            node_mode = rec["node"].get("mode", 0)
            muted = top_mode in (2, 4) or node_mode in (2, 4)
            if muted and top_id not in FORCE_ENABLE:
                self.dropped.add(api_id)

    # ---------- producer resolution ----------
    def resolve_producer(self, scope, node_id, slot, depth=0):
        if depth > 50:
            return None
        if node_id == -10:  # subgraph input boundary -> external
            inst_id = int(scope)
            inst = self.top[inst_id]
            inputs = inst.get("inputs", [])
            if slot >= len(inputs):
                return None
            link = inputs[slot].get("link")
            if link is None:
                return None  # unconnected promoted widget
            oid, oslot = self.link_origin("", link)
            return self.resolve_producer("", oid, oslot, depth + 1)
        node = self.node_in_scope(scope, node_id)
        if node is None:
            return None
        t = node.get("type", "")
        if self.is_sub(t):
            sg = self.subgraphs[t]
            drv = self.subgraph_output_driver(sg, slot)
            if drv is None:
                return None
            return self.resolve_producer(self.sid(scope, node_id), drv[0], drv[1], depth + 1)
        if t == ANY_SWITCH:
            for inp in node.get("inputs", []):
                l = inp.get("link")
                if l is None:
                    continue
                oo = self.link_origin(scope, l)
                if oo is None:
                    continue
                r = self.resolve_producer(scope, oo[0], oo[1], depth + 1)
                if r and r[0] not in self.dropped:
                    return r
            return None
        api_id = self.sid(scope, node_id)
        if api_id in self.dropped:
            return None
        return [api_id, slot]

    # ---------- Anything Everywhere sources ----------
    def collect_ae(self):
        for n in self.ui["nodes"]:
            if n.get("type") not in AE_TYPES:
                continue
            for inp in n.get("inputs", []):
                l = inp.get("link")
                if l is None:
                    continue
                oo = self.link_origin("", l)
                if oo is None:
                    continue
                r = self.resolve_producer("", oo[0], oo[1])
                if not r:
                    continue
                # type from the top-level link record
                ltype = next((x[5] for x in self.ui["links"] if x[0] == l), None)
                if ltype:
                    self.ae_sources.setdefault(ltype, r)
        print("AE broadcast sources by type:")
        for t, r in self.ae_sources.items():
            print(f"   {t} <- {r}")

    # ---------- widget mapping ----------
    def widget_names(self, class_type):
        """All widget (non-socket) inputs in declared order. widgets_values retains a value for
        every one of these (including those promoted/converted to sockets); a connected link just
        overrides the value later, so we never exclude any here."""
        info = self.obj.get(class_type, {}).get("input", {})
        names = []
        for sect in ("required", "optional"):
            for name, spec in info.get(sect, {}).items():
                t = spec[0] if isinstance(spec, list) and spec else spec
                if isinstance(t, list) or t in PRIMITIVE or t in WIDGET_CUSTOM:
                    names.append(name)
        return names

    def socket_inputs(self, class_type):
        """name -> type for socket (connection) inputs, in declared order."""
        info = self.obj.get(class_type, {}).get("input", {})
        out = {}
        req = set()
        for sect in ("required", "optional"):
            for name, spec in info.get(sect, {}).items():
                t = spec[0] if isinstance(spec, list) and spec else spec
                if isinstance(t, str) and t not in PRIMITIVE and t not in WIDGET_CUSTOM:
                    out[name] = t
                    if sect == "required":
                        req.add(name)
        return out, req

    def build(self):
        api = {}
        for api_id, rec in self.nodes.items():
            if api_id in self.dropped:
                continue
            node, scope, t = rec["node"], rec["scope"], rec["type"]
            inputs = {}
            wv = node.get("widgets_values")
            if t == "Power Lora Loader (rgthree)" and isinstance(wv, list):
                # rgthree serializes a list of widget dicts; the API form is a flat dict with
                # PowerLoraLoaderHeaderWidget, lora_N entries and the "Add Lora" placeholder.
                lora_n = 0
                for entry in wv:
                    if isinstance(entry, dict) and entry.get("type") == "PowerLoraLoaderHeaderWidget":
                        inputs["PowerLoraLoaderHeaderWidget"] = {"type": "PowerLoraLoaderHeaderWidget"}
                    elif isinstance(entry, dict) and "lora" in entry:
                        lora_n += 1
                        inputs[f"lora_{lora_n}"] = {"on": entry.get("on", True),
                                                    "lora": entry.get("lora", ""),
                                                    "strength": entry.get("strength", 1)}
                inputs["➕ Add Lora"] = ""
            elif isinstance(wv, dict):
                for k, v in wv.items():
                    if k not in DROP_WIDGET_KEYS:
                        inputs[k] = v
            elif isinstance(wv, list):
                # Map every widget input positionally; control_after_generate / UI extras sit past
                # the widget count and are ignored. Connected links override these values below.
                names = self.widget_names(t)
                for i, name in enumerate(names):
                    if i < len(wv):
                        inputs[name] = wv[i]
            # connected links override widget values; an unresolved link (unconnected promoted
            # widget) leaves the widget value in place rather than clearing it.
            for inp in node.get("inputs", []):
                name = inp.get("name")
                l = inp.get("link")
                if l is None:
                    continue
                oo = self.link_origin(scope, l)
                if oo is None:
                    continue
                r = self.resolve_producer(scope, oo[0], oo[1])
                if r and r[0] not in self.dropped:
                    inputs[name] = r
            # bake Anything-Everywhere into unconnected *required* matching-type sockets only
            # (optional ones, e.g. VHS_VideoCombine.vae, must stay empty — matches the export).
            sockets, required = self.socket_inputs(t)
            for sname in required:
                stype = sockets[sname]
                if sname in inputs:
                    continue
                if stype in self.ae_sources:
                    inputs[sname] = list(self.ae_sources[stype])
                    self.ae_filled.append((api_id, t, sname, stype))
            api[api_id] = {"inputs": inputs, "class_type": t,
                           "_meta": {"title": node.get("title", "") or t}}
        return api


def validate(api, fl):
    print("\n=== VALIDATION ===")
    ids = set(api.keys())
    dangling = []
    for nid, nd in api.items():
        for name, val in nd["inputs"].items():
            if isinstance(val, list) and len(val) == 2 and isinstance(val[0], str) \
                    and (isinstance(val[1], int)):
                if val[0] not in ids:
                    dangling.append((nid, name, val[0]))
    print(f"nodes: {len(api)}  dangling refs: {len(dangling)}")
    for d in dangling[:40]:
        print("   DANGLING", d)
    # missing required sockets
    missing = []
    for nid, nd in api.items():
        sockets, required = fl.socket_inputs(nd["class_type"])
        for rname in required:
            if rname not in nd["inputs"]:
                missing.append((nid, nd["class_type"], rname))
    print(f"missing required socket inputs: {len(missing)}")
    for m in missing[:60]:
        print("   MISSING", m)
    if fl.ae_filled:
        print(f"AE-filled inputs ({len(fl.ae_filled)}):")
        for a in fl.ae_filled:
            print("   ", a)
    if fl.warnings:
        print(f"widget warnings ({len(fl.warnings)}):")
        for w in fl.warnings[:40]:
            print("   ", w)
    return dangling, missing


def main():
    with open(SRC, encoding="utf-8") as f:
        ui = json.load(f)
    obj = load_object_info()
    print("object_info classes:", len(obj))
    fl = Flattener(ui, obj)
    fl.collect()
    print("concrete nodes collected:", len(fl.nodes))
    fl.compute_dropped()
    print("dropped (muted/UI):", len(fl.dropped), sorted(fl.dropped))
    fl.collect_ae()
    api = fl.build()
    validate(api, fl)
    with open(DST, "w", encoding="utf-8") as f:
        json.dump(api, f, indent=2)
    print(f"\nWROTE {DST}")
    # quick presence check
    types = {}
    for nd in api.values():
        types[nd["class_type"]] = types.get(nd["class_type"], 0) + 1
    for c in ("LoadImage", "VHS_VideoCombine", "ImpactSwitch", "LTXVAddGuide", "mxSlider"):
        print(f"   {c}: {types.get(c,0)}")


if __name__ == "__main__":
    main()
