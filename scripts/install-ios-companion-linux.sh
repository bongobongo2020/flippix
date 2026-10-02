#!/usr/bin/env bash
# =============================================================================================
#  FlipPix iOS Companion - Ubuntu installer
#
#  Sets this PC up to serve the FlipPix iPad / iPhone app, the Linux counterpart of
#  FlipPix-iOS-Companion-Setup.exe:
#
#    * ComfyUI (pinned release) in its own Python, with exactly the custom-node packs the iPad's
#      two graphs need (flippix-custom-nodes-ios.txt)
#    * the models those graphs load, plus the content filter (flippix-models-ios.txt, ~60 GB)
#    * the writing assistant: llama-server (CUDA) running Qwen2.5-VL 7B
#    * the companion itself, as a systemd user service that starts with the PC and keeps
#      ComfyUI and the writing assistant running
#
#  Everything goes in one folder (default ~/FlipPix). Running it again carries on where it left
#  off: finished downloads are kept and half-finished ones resume.
#
#  Usually run from the one-file FlipPix-iOS-Companion-Setup-Linux.sh, which unpacks this beside
#  app/ (the companion build), lists/ and workflow/ (scripts/make-ios-companion-linux.ps1).
#
#  Usage:  bash install-ios-companion-linux.sh [--dir DIR] [--yes] [--no-service] [--no-linger]
# =============================================================================================
set -Eeuo pipefail

# ---------------------------------------------------------------------------------------------
# pins - bump together with the Windows companion (setup-comfyui-fresh / setup-llm.ps1 /
# packaging/comfyui-starter/starter.json) after testing
# ---------------------------------------------------------------------------------------------
COMFY_REPO="https://github.com/comfyanonymous/ComfyUI"
COMFY_TAG="v0.37.0"
PYTHON_VERSION="3.12"
# What the v0.37.0 Windows portable build ships (release/comfyui-starter/constraints.txt).
# CUDA 13.0 wheels need an NVIDIA driver >= 580; older drivers get the newest CUDA 12.8 build.
TORCH_PINS="torch==2.13.0 torchvision==0.28.0 torchaudio==2.11.0"
TORCH_INDEX_CU130="https://download.pytorch.org/whl/cu130"
TORCH_INDEX_CU128="https://download.pytorch.org/whl/cu128"
TORCH_MIN_DRIVER_CU130=580

LLAMA_BUILD="b11321"
LLAMA_CUDA="12.8"
LLAMA_BASE="https://github.com/ggml-org/llama.cpp/releases/download/${LLAMA_BUILD}"
LLM_HF="https://huggingface.co/ggml-org/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main"
LLM_MODEL="Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf"
LLM_MMPROJ="mmproj-Qwen2.5-VL-7B-Instruct-Q8_0.gguf"
LLM_ALIAS="qwen2.5-vl-7b-instruct-q4_k_m"   # the id FlipPix sends; matches setup-llm.ps1
LLM_PORT=8080
COMFY_PORT=8188
REMOTE_PORT=47800      # FlipPix.Remote.Contracts RemoteApi.DefaultPort
DISCOVERY_PORT=47801   # RemoteApi.DiscoveryPort (UDP)

PRODUCT="FlipPix iOS Companion"
SERVICE="flippix-companion"

# ---------------------------------------------------------------------------------------------
# options
# ---------------------------------------------------------------------------------------------
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$HOME/FlipPix"
ASSUME_YES=0
WANT_SERVICE=1
WANT_LINGER=1

usage() {
    cat <<EOF
$PRODUCT - Ubuntu installer

  --dir DIR      install into DIR (default: ~/FlipPix)
  --yes          accept the licenses and every default without asking
  --no-service   don't install the background service (run: flippix-companion serve)
  --no-linger    start the companion when you sign in, not when the PC boots
  -h, --help     this help
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --dir) ROOT="$2"; shift 2 ;;
        --dir=*) ROOT="${1#*=}"; shift ;;
        -y|--yes) ASSUME_YES=1; shift ;;
        --no-service) WANT_SERVICE=0; shift ;;
        --no-linger) WANT_LINGER=0; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1"; usage; exit 2 ;;
    esac
done
ROOT="$(mkdir -p "$ROOT" && cd "$ROOT" && pwd)"

# Where everything lives inside ROOT
VENV="$ROOT/venv"
PY="$VENV/bin/python"
COMFY="$ROOT/ComfyUI"
MODELS="$ROOT/models"
LLM_DIR="$ROOT/LLM"
APP_DIR="$ROOT/App"
TOOLS="$ROOT/tools"
UV="$TOOLS/uv/uv"
CONSTRAINTS="$ROOT/constraints.txt"

CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/FlipPix"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/FlipPix"
LOG_DIR="$DATA_DIR/setup-logs"
mkdir -p "$CONFIG_DIR" "$DATA_DIR/companion" "$LOG_DIR"
LOG_FILE="$LOG_DIR/install-$(date +%Y%m%d-%H%M%S).log"

