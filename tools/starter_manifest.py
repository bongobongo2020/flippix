"""Checks the starter ComfyUI manifest against what the phone actually submits.

Builds every phone graph with FlipPix.Remote's own recipes (tools/mobile-needs), then:
  * every model file a graph loads must be listed in packaging/comfyui-starter/starter.json;
  * with --server, every node class must be registered there, and each custom one must come from
    a pack in the manifest (run against the starter itself, so module names are its folder names).

    python tools/starter_manifest.py                          # models only
    python tools/starter_manifest.py --server http://127.0.0.1:8199

Exit code 1 if anything is missing.
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(REPO, "packaging", "comfyui-starter", "starter.json")
MODEL_EXT = (".safetensors", ".gguf", ".pth", ".pt", ".ckpt", ".bin", ".onnx", ".sft")


def build_graphs(out_dir):
    subprocess.run(["dotnet", "run", "--project", os.path.join(REPO, "tools", "mobile-needs"), "--", out_dir],
                   check=True, stdout=subprocess.DEVNULL)
    graphs = {}
    for name in sorted(os.listdir(out_dir)):
        with open(os.path.join(out_dir, name), encoding="utf-8") as f:
            graphs[name[:-5]] = json.load(f)
    return graphs


def model_refs(graph):
    """(filename, where) for every model a graph loads, including rgthree's switched-on LoRA rows."""
    for nid, node in graph.items():
        for key, value in node["inputs"].items():
            if isinstance(value, str) and value.lower().endswith(MODEL_EXT):
                yield value, f"{node['class_type']}.{key}"
            elif isinstance(value, dict) and value.get("on") and isinstance(value.get("lora"), str):
                yield value["lora"], f"{node['class_type']}.{key}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", help="a running starter ComfyUI to check node classes against")
    args = ap.parse_args()

    with open(MANIFEST, encoding="utf-8") as f:
        manifest = json.load(f)
    listed = {m["path"].split("/", 1)[1].replace("\\", "/") for m in manifest["models"]}
    problems = 0

    with tempfile.TemporaryDirectory() as tmp:
        graphs = build_graphs(tmp)

    for name, graph in graphs.items():
        for filename, where in model_refs(graph):
            if filename.replace("\\", "/") not in listed:
                print(f"MISSING MODEL  {name}: {filename}  ({where})")
                problems += 1

    if args.server:
        with urllib.request.urlopen(args.server.rstrip("/") + "/object_info", timeout=120) as r:
            info = json.load(r)
        packs = {p["dir"] for p in manifest["packs"]}
        for name, graph in graphs.items():
            for cls in sorted({n["class_type"] for n in graph.values()}):
                if cls not in info:
                    print(f"MISSING NODE   {name}: {cls}")
                    problems += 1
                    continue
                module = info[cls].get("python_module", "")
                if module.startswith("custom_nodes.") and module.split(".", 1)[1] not in packs:
                    print(f"UNLISTED PACK  {name}: {cls} comes from {module}")
                    problems += 1

    print(f"{len(graphs)} phone graphs checked, {problems} problem(s)")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
