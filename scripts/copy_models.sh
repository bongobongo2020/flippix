#!/usr/bin/env bash
# copy_models.sh — Copy FlipPix AI models from SRC to DST preserving folder structure.
# Uses parallel rsync jobs for maximum throughput on large files.
#
# Usage: ./copy_models.sh [JOBS]
#   JOBS: parallel copy jobs (default 4; use 8 for NVMe, 2 for spinning HDD)

set -euo pipefail

SRC="/mnt/ai-models"
DST="/mnt/storage3/ai-models"
JOBS="${1:-4}"
LOG_DIR="$(dirname "$0")"
MISSING_LOG="$LOG_DIR/missing_models.log"
COPIED_LOG="$LOG_DIR/copied_models.log"

# ─── Model file list ──────────────────────────────────────────────────────────
MODELS=(
  # Checkpoints — LTX
  "ltx-2.3-22b-dev_transformer_only_fp8_scaled.safetensors"
  "ltx-2.3-22B-distilled-1.1-Q8_0.gguf"
  "LTX-2.3-distilled-Q6_K.gguf"
  "ltx-2-19b-dev-Q8_0.gguf"
  "ltx-2-19b-distilled_Q8_0.gguf"
  "LTX-2.3-dev-Q8_0.gguf"
  "ltx-2-19b-distilled-fp8.safetensors"
  "ltx2-phr00tmerge-sfw-v5.safetensors"
  "10Eros_v1-Q8_0.gguf"
  "sulphur_dev-Q8_0.gguf"
  # Checkpoints — WAN
  "wan2.1_t2v_14B_fp8_e4m3fn.safetensors"
  "wan2.1-i2v-14b-480p-Q8_0.gguf"
  "Wan2.1-InfiniteTalk_Single_Q8.gguf"
  "Wan2_1-T2V-14B_fp8_e4m3fn_scaled_KJ.safetensors"
  "Wan2_1-T2V-14B_fp8_e4m3fn.safetensors"
  "Wan2.2-Animate-14B-Q8_0.gguf"
  "Wan2_2-Animate-14B_fp8_scaled_e4m3fn_KJ_v2.safetensors"
  "wan2.2_i2v_high_noise_14B_Q8_0.gguf"
  "wan2.2_i2v_low_noise_14B_Q8_0.gguf"
  "Wan22EnhancedNSFWSVICamera_nsfwFASTMOVEV2Q8H.gguf"
  "Wan2_1-VACE_module_14B_fp8_e4m3fn.safetensors"
  "Wan2_1-VACE_module_14B_bf16.safetensors"
  "Wan2_1-mocha-14B-preview_fp8_e4m3fn_scaled_KJ.safetensors"
  "Wan21-14B-SCAIL-preview_fp8_scaled_mixed.safetensors"
  "Wan21-14B-SCAIL-preview_comfy-Q8_0.gguf"
  "wan22RemixT2VI2V_i2vHighV30-Q8_0.gguf"
  "wan22RemixT2VI2V_i2vLowV30-Q8_0.gguf"
  "DasiwaWAN22I2V14BLightspeed_midnightflirtLow.safetensors"
  # Checkpoints — FLUX / Klein
  "flux2-dev-nvfp4-mixed.safetensors"
  "flux-2-klein-9b-fp8.safetensors"
  "flux-2-klein-9b.safetensors"
  "Flux2-Klein-9B-True-v2-Q8_0.gguf"
  "Flux2-Klein-9B-True-v2-bf16.safetensors"
  # Checkpoints — QWen / FireRed
  "Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors"
  "qwen-image-edit-2511-Q8_0.gguf"
  "Real-Qwen-Image-V2-2512-Q8_0.gguf"
  "Qwen-Rapid-NSFW-v18_Q8_0.gguf"
  "FireRed-Image-Edit-1.1-Q8_0.gguf"
  # VAE
  "LTX23_audio_vae_bf16.safetensors"
  "LTX23_video_vae_bf16.safetensors"
  "LTX2_audio_vae_bf16.safetensors"
  "LTX2_video_vae_bf16.safetensors"
  "ltx-2-3-22b-VAE.safetensors"
  "ltx-2-3-22b-audio_vae.safetensors"
  "taeltx2_3.safetensors"
  "Wan2_1_VAE_fp32.safetensors"
  "Wan2_1_VAE_bf16.safetensors"
  "wan_2.1_vae.safetensors"
  "flux2-vae.safetensors"
  "ae.safetensors"
  "z_image_turbo_bf16.safetensors"
  "ultraflux-vael.safetensors"
  "ultrafluxVAEImproved_v10.safetensors"
  "qwen_image_vae.safetensors"
  # Text encoders / CLIP
  "umt5_xxl_fp8_e4m3fn_scaled.safetensors"
  "umt5-xxl-enc-bf16.safetensors"
  "nsfw_wan_umt5-xxl_fp8_scaled.safetensors"
  "qwen_3_8b.safetensors"
  "qwen_3_4b.safetensors"
  "qwen_2.5_vl_7b_fp8_scaled.safetensors"
  "qwen-4b-zimage-heretic-q8.gguf"
  "Qwen3-4B.i1-Q5_K_S.gguf"
  "Qwen3-8B-Q8_0.gguf"
  "qwen38BFluxKlein9BTE_38b.safetensors"
  "mistral_3_small_flux2_fp4_mixed.safetensors"
  "gemma_3_12B_it.safetensors"
  "gemma_3_12B_it_fp8_e4m3fn.safetensors"
  "gemma_3_12B_it_fp8_scaled.safetensors"
  "gemma_3_12B_it_fpmixed.safetensors"
  "clip_vision_h.safetensors"
  "wav2vec2-chinese-base_fp16.safetensors"
  "ltx-2.3_text_projection_bf16.safetensors"
  "ltx-2-19b-embeddings_connector_bf16.safetensors"
  "ltx-2-3-22b-text_encoder.safetensors"
  # LoRA — LTX
  "ltx-2.3-22b-distilled-lora-384.safetensors"
  "ltx-2.3-22b-distilled-lora-1.1.safetensors"
  "ltx-2.3-22b-distilled-lora-384-1.1.safetensors"
  "ltx-2.3-22b-distilled-lora-dynamic_fro09_avg_rank_105_bf16.safetensors"
  "ltx-2.3-22b-distilled-lora-1.1_fro90_ceil72_condsafe.safetensors"
  "ltx23_edit_anything_global_rank128_v1_9000steps_adamw.safetensors"
  "ltx-2-19b-distilled-lora-384.safetensors"
  "ltx-2-19b-distilled-lora_resized_dynamic_fro09_avg_rank_175_fp8.safetensors"
  "ltx-2-19b-lora-camera-control-static.safetensors"
  "ltx-2-19b-ic-lora-detailer.safetensors"
  "LTX2-i2v-OralSuite.safetensors"
  "LTX2.3_reasoning_I2V_V3.safetensors"
  "Penile_Praxis_V4.safetensors"
  # LoRA — WAN
  "wan21-lightx2v-i2v-14b-480p-cfg-step-distill-rank64-bf16.safetensors"
  "Wan21_CausVid_14B_T2V_lora_rank32.safetensors"
  "Wan21_CausVid_14B_T2V_lora_rank32_v2.safetensors"
  "Wan14B_RealismBoost.safetensors"
  "WanAnimate_relight_lora_fp16.safetensors"
  "lightx2v_I2V_14B_480p_cfg_step_distill_rank64_bf16.safetensors"
  "lightx2v_T2V_14B_cfg_step_distill_v2_lora_rank64_bf16.safetensors"
  "wan2.2_t2v_A14b_high_noise_lora_rank64_lightx2v_4step_1217.safetensors"
  "wan2.2_t2v_A14b_low_noise_lora_rank64_lightx2v_4step_1217.safetensors"
  "wan2.2_i2v_A14b_high_noise_lora_rank64_lightx2v_4step_1022.safetensors"
  "wan2.2_i2v_A14b_low_noise_lora_rank64_lightx2v_4step_1022.safetensors"
  "Wan22_PusaV1_lora_HIGH_resized_dynamic_avg_rank_98_bf16.safetensors"
  "Wan22_PusaV1_lora_LOW_resized_dynamic_avg_rank_98_bf16.safetensors"
  "SVI_v2_PRO_Wan2.2-I2V-A14B_HIGH_lora_rank_128_fp16.safetensors"
  "SVI_v2_PRO_Wan2.2-I2V-A14B_LOW_lora_rank_128_fp16.safetensors"
  "Bouncing Breasts - XL wan 480p .safetensors"
  "wan_pov_missionary_i2v_v1.1.safetensors"
  "doggy_pov_9fingers.safetensors"
  "DaSiWa_Wan22_Low_Deepthroat_v11.safetensors"
  "DaSiWa_Wan22_High_Deepthroat_v11.safetensors"
  "23High noise-Cumshot Aesthetics.safetensors"
  "WAN-2.2-I2V-POV-Cowgirl-LOW-v1.0-fixed.safetensors"
  "WAN-2.2-I2V-POV-Cowgirl-HIGH-v1.0-fixed.safetensors"
  "wan2.2_i2v_lownoise_pov_missionary_v1.0.safetensors"
  "wan2.2_i2v_highnoise_pov_missionary_v1.0.safetensors"
  "WAN-2.2-I2V-FaceDownAssUp-LOW-v1.safetensors"
  "WAN-2.2-I2V-FaceDownAssUp-HIGH-v1.safetensors"
  "WAN-2.2-I2V-POV-Titfuck-Paizuri-LOW-v1.0.safetensors"
  "WAN-2.2-I2V-POV-Titfuck-Paizuri-HIGH-v1.0.safetensors"
  "wan22-side-deepthroat-12epoc-low-k3nk.safetensors"
  "wan22-side-deepthroat-54epoc-high-k3nk.safetensors"
  "wan_rashidajones_v1.safetensors"
  "wan_gilliananderson_v1.safetensors"
  # LoRA — FLUX / Klein
  "Flux2TurboComfyv2.safetensors"
  "klein_9B_Turbo_r128.safetensors"
  "flux2klein_unchained_v1.safetensors"
  "snofs_v1.safetensors"
  "klein_slider_anatomy.safetensors"
  "klein_9b_enhancer_v2.safetensors"
  # LoRA — ZImage
  "amateur_photography_zimage_v1.safetensors"
  "zimage_gilliananderson_v1.safetensors"
  "sridevi-zimage.safetensors"
  "jibs_Z-image_lora_v2_000000500.safetensors"
  "Jibs_zimagesafetensors.safetensors"
  "Z-Real-v1.0.safetensors"
  "ZIT_Luneva CyberHD.safetensors"
  "ZIT_Midjourney_Luneva_Cinematic_v1_r128.safetensors"
  "lenovo_z.safetensors"
  "Jibs_Z-Image_skin_lora_V1.safetensors"
  "burtonesque_ZIT_v1.safetensors"
  "god_Pussy-zimage_000008000.safetensors"
  "jibMixZIT_v20.safetensors"
  "x3n0666_x3n0666Fp16.safetensors"
  # LoRA — QWen / FireRed
  "Qwen-Image-Lightning-8steps-V2.0.safetensors"
  "mult-angles.safetensors"
  "Qwen-Image-2512-Lightning-8steps-V1.0-bf16.safetensors"
  "qwen-edit-skin_1.1_000002750.safetensors"
  "FireRed-Image-Edit-1.0-Lightning-8steps-v1.0.safetensors"
  # Upscalers
  "ltx-2.3-spatial-upscaler-x2-1.0.safetensors"
  "ltx-2.3-spatial-upscaler-x2-1.1.safetensors"
  "ltx-2.3-spatial-upscaler-x1.5-1.0.safetensors"
  "ltx-2-spatial-upscaler-x2-1.0.safetensors"
  "RealESRGAN_x2plus.pth"
  "4x_foolhardy_Remacri.safetensors"
  "4xNomos8kSCHAT-L.safetensors"
  # Detection / Pose / Segmentation
  "sam2.1_hiera_base_plus.safetensors"
  "sam2_hiera_base_plus.safetensors"
  "dw-ll_ucoco_384_bs5.torchscript.pt"
  "vitpose-l-wholebody.onnx"
  "vitpose_h_wholebody_model.onnx"
  "yolox_l.onnx"
  "yolox_l.torchscript.pt"
  "yolov10m.onnx"
  # Audio / Frame interpolation / ControlNet
  "MelBandRoformer_fp16.safetensors"
  "rife47.pth"
  "Wan21_Uni3C_controlnet_fp16.safetensors"
)