# The package this script came in (or a repo checkout: scripts/ beside workflow/)
LISTS="$HERE/lists";      [ -d "$LISTS" ] || LISTS="$HERE"
WORKFLOWS="$HERE/workflow"; [ -d "$WORKFLOWS" ] || WORKFLOWS="$HERE/../workflow"
APP_SRC="$HERE/app"
NODE_LIST="$LISTS/flippix-custom-nodes-ios.txt"
MODEL_LIST="$LISTS/flippix-models-ios.txt"
COMPANION_WORKFLOWS=("image/krea/krea2RealismV1_krea2RealismV1WF.json" "video/h3-minimax/h3-minimax-i2v.json")

# ---------------------------------------------------------------------------------------------
# output helpers (everything also goes to the log)
# ---------------------------------------------------------------------------------------------
exec > >(tee -a "$LOG_FILE") 2>&1

if [ -t 1 ]; then
    C_B=$'\e[1m'; C_C=$'\e[36m'; C_G=$'\e[32m'; C_Y=$'\e[33m'; C_R=$'\e[31m'; C_M=$'\e[35m'; C_0=$'\e[0m'
else
    C_B=""; C_C=""; C_G=""; C_Y=""; C_R=""; C_M=""; C_0=""
fi
STEP=0
STEPS=9
step() { STEP=$((STEP + 1)); echo; echo "${C_C}${C_B}==> Step $STEP of $STEPS: $*${C_0}"; }
ok()   { echo "  ${C_G}[ok]${C_0} $*"; }
warn() { echo "  ${C_Y}[!]${C_0} $*"; }
fail() { echo; echo "  ${C_R}[x] $*${C_0}"; exit 1; }
info() { echo "  $*"; }

on_error() {
    local code=$?
    echo
    echo "${C_R}${C_B}Setup stopped (line $1, exit $code).${C_0}"
    echo "Run it again to carry on - finished downloads are kept."
    echo "The full log is $LOG_FILE"
    exit "$code"
}
trap 'on_error $LINENO' ERR

ask_yes() {
    # ask_yes "Question" [default y|n] -> 0 for yes
    local q="$1" def="${2:-y}" a
    if [ "$ASSUME_YES" = 1 ]; then [ "$def" = y ]; return; fi
    local hint="[Y/n]"; [ "$def" = n ] && hint="[y/N]"
    read -r -p "  $q $hint " a </dev/tty || a=""
    a="${a:-$def}"
    [[ "$a" =~ ^[Yy] ]]
}

SUDO_READY=0
need_sudo() {
    # Ask for the password once, the first time something needs it.
    [ "$SUDO_READY" = 1 ] && return 0
    if [ "$(id -u)" = 0 ]; then SUDO_READY=1; return 0; fi
    command -v sudo >/dev/null || fail "This step needs administrator rights, but sudo isn't installed."
    info "Administrator rights are needed for: $1"
    # The password prompt reads the terminal (stdout is piped through tee); with no terminal
    # (an unattended --yes run) only a password-less sudo can work.
    { sudo -v </dev/tty; } 2>/dev/null || sudo -n true 2>/dev/null || fail "sudo was refused."
    SUDO_READY=1
}
as_root() { if [ "$(id -u)" = 0 ]; then "$@"; else sudo "$@"; fi; }

# size string '~4.9 GB' -> bytes
to_bytes() {
    awk -v s="$1" 'BEGIN { if (match(s, /[0-9.]+ *[KMGT]?B/)) { t = substr(s, RSTART, RLENGTH); n = t + 0;
        u = t; gsub(/[0-9. ]/, "", u); m = (u=="TB")?1024^4:(u=="GB")?1024^3:(u=="MB")?1024^2:(u=="KB")?1024:1;
        printf "%.0f", n * m } else print 0 }'
}
human() { awk -v b="$1" 'BEGIN { if (b >= 1024^3) printf "%.1f GB", b/1024^3; else printf "%.0f MB", b/1024^2 }'; }

# download URL DEST [label]: resumes DEST.part, skips a finished DEST
download() {
    local url="$1" dest="$2" label="${3:-$(basename "$2")}"
    if [ -s "$dest" ]; then ok "$label (already downloaded)"; return 0; fi
    mkdir -p "$(dirname "$dest")"
    local auth=()
    if [ -n "${HF_TOKEN:-}" ] && [[ "$url" == https://huggingface.co/* ]]; then auth=(-H "Authorization: Bearer $HF_TOKEN"); fi
    local try rc remote have
    for try in 1 2 3 4 5; do
        info "$label"
        rc=0
        curl -fL --retry 3 --retry-delay 5 --connect-timeout 30 -C - "${auth[@]}" \
            --progress-bar -o "$dest.part" "$url" || rc=$?
        if [ "$rc" = 0 ]; then mv -f "$dest.part" "$dest"; return 0; fi
        # A refused resume (HTTP 416) usually means the .part is already whole: compare with the server.
        if [ -f "$dest.part" ]; then
            remote="$(curl -fsIL "${auth[@]}" "$url" 2>/dev/null | awk 'tolower($1) == "content-length:" { v = $2 } END { gsub(/\r/, "", v); print v }')"
            have="$(stat -c %s "$dest.part")"
            if [ -n "$remote" ] && [ "$have" = "$remote" ]; then mv -f "$dest.part" "$dest"; return 0; fi
            if [ -n "$remote" ] && [ "$have" -gt "$remote" ]; then rm -f "$dest.part"; fi
        fi
        warn "download interrupted (curl exit $rc); retrying ($try/5)..."
        sleep 5
    done
    fail "Couldn't download $label from $url"
}

# ---------------------------------------------------------------------------------------------
echo "${C_M}${C_B}"
echo "  ============================================================"
echo "     $PRODUCT - Ubuntu setup"
echo "  ============================================================${C_0}"
echo "  Install folder: $ROOT"
echo "  Log:            $LOG_FILE"

# =============================================================================================
step "Checking this PC"
# =============================================================================================
[ "$(id -u)" != 0 ] || fail "Run this as your normal user, not root (it asks for sudo when it needs it)."
[ "$(uname -m)" = x86_64 ] || fail "This installer is for 64-bit Intel/AMD PCs (found $(uname -m))."
. /etc/os-release 2>/dev/null || true
case "${ID:-}:${ID_LIKE:-}" in
    ubuntu:*|*:*ubuntu*|debian:*|*:*debian*) ok "${PRETTY_NAME:-Linux}" ;;
    *) warn "${PRETTY_NAME:-This Linux} isn't Ubuntu; carrying on, but only Ubuntu 22.04 / 24.04 are tested." ;;
