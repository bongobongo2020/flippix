#!/usr/bin/env bash
# Downloads the LoRAs the 🥷 Martial tab's default stack uses (the MiniMax H3 martial workflow, 02_MiniMax_H3martial)
# into the ComfyUI server's loras/H3 folder. Run it ON the ComfyUI server, e.g.
#   ssh x2@ubuntu-server 'bash -s' < scripts/download-martial-models.sh
# Already-present files are skipped. Pass a folder to override where they go:
#   bash download-martial-models.sh /mnt/storage1/ai-models/loras/H3
set -euo pipefail

TARGET="${1:-}"
if [ -z "$TARGET" ]; then
  # The first loras folder that already has an H3 subfolder (the server lists LoRAs as H3/…).
  for d in /mnt/storage1/ai-models/loras /home/x2/sep23/ComfyUI-Easy-Install/ComfyUI/models/loras; do
    if [ -d "$d/H3" ]; then TARGET="$d/H3"; break; fi
  done
fi
[ -n "$TARGET" ] || { echo "No loras/H3 folder found — pass the target folder as the first argument." >&2; exit 1; }
mkdir -p "$TARGET"
echo "Downloading into $TARGET"

fetch() {
  local url="$1" file="$TARGET/$(basename "$1")"
  if [ -s "$file" ]; then echo "✓ $(basename "$file") already there"; return; fi
  echo "↓ $(basename "$file")"
  wget -q --show-progress -c -O "$file.part" "$url" && mv "$file.part" "$file"
}

# wushu spatial physics (workflow: clean_2500 @ 0.3 — the published build is clean_3000)
fetch "https://huggingface.co/Jojocodex/minimax-h3-spatial-physics-lora/resolve/main/wushu_spatial_physics_clean_3000_pruned.safetensors"
# wushu_action v7, fl2va, 2000 steps (workflow: wushu_action_fl2va_v7_2000step_no_adaln @ 0.4)
fetch "https://huggingface.co/Jojocodex/wushu-action-v7-minimax-h3-fl2va-ref2va-lora/resolve/main/wushu_action_v7_fl2va_aitoolkit_adaln_full-int8convrot_bf16te_2000step.safetensors"
# wushu_action v7 for ref2va models (optional alternative row)
fetch "https://huggingface.co/Jojocodex/wushu-action-v7-minimax-h3-fl2va-ref2va-lora/resolve/main/wushu_h3_r2v_v7_2000.safetensors"
# camera motion (workflow: camera_motion_h3_lora_v1_3000_pruned @ 0.3 — same file)
fetch "https://huggingface.co/Jojocodex/minimax-h3-Camera-Motion-lora/resolve/main/camera_motion_h3_lora_v1_3000_pruned.safetensors"

echo "Done. In FlipPix: 🥷 Martial → ⚙ → ⟳ Server lists."
