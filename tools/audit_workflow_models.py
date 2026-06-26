#!/usr/bin/env python3
"""
Audit which model files the FlipPix workflows actually require, and diff that
against the download manifest (scripts/flippix-models.txt).

Why: ComfyUI validates every model name a workflow references against what is
actually present in its model folders, and rejects the whole prompt (BadRequest
"value_not_in_list") if any name is missing. The manifest is hand-maintained, so
it drifts out of sync with the 100+ workflows -> fresh installs fail validation.

This tool is the source of truth for "what must be downloadable". It is read-only
(reports only); use its output to extend/regenerate the manifest and to drive the
in-app pre-submit validation.

Usage:
    python tools/audit_workflow_models.py [--workflow-dir DIR] [--manifest FILE] [--json]
"""
from __future__ import annotations
import argparse
import json
import os
import sys
from pathlib import Path

# File extensions that denote a model weight (not an image/text/etc input).
MODEL_EXTS = {".safetensors", ".gguf", ".ckpt", ".pt", ".pth", ".bin", ".sft", ".onnx"}


def iter_model_strings(obj):
    """Recursively yield every string value in a parsed JSON doc that looks like
    a model filename. Works for both ComfyUI API graphs (nodeid -> {inputs}) and
    UI graphs (nodes[].widgets_values[]), since we just walk everything."""
    if isinstance(obj, dict):
        for v in obj.values():
            yield from iter_model_strings(v)
    elif isinstance(obj, list):
        for v in obj:
            yield from iter_model_strings(v)
    elif isinstance(obj, str):
        s = obj.strip()
        # Normalize separators; ComfyUI uses forward slashes for subfolders.
        ext = os.path.splitext(s)[1].lower()
        if ext in MODEL_EXTS and len(s) < 256 and "\n" not in s:
            yield s.replace("\\", "/")


def load_manifest_basenames(manifest: Path):
    """Return {basename_lower: original_path} for each manifest entry
    (Path | Size | Url)."""
    out = {}
    if not manifest.exists():
        return out
    for line in manifest.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split("|", 2)
        if len(parts) != 3:
            continue
        path = parts[0].strip().replace("\\", "/")
        out[os.path.basename(path).lower()] = path
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    repo = Path(__file__).resolve().parent.parent
    ap.add_argument("--workflow-dir", default=str(repo / "workflow"))
    ap.add_argument("--manifest", default=str(repo / "scripts" / "flippix-models.txt"))
    ap.add_argument("--json", action="store_true", help="emit machine-readable JSON")
    args = ap.parse_args()

    wf_dir = Path(args.workflow_dir)
    wf_files = sorted(wf_dir.rglob("*.json"))

    # required_basename -> {"refs": {full_ref_strings}, "workflows": {names}}
    required: dict[str, dict] = {}
    parse_errors = []
    for wf in wf_files:
        try:
            doc = json.loads(wf.read_text(encoding="utf-8"))
        except Exception as e:
            parse_errors.append((wf.name, str(e)))
            continue
        for ref in iter_model_strings(doc):
            base = os.path.basename(ref).lower()
            entry = required.setdefault(base, {"refs": set(), "workflows": set()})
            entry["refs"].add(ref)
            entry["workflows"].add(str(wf.relative_to(wf_dir)))

    manifest = load_manifest_basenames(Path(args.manifest))

    missing = {b: v for b, v in required.items() if b not in manifest}
    present = {b for b in required if b in manifest}
    unused = {b: p for b, p in manifest.items() if b not in required}

    if args.json:
        print(json.dumps({
            "workflows_scanned": len(wf_files),
            "required_models": len(required),
            "present": sorted(present),
            "missing": {b: {"refs": sorted(v["refs"]), "workflows": sorted(v["workflows"])}
                        for b, v in sorted(missing.items())},
            "unused_manifest_entries": sorted(unused.values()),
            "parse_errors": parse_errors,
        }, indent=2))
        return 0

    print(f"Workflows scanned     : {len(wf_files)}")
    print(f"Distinct models needed: {len(required)}")
    print(f"  in manifest         : {len(present)}")
    print(f"  MISSING from manifest: {len(missing)}")
    print(f"Unused manifest entries: {len(unused)}")
    if parse_errors:
        print(f"Unparseable workflows  : {len(parse_errors)}")

    if missing:
        print("\n=== MISSING (referenced by a workflow, no manifest entry => will fail validation) ===")
        for base, v in sorted(missing.items()):
            ref = sorted(v["refs"])[0]
            print(f"\n  {ref}")
            for wf in sorted(v["workflows"])[:6]:
                print(f"      used by: {wf}")
            extra = len(v["workflows"]) - 6
            if extra > 0:
                print(f"      ... +{extra} more workflow(s)")

    if unused:
        print("\n=== UNUSED manifest entries (downloaded but no workflow references) ===")
        for p in sorted(unused.values()):
            print(f"  {p}")

    if parse_errors:
        print("\n=== Unparseable workflow files ===")
        for name, err in parse_errors:
            print(f"  {name}: {err}")

    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