esac
for f in "$NODE_LIST" "$MODEL_LIST"; do [ -f "$f" ] || fail "Missing $f - run the installer from its package."; done
[ -x "$APP_SRC/flippix-companion" ] || [ -f "$APP_SRC/flippix-companion" ] \
    || fail "The companion app isn't in this package ($APP_SRC). Use FlipPix-iOS-Companion-Setup-Linux.sh."

# NVIDIA GPU + driver
if ! command -v nvidia-smi >/dev/null 2>&1 || ! nvidia-smi >/dev/null 2>&1; then
    if command -v lspci >/dev/null 2>&1 && lspci | grep -qi nvidia; then
        warn "An NVIDIA graphics card is here, but its driver isn't installed (or isn't loaded yet)."
        if ask_yes "Install the recommended NVIDIA driver now? (needs a restart afterwards)"; then
            need_sudo "installing the NVIDIA driver"
            as_root apt-get update -y
            as_root apt-get install -y ubuntu-drivers-common
            as_root ubuntu-drivers install
            echo
            echo "${C_B}The driver is installed. Restart the PC, then run this installer again.${C_0}"
            exit 0
        fi
        fail "ComfyUI needs the NVIDIA driver. Install it (sudo ubuntu-drivers install), restart, and run this again."
    fi
    fail "No NVIDIA graphics card found. Krea 2 and MiniMax H3 need an NVIDIA RTX card."
fi
GPU_NAME="$(nvidia-smi --query-gpu=name --format=csv,noheader | head -1)"
GPU_VRAM_MB="$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits | head -1 | tr -dc 0-9)"
DRIVER="$(nvidia-smi --query-gpu=driver_version --format=csv,noheader | head -1)"
DRIVER_MAJOR="${DRIVER%%.*}"
ok "$GPU_NAME, $((GPU_VRAM_MB / 1024)) GB, driver $DRIVER"
if [ "${GPU_VRAM_MB:-0}" -lt 15000 ]; then
    warn "This card has under 16 GB of video memory; MiniMax H3 video may run out of memory."
fi

# Disk space: what's still to download, plus ~15 GB for Python, torch and the packs
need=0
while IFS='|' read -r path size url; do
    path="$(echo "$path" | xargs)"; [ -z "$path" ] && continue
    [ -s "$MODELS/$path" ] || need=$((need + $(to_bytes "$size")))
done < <(tr -d '\r' < "$MODEL_LIST" | grep -v '^\s*#' | grep '|')
[ -s "$LLM_DIR/models/$LLM_MODEL" ] || need=$((need + 5600000000))
[ -d "$VENV" ] || need=$((need + 15 * 1024 * 1024 * 1024))
free=$(( $(df -Pk "$ROOT" | awk 'NR==2 {print $4}') * 1024 ))
if [ "$free" -lt "$need" ]; then
    warn "This needs about $(human "$need") free in $ROOT, but there's only $(human "$free")."
    ask_yes "Carry on anyway?" n || fail "Free up some space (or choose another folder with --dir) and run this again."
else
    ok "Disk: $(human "$need") needed, $(human "$free") free"
fi

# =============================================================================================
step "License agreement"
# =============================================================================================
cat <<'EOF'

  FlipPix iOS Companion does not include any AI models. Setup downloads each one from its
  publisher onto this PC, and you may use it only under its own license.

  KREA 2 (pictures): Krea 2 Community License Agreement
    - Commercial use only while your company's yearly revenue is under US$1,000,000.
    - Content filters are required. The companion checks every picture and video, and every
      photo sent from the iPad.
    - https://krea.ai/krea-2-licensing

  MINIMAX H3 (video): MiniMax H3 Community License Agreement
    - NOT licensed for use in the United States, the European Union, the United Kingdom or
      South Korea.
    - You must follow its Acceptable Use Policy: nothing illegal, harmful, deceptive or
      infringing.
    - https://huggingface.co/MiniMaxAI/MiniMax-H3/blob/main/LICENSE

  Apache License 2.0: Qwen2.5-VL 7B (writing assistant), the Wan 2.1 VAE, the H3 turbo LoRA and
  latent upscaler, and the content filter (Falconsai nsfw_image_detection).

  ComfyUI (GPL-3.0), llama.cpp (MIT) and the ComfyUI custom nodes are downloaded from their own
  projects under their own licenses.