# ─── Helpers ──────────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
TOTAL=${#MODELS[@]}
COUNTER_FILE="$(mktemp)"
echo 0 > "$COUNTER_FILE"
START_TIME=$(date +%s)

mkdir -p "$DST"
: > "$MISSING_LOG"
: > "$COPIED_LOG"

fmt_size() {
  local bytes="$1"
  if   (( bytes >= 1073741824 )); then printf "%.1f GB" "$(echo "scale=1; $bytes/1073741824" | bc)"
  elif (( bytes >= 1048576    )); then printf "%.1f MB" "$(echo "scale=1; $bytes/1048576"    | bc)"
  else printf "%d KB" $(( bytes / 1024 ))
  fi
}

copy_one() {
  local filename="$1"
  local src="$2"
  local dst="$3"
  local missing_log="$4"
  local copied_log="$5"
  local counter_file="$6"
  local total="$7"

  # Atomic counter read+increment for [N/total] prefix
  local n
  (
    flock 9
    n=$(cat "$counter_file")
    echo $((n + 1)) > "$counter_file"
  ) 9>"${counter_file}.lock"
  n=$(cat "$counter_file")
  local prefix="[${n}/${total}]"

  # Find file anywhere under src (first match wins)
  local found
  found=$(find "$src" -name "$filename" -type f 2>/dev/null | head -1)

  if [[ -z "$found" ]]; then
    echo "$filename" >> "$missing_log"
    echo -e "  ${RED}MISSING${NC} $prefix $filename"
    return
  fi

  local rel="${found#"$src"/}"
  local dest_path="$dst/$rel"
  mkdir -p "$(dirname "$dest_path")"

  local size_bytes size_human
  size_bytes=$(stat -c %s "$found" 2>/dev/null || echo 0)
  size_human=$(fmt_size "$size_bytes")

  echo -e "  ${YELLOW}→${NC} $prefix $filename  ($size_human)"
  local t0; t0=$(date +%s)

  rsync -a --no-compress --inplace "$found" "$dest_path"

  local elapsed=$(( $(date +%s) - t0 ))
  echo "$rel" >> "$copied_log"
  echo -e "  ${GREEN}✓${NC} $prefix $filename  (${elapsed}s)"
}

export -f fmt_size copy_one

# ─── Run ──────────────────────────────────────────────────────────────────────
echo -e "\n${YELLOW}FlipPix model copy${NC}"
echo "  Source : $SRC"
echo "  Dest   : $DST"
echo "  Files  : $TOTAL"
echo "  Jobs   : $JOBS"
echo ""

printf '%s\n' "${MODELS[@]}" \
  | xargs -P "$JOBS" -I{} bash -c \
      'copy_one "$@"' _ {} "$SRC" "$DST" "$MISSING_LOG" "$COPIED_LOG" "$COUNTER_FILE" "$TOTAL"

# ─── Summary ──────────────────────────────────────────────────────────────────
ELAPSED=$(( $(date +%s) - START_TIME ))
COPIED_COUNT=$(wc -l < "$COPIED_LOG")
MISSING_COUNT=$(wc -l < "$MISSING_LOG")
rm -f "$COUNTER_FILE" "${COUNTER_FILE}.lock"

echo ""
echo "────────────────────────────────"
printf "  Copied  : %d / %d\n" "$COPIED_COUNT" "$TOTAL"
printf "  Missing : %d\n"      "$MISSING_COUNT"
printf "  Time    : %dm %ds\n" $((ELAPSED/60)) $((ELAPSED%60))
echo "────────────────────────────────"

if [[ "$MISSING_COUNT" -gt 0 ]]; then
  echo -e "\n${RED}Missing files logged to:${NC} $MISSING_LOG"
  cat "$MISSING_LOG"
fi

# Special case: rife49 is a directory, not a file
if find "$SRC" -maxdepth 5 -type d -name "rife49" 2>/dev/null | grep -q .; then
  RIFE_SRC=$(find "$SRC" -maxdepth 5 -type d -name "rife49" | head -1)
  RIFE_REL="${RIFE_SRC#"$SRC"/}"
  echo -e "\n${YELLOW}Copying rife49 directory...${NC}"
  rsync -a --no-compress "$RIFE_SRC/" "$DST/$RIFE_REL/"
  echo -e "  ${GREEN}OK${NC}  $RIFE_REL/"
fi