EOF
LICENSE_STAMP="$ROOT/.license-accepted"
if [ -f "$LICENSE_STAMP" ]; then
    ok "accepted on $(cat "$LICENSE_STAMP")"
elif [ "$ASSUME_YES" = 1 ]; then
    date -Iseconds > "$LICENSE_STAMP"; ok "accepted (--yes)"
else
    read -r -p "  Type ${C_B}yes${C_0} to accept the license of every component above: " a </dev/tty || a=""
    [ "$a" = yes ] || fail "Setup needs the licenses accepted. Nothing was installed."
    date -Iseconds > "$LICENSE_STAMP"; ok "accepted"
fi

# =============================================================================================
step "System packages"
# =============================================================================================
PKGS=(git curl ca-certificates ffmpeg build-essential libgl1 pciutils)
missing=()
for p in "${PKGS[@]}"; do dpkg -s "$p" >/dev/null 2>&1 || missing+=("$p"); done
if [ ${#missing[@]} -gt 0 ]; then
    need_sudo "installing ${missing[*]}"
    as_root apt-get update -y
    DEBIAN_FRONTEND=noninteractive as_root apt-get install -y "${missing[@]}"
fi
ok "git, curl, ffmpeg and build tools"

# =============================================================================================
step "Python $PYTHON_VERSION and ComfyUI $COMFY_TAG"
# =============================================================================================
export UV_PYTHON_INSTALL_DIR="$TOOLS/python"
export UV_CACHE_DIR="$TOOLS/uv-cache"
export UV_LINK_MODE=copy
if [ ! -x "$UV" ]; then
    info "Installing uv (Python installer)..."
    curl -LsSf https://astral.sh/uv/install.sh | env UV_INSTALL_DIR="$TOOLS/uv" UV_NO_MODIFY_PATH=1 sh >/dev/null
fi
ok "uv $("$UV" --version | awk '{print $2}')"

if [ ! -x "$PY" ]; then
    "$UV" venv --seed --python "$PYTHON_VERSION" "$VENV"
fi
ok "Python $("$PY" -c 'import sys; print(sys.version.split()[0])') in $VENV"
pipi() { "$UV" pip install --python "$PY" "$@"; }

if [ ! -f "$COMFY/main.py" ]; then
    rm -rf "$COMFY"
    git clone --quiet --depth 1 --branch "$COMFY_TAG" "$COMFY_REPO" "$COMFY"
fi
ok "ComfyUI $(git -C "$COMFY" describe --tags 2>/dev/null || echo "$COMFY_TAG")"

if [ "${DRIVER_MAJOR:-0}" -ge "$TORCH_MIN_DRIVER_CU130" ]; then
    TORCH_INDEX="$TORCH_INDEX_CU130"; TORCH_WANT="$TORCH_PINS"; TORCH_LABEL="CUDA 13.0"
else
    TORCH_INDEX="$TORCH_INDEX_CU128"; TORCH_WANT="torch torchvision torchaudio"; TORCH_LABEL="CUDA 12.8"
    warn "Driver $DRIVER is older than $TORCH_MIN_DRIVER_CU130, so using PyTorch for CUDA 12.8."
    info "  (Updating the NVIDIA driver to $TORCH_MIN_DRIVER_CU130+ and running this again gets the tested build.)"
fi
if ! "$PY" -c 'import torch, sys; sys.exit(0 if torch.version.cuda else 1)' >/dev/null 2>&1; then
    info "Installing PyTorch ($TORCH_LABEL) - about 3 GB..."
    # shellcheck disable=SC2086
    pipi $TORCH_WANT --index-url "$TORCH_INDEX"
fi
# Hold torch where it is: a pack's requirements must never swap it for another build.
"$UV" pip freeze --python "$PY" | grep -E '^(torch|torchvision|torchaudio)==' > "$CONSTRAINTS"
if ! "$PY" -c 'import torch; assert torch.cuda.is_available(), "CUDA not available"; print("  [ok] PyTorch", torch.__version__, "on", torch.cuda.get_device_name(0))'; then
    # FLIPPIX_ALLOW_NO_CUDA=1: testing the installer on a machine without the card (CI, WSL).
    [ "${FLIPPIX_ALLOW_NO_CUDA:-0}" = 1 ] || fail "PyTorch can't see the graphics card. Check that nvidia-smi works, restart, and run this again."
    warn "PyTorch can't see a graphics card; carrying on (FLIPPIX_ALLOW_NO_CUDA=1)."
fi

info "Installing ComfyUI's requirements..."
pipi -r "$COMFY/requirements.txt" -c "$CONSTRAINTS" --quiet
ok "ComfyUI requirements"

# =============================================================================================
step "Custom nodes"
# =============================================================================================
CUSTOM="$COMFY/custom_nodes"
mkdir -p "$CUSTOM"
mapfile -t NODE_URLS < <(tr -d '\r' < "$NODE_LIST" | sed -e 's/#.*//' | awk 'NF {print $1}')
i=0
for url in "${NODE_URLS[@]}"; do
    i=$((i + 1))
    name="$(basename "$url" .git)"
    dest="$CUSTOM/$name"
    if [ ! -d "$dest/.git" ]; then
        rm -rf "$dest"
        git clone --quiet --depth 1 "$url" "$dest" || { warn "[$i/${#NODE_URLS[@]}] couldn't clone $name"; continue; }
    fi
    if [ -f "$dest/requirements.txt" ]; then
        extra=()
        # nvidia-vfx (RTX Video Super Resolution) is only on NVIDIA's index.
        [ "$name" = Nvidia_RTX_Nodes_ComfyUI ] && extra=(--extra-index-url https://pypi.nvidia.com --index-strategy unsafe-best-match)
        if ! pipi -r "$dest/requirements.txt" -c "$CONSTRAINTS" "${extra[@]}" --quiet; then
            warn "[$i/${#NODE_URLS[@]}] $name: some requirements didn't install (carrying on)"
            continue
        fi
    fi
    ok "[$i/${#NODE_URLS[@]}] $name"
done

# Anything the two graphs still need, found by ComfyUI-Manager's cm-cli from the graphs themselves.
CMCLI="$CUSTOM/ComfyUI-Manager/cm-cli.py"
mkdir -p "$COMFY/user/default/workflows/FlipPix"
for wf in "${COMPANION_WORKFLOWS[@]}"; do
    src="$WORKFLOWS/$wf"
    [ -f "$src" ] || { warn "workflow $wf isn't in the package; skipping its scan"; continue; }
    mkdir -p "$COMFY/user/default/workflows/FlipPix/$(dirname "$wf")"
    cp -f "$src" "$COMFY/user/default/workflows/FlipPix/$wf"
    if [ -f "$CMCLI" ]; then
        # deps-in-workflow writes the file only when something is missing; install-deps takes it as
        # a plain argument (current ComfyUI-Manager has no --deps option).
        deps_dir="$(mktemp -d)"; deps="$deps_dir/deps.json"
        if ! (cd "$COMFY" && COMFYUI_PATH="$COMFY" "$PY" -s "$CMCLI" deps-in-workflow --workflow "$src" --output "$deps" >/dev/null 2>&1); then
            warn "the missing-node scan of $(basename "$wf") didn't finish (carrying on)"
        elif [ ! -s "$deps" ]; then
            ok "$(basename "$wf"): no missing nodes"
        elif (cd "$COMFY" && COMFYUI_PATH="$COMFY" "$PY" -s "$CMCLI" install-deps "$deps" >/dev/null 2>&1); then
            ok "$(basename "$wf"): installed the missing nodes it found"
        else
            warn "$(basename "$wf"): couldn't install some missing nodes (carrying on)"
        fi
        rm -rf "$deps_dir"
    fi
done
# install-deps may have pip-installed packs' requirements; make sure torch is still the one we chose.
if ! "$UV" pip freeze --python "$PY" | grep -E '^(torch|torchvision|torchaudio)==' | cmp -s - "$CONSTRAINTS"; then
    warn "a pack replaced PyTorch; putting it back"
    pipi -r "$CONSTRAINTS" --index-url "$TORCH_INDEX" \
        --reinstall-package torch --reinstall-package torchvision --reinstall-package torchaudio
fi

# =============================================================================================
step "Models (Krea 2, MiniMax H3, content filter)"
# =============================================================================================
mkdir -p "$MODELS"
SQ="'"
MODELS_YAML="${MODELS//$SQ/$SQ$SQ}"   # a single-quoted YAML scalar: double any '
# Point ComfyUI at the models folder (same layout as the Windows installer's extra_model_paths.yaml).
cat > "$COMFY/extra_model_paths.yaml" <<EOF
flippix:
    base_path: '$MODELS_YAML'
    checkpoints: checkpoints
    clip: clip
    clip_vision: clip_vision
    controlnet: controlnet
    diffusion_models: diffusion_models
    unet: unet
    loras: loras
    vae: vae
    text_encoders: text_encoders
    upscale_models: upscale_models
    latent_upscale_models: latent_upscale_models
EOF
ok "ComfyUI reads models from $MODELS"
warn "Large download (~60 GB); if it's interrupted, run this again and it resumes."
mapfile -t MODEL_LINES < <(tr -d '\r' < "$MODEL_LIST" | grep -v '^\s*#' | grep '|')
i=0
for line in "${MODEL_LINES[@]}"; do
    i=$((i + 1))
    IFS='|' read -r path size url <<< "$line"
    path="$(echo "$path" | xargs)"; size="$(echo "$size" | xargs)"; url="$(echo "$url" | xargs)"
    download "$url" "$MODELS/$path" "[$i/${#MODEL_LINES[@]}] $path ($size)"
done
FILTER_MODEL="$MODELS/filter/nsfw_image_detection_uint8.onnx"
[ -s "$FILTER_MODEL" ] || fail "The content filter didn't download; the companion won't serve the iPad without it."

# =============================================================================================
step "Writing assistant (llama-server + Qwen2.5-VL 7B)"
# =============================================================================================
mkdir -p "$LLM_DIR/bin" "$LLM_DIR/models" "$LLM_DIR/downloads"
STAMP="$LLM_DIR/bin/.build"
if [ ! -x "$LLM_DIR/bin/llama-server" ] || [ "$(cat "$STAMP" 2>/dev/null)" != "$LLAMA_BUILD-cuda$LLAMA_CUDA" ]; then
    for f in "llama-$LLAMA_BUILD-bin-ubuntu-cuda-$LLAMA_CUDA-x64.tar.gz" "cudart-llama-$LLAMA_BUILD-bin-ubuntu-cuda-$LLAMA_CUDA-x64.tar.gz"; do
        download "$LLAMA_BASE/$f" "$LLM_DIR/downloads/$f" "$f"
        tar -xzf "$LLM_DIR/downloads/$f" -C "$LLM_DIR/bin" --strip-components=1
    done
    [ -x "$LLM_DIR/bin/llama-server" ] || fail "llama-server wasn't in the llama.cpp download."
    echo "$LLAMA_BUILD-cuda$LLAMA_CUDA" > "$STAMP"
    rm -rf "$LLM_DIR/downloads"
fi
ok "llama-server $LLAMA_BUILD (CUDA $LLAMA_CUDA)"
download "$LLM_HF/$LLM_MODEL" "$LLM_DIR/models/$LLM_MODEL" "Qwen2.5-VL 7B (~4.7 GB)"
download "$LLM_HF/$LLM_MMPROJ" "$LLM_DIR/models/$LLM_MMPROJ" "Qwen2.5-VL vision projector (~850 MB)"

# -ngl 99 puts every layer on the GPU (~6 GB of VRAM with the projector and an 8k context).
cat > "$LLM_DIR/start-llm.sh" <<EOF
#!/usr/bin/env bash
# FlipPix writing assistant - Qwen2.5-VL 7B on llama-server. Written by install-ios-companion-linux.sh.
cd "\$(dirname "\$0")"
export LD_LIBRARY_PATH="\$PWD/bin\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}"
exec bin/llama-server -m "models/$LLM_MODEL" --mmproj "models/$LLM_MMPROJ" --alias $LLM_ALIAS \\
    --host 127.0.0.1 --port $LLM_PORT -ngl 99 -c 8192
EOF
chmod +x "$LLM_DIR/start-llm.sh"
ok "start script: $LLM_DIR/start-llm.sh"

# =============================================================================================
step "The companion"
# =============================================================================================
# Stop a running copy so its binary can be replaced.
if systemctl --user is-active --quiet "$SERVICE" 2>/dev/null; then systemctl --user stop "$SERVICE" || true; fi
mkdir -p "$APP_DIR"
cp -f "$APP_SRC/flippix-companion" "$APP_DIR/flippix-companion"
chmod +x "$APP_DIR/flippix-companion"
for f in THIRD_PARTY_LICENSES.md NOTICE.txt; do
    if [ -f "$APP_SRC/$f" ]; then cp -f "$APP_SRC/$f" "$APP_DIR/"; fi
done
ok "installed $APP_DIR/flippix-companion"

# FlipPix settings (~/.config/FlipPix/settings.json): where ComfyUI is and the writing assistant,
# merged into whatever is there, as setup-comfyui-fresh.ps1 and setup-llm.ps1 do on Windows.
FP_SETTINGS="$CONFIG_DIR/settings.json" FP_COMFY="$COMFY" FP_LLM_URL="http://127.0.0.1:$LLM_PORT" \
FP_LLM_MODEL="$LLM_ALIAS" FP_COMFY_URL="http://127.0.0.1:$COMFY_PORT" "$PY" - <<'PYEOF'
import json, os
path = os.environ["FP_SETTINGS"]
try:
    with open(path, encoding="utf-8-sig") as f: s = json.load(f)
except Exception:
    s = {}
comfy = os.environ["FP_COMFY"]
s["ComfyUIFolderPath"] = comfy
s["OutputFolderPath"] = os.path.join(comfy, "output")
if not str(s.get("BaseUrl") or "").strip():
    s["BaseUrl"] = os.environ["FP_COMFY_URL"]
url, model = os.environ["FP_LLM_URL"], os.environ["FP_LLM_MODEL"]
lm = s.get("LMStudioSettings") or {}
history = [h for h in (lm.get("ServerHistory") or []) if h]
old = str(lm.get("BaseUrl") or "").rstrip("/")
if old and old != url and old not in history: history.insert(0, old)
servers = [sv for sv in (lm.get("Servers") or []) if sv and str(sv.get("BaseUrl") or "").rstrip("/") != url]
for sv in servers: sv["IsDefault"] = False
servers.append({"Name": "This PC", "BaseUrl": url, "Model": model, "ModelName": "Qwen2.5-VL 7B", "IsDefault": True})
lm.update({"BaseUrl": url, "SelectedModel": model, "ServerHistory": history, "Servers": servers})
s["LMStudioSettings"] = lm
with open(path, "w", encoding="utf-8") as f: json.dump(s, f, indent=2)
PYEOF
ok "FlipPix settings: $CONFIG_DIR/settings.json"

# Where Setup put things (CompanionConfig).
FP_FILE="$CONFIG_DIR/companion.json" FP_ROOT="$ROOT" FP_LLM="$LLM_DIR/start-llm.sh" FP_FILTER="$FILTER_MODEL" "$PY" - <<'PYEOF'
import json, os
with open(os.environ["FP_FILE"], "w", encoding="utf-8") as f:
    json.dump({"PortableRoot": os.environ["FP_ROOT"], "LlmStartScript": os.environ["FP_LLM"],
               "FilterModel": os.environ["FP_FILTER"]}, f, indent=2)
PYEOF
ok "companion settings: $CONFIG_DIR/companion.json"

# The flippix-companion command.
BIN_DIR="$HOME/.local/bin"
mkdir -p "$BIN_DIR"
USE_SYSTEMD=0
if [ "$WANT_SERVICE" = 1 ] && systemctl --user show-environment >/dev/null 2>&1; then USE_SYSTEMD=1; fi
cat > "$BIN_DIR/flippix-companion" <<EOF
#!/usr/bin/env bash
# FlipPix iOS Companion. Written by install-ios-companion-linux.sh.
APP="$APP_DIR/flippix-companion"
LOGS="$DATA_DIR/companion/logs"
USE_SYSTEMD=$USE_SYSTEMD
case "\${1:-status}" in
    start|stop|restart)
        if [ "\$USE_SYSTEMD" = 1 ]; then
            systemctl --user "\$1" $SERVICE
        else
            pkill -f "\$APP serve" 2>/dev/null; sleep 1
            [ "\$1" = stop ] || { mkdir -p "\$LOGS"; nohup "\$APP" serve >> "\$LOGS/service.log" 2>&1 & }
        fi
        [ "\$1" = stop ] && exit 0
        echo "Starting..."; sleep 4; exec "\$APP" status ;;
    logs)
        echo "Logs: \$LOGS"; ls -1 "\$LOGS" 2>/dev/null
        [ "\$USE_SYSTEMD" = 1 ] && exec journalctl --user -u $SERVICE -f -n 50
        exec tail -n 50 -F "\$LOGS/companion.log" ;;
    uninstall)
        exec bash "$ROOT/uninstall.sh" ;;
    -h|--help|help)
        "\$APP" --help
        echo "  start | stop | restart   the background service"
        echo "  logs     follow the companion's log"
        echo "  uninstall"
        ;;
    *)
        exec "\$APP" "\$@" ;;
esac
EOF
chmod +x "$BIN_DIR/flippix-companion"
ok "command: flippix-companion"

cat > "$ROOT/uninstall.sh" <<EOF
#!/usr/bin/env bash
# Removes the FlipPix iOS Companion. Written by install-ios-companion-linux.sh.
echo "This stops the FlipPix iOS Companion and removes its service and settings."
systemctl --user disable --now $SERVICE 2>/dev/null
pkill -f "$APP_DIR/flippix-companion serve" 2>/dev/null
rm -f "\${XDG_CONFIG_HOME:-\$HOME/.config}/systemd/user/$SERVICE.service" "$BIN_DIR/flippix-companion"
systemctl --user daemon-reload 2>/dev/null
rm -f "$CONFIG_DIR/companion.json" "$CONFIG_DIR/companion-remote.json"
rm -rf "$DATA_DIR/companion"
read -r -p "Also delete $ROOT (ComfyUI, the writing assistant and ~60 GB of models)? [y/N] " a
if [[ "\$a" =~ ^[Yy] ]]; then rm -rf "$ROOT"; echo "Deleted $ROOT."; else echo "Kept $ROOT."; fi
echo "Done."
EOF
chmod +x "$ROOT/uninstall.sh"

# Let the iPad in through the firewall, if one is on (ufw.conf is world-readable; ufw status needs root).
if command -v ufw >/dev/null 2>&1 && grep -qs '^ENABLED=yes' /etc/ufw/ufw.conf; then
    need_sudo "opening ports $REMOTE_PORT (TCP) and $DISCOVERY_PORT (UDP) in the firewall for the iPad"
    as_root ufw allow "$REMOTE_PORT/tcp" comment "FlipPix iOS Companion" >/dev/null
    as_root ufw allow "$DISCOVERY_PORT/udp" comment "FlipPix iOS Companion discovery" >/dev/null
    ok "firewall: ports $REMOTE_PORT/tcp and $DISCOVERY_PORT/udp open"
fi

# The service: starts with the PC (linger) or at sign-in, restarts if it stops.
if [ "$USE_SYSTEMD" = 1 ]; then
    UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
    mkdir -p "$UNIT_DIR"
    cat > "$UNIT_DIR/$SERVICE.service" <<EOF
[Unit]
Description=$PRODUCT (serves the FlipPix iPad app; keeps ComfyUI and the writing assistant running)
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=$APP_DIR/flippix-companion serve
Restart=on-failure
RestartSec=10
TimeoutStopSec=30

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload
    systemctl --user enable "$SERVICE" >/dev/null 2>&1
    systemctl --user restart "$SERVICE"
    ok "service $SERVICE enabled and started"
    if [ "$WANT_LINGER" = 1 ] && [ "$(loginctl show-user "$USER" -p Linger --value 2>/dev/null)" != yes ]; then
        if ask_yes "Start the companion when the PC boots, even before you sign in?"; then
            need_sudo "letting the companion start at boot (loginctl enable-linger)"
            as_root loginctl enable-linger "$USER" && ok "starts when the PC boots"
        else
            info "It starts when you sign in."
        fi
    fi
elif [ "$WANT_SERVICE" = 1 ]; then
    warn "systemd user services aren't available here (WSL without systemd?)."
    info "Starting the companion now; after a restart, start it again with:  flippix-companion start"
    "$BIN_DIR/flippix-companion" start >/dev/null 2>&1 || true
else
    info "Not installing the service (--no-service). Run it with:  $APP_DIR/flippix-companion serve"
fi

# =============================================================================================
step "Checking that everything starts"
# =============================================================================================
wait_url() {
    # wait_url URL SECONDS LABEL [STATUS_ROW]: polls URL until it answers. With STATUS_ROW, gives up
    # early once the companion has reported that server stopped twice (it crashed on start).
    local url="$1" secs="$2" label="$3" row="${4:-}" t=0 stops=0
    while [ "$t" -lt "$secs" ]; do
        if curl -fs -m 3 "$url" >/dev/null 2>&1; then printf '\r%-70s\r' ""; return 0; fi
        if [ -n "$row" ] && "$APP_DIR/flippix-companion" status 2>/dev/null | grep -F "$row" | grep -q "Stopped"; then
            stops=$((stops + 1))
            if [ "$stops" -ge 2 ]; then printf '\n'; return 1; fi
            sleep 60; t=$((t + 60)); continue
        fi
        printf '\r  %s (%ss)   ' "$label" "$t"
        sleep 5; t=$((t + 5))
    done
    printf '\n'
    return 1
}

COMPANION_RUNNING=0
"$APP_DIR/flippix-companion" status >/dev/null 2>&1 && COMPANION_RUNNING=1
if [ "$COMPANION_RUNNING" = 0 ]; then
    sleep 5
    "$APP_DIR/flippix-companion" status >/dev/null 2>&1 && COMPANION_RUNNING=1
fi
LLM_OK=0
if [ "$COMPANION_RUNNING" = 1 ]; then
    # The companion starts both servers; the first ComfyUI start can take several minutes.
    if wait_url "http://127.0.0.1:$LLM_PORT/health" 300 "Loading Qwen2.5-VL onto the GPU"; then
        reply="$(curl -fs -m 120 "http://127.0.0.1:$LLM_PORT/v1/chat/completions" -H 'Content-Type: application/json' \
            -d "{\"model\":\"$LLM_ALIAS\",\"max_tokens\":8,\"messages\":[{\"role\":\"user\",\"content\":\"Reply with the single word OK.\"}]}" || true)"
        if echo "$reply" | grep -q '"content"'; then LLM_OK=1; fi
    fi
    [ "$LLM_OK" = 1 ] && ok "The writing assistant (Qwen2.5-VL) answered." \
        || warn "The writing assistant didn't answer. Run $LLM_DIR/start-llm.sh to see why."
    if wait_url "http://127.0.0.1:$COMFY_PORT/system_stats" 900 "Starting ComfyUI and loading custom nodes" "(ComfyUI)"; then
        ok "ComfyUI is running on $GPU_NAME."
    else
        warn "ComfyUI didn't start. The end of its log ($DATA_DIR/companion/logs/comfyui.log):"
        tail -n 8 "$DATA_DIR/companion/logs/comfyui.log" 2>/dev/null | sed 's/^/      /' || true
    fi
else
    warn "The companion isn't running. See: journalctl --user -u $SERVICE -n 50"
fi

# =============================================================================================
echo
echo "${C_M}${C_B}  ============================================================"
echo "     Your PC is ready for the iPad"
echo "  ============================================================${C_0}"
case ":$PATH:" in *":$BIN_DIR:"*) ;; *) warn "Open a new terminal (or sign out and in) so the flippix-companion command is found." ;; esac
echo
echo "  Connect your iPad:"
echo "    1. Put the iPad on the same Wi-Fi as this PC."
echo "    2. Open FlipPix on the iPad and tap $(hostname)."
echo "    3. Type the 6-digit code below. For a new code later, run:  ${C_B}flippix-companion code${C_0}"
if [ "$COMPANION_RUNNING" = 1 ]; then
    "$APP_DIR/flippix-companion" code || true
fi
echo
echo "  flippix-companion           pairing code and status"
echo "  flippix-companion restart   restart it      flippix-companion logs   follow its log"
echo "  Installed in $ROOT   (remove: flippix-companion uninstall)"
echo "  Setup log: $LOG_FILE"
