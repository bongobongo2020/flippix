#!/usr/bin/env bash
# ===========================================================================
# FlipPix - restore a ComfyUI snapshot made by backup-comfyui-remote.ps1.
#
# Two layouts are handled automatically:
#   * PORTABLE (python_embeded bundled)  -> just extract + fix paths. No pip,
#       no venv, no reinstalling custom nodes. Launch with the bundled
#       run_nvidia_gpu.sh (or the run.sh written here). This is the default
#       for FlipPix backups -- it "just works" (needs an NVIDIA GPU + driver).
#   * VENV (no python_embeded)           -> rebuild a venv and install torch +
#       ComfyUI + each custom node's requirements.
#
# Usage:
#   bash restore-comfyui.sh [backup.tar.gz] [target-dir] [options]
#
#   [backup.tar.gz]  local snapshot to restore. Optional if --hf is given (it is
#                    then downloaded). Default download name: flippix-comfyui.tar.gz
#   [target-dir]     where to restore it       (default: ~/flippix-comfyui)
#
#   --hf <repo>          download the bundle from a Hugging Face repo if it isn't
#                        already present locally, e.g. --hf bongobongo2020/flippix-comfyui
#   --hf-file <name>     filename inside the repo (default: the archive's basename,
#                        else flippix-comfyui.tar.gz)
#   --hf-revision <rev>  branch / tag / commit to pull (default: main)
#   --sha256 <hex>       verify the archive against this checksum. If omitted, the
#                        script tries to fetch <file>.sha256 from the same HF repo.
#   --cpu                (venv layout only) install CPU-only torch (default: CUDA cu121)
#   --skip-deps          (venv layout only) just extract; don't build the venv
#   --exact              (venv layout only) install requirements-freeze.txt verbatim
#
# One-command restore from Hugging Face (public repo, no login needed):
#   bash restore-comfyui.sh --hf bongobongo2020/flippix-comfyui
# Gated/private repo: set HF_TOKEN=hf_xxx in the environment first.
#
# Prereqs:  portable layout needs only tar + (curl|wget|hf) to fetch (+ NVIDIA driver
#           to run). venv layout also needs: sudo apt install -y python3 python3-venv git
# ===========================================================================
set -euo pipefail

say()  { printf '\n==> %s\n' "$*"; }
ok()   { printf '  [ok] %s\n' "$*"; }
warn() { printf '  [!] %s\n' "$*"; }
die()  { printf '  [x] %b\n' "$*" >&2; exit 1; }

ARCHIVE=""
TARGET="$HOME/flippix-comfyui"
TORCH_INDEX="https://download.pytorch.org/whl/cu121"
SKIP_DEPS=0
EXACT=0
HF_REPO=""
HF_FILE=""
HF_REV="main"
SHA256=""

# ---- parse args (positionals + flags, any order) --------------------------
pos=0
while [ $# -gt 0 ]; do
    case "$1" in
        --cpu)         TORCH_INDEX="https://download.pytorch.org/whl/cpu" ;;
        --skip-deps)   SKIP_DEPS=1 ;;
        --exact)       EXACT=1 ;;
        --hf)          HF_REPO="${2:-}"; shift ;;
        --hf-file)     HF_FILE="${2:-}"; shift ;;
        --hf-revision) HF_REV="${2:-}";  shift ;;
        --sha256)      SHA256="${2:-}";  shift ;;
        -h|--help)     sed -n '2,52p' "$0"; exit 0 ;;
        -*)            die "unknown option: $1" ;;
        *)
            if   [ $pos -eq 0 ]; then ARCHIVE="$1"
            elif [ $pos -eq 1 ]; then TARGET="$1"
            else die "too many positional args: $1"; fi
            pos=$((pos+1)) ;;
    esac
    shift
done

# Auth header for gated/private HF repos (built as arrays to survive word-splitting).
CURL_AUTH=(); WGET_AUTH=()
if [ -n "${HF_TOKEN:-}" ]; then
    CURL_AUTH=(-H "Authorization: Bearer $HF_TOKEN")
    WGET_AUTH=(--header="Authorization: Bearer $HF_TOKEN")
fi

# ---- resolve / download the archive (optionally from Hugging Face) --------
# Default the HF filename from the positional archive name, else a stable name.
if [ -z "$HF_FILE" ]; then
    if [ -n "$ARCHIVE" ]; then HF_FILE="$(basename "$ARCHIVE")"; else HF_FILE="flippix-comfyui.tar.gz"; fi
fi
# No local archive path but an HF repo given -> download next to us under HF_FILE.
if [ -z "$ARCHIVE" ]; then
    [ -n "$HF_REPO" ] || die "usage: restore-comfyui.sh <backup.tar.gz> [target] [--hf <repo>] ...  (see --help)"
    ARCHIVE="./$HF_FILE"
fi

fetch_from_hf() {
    local dest="$1" destdir
    destdir="$(dirname "$dest")"; mkdir -p "$destdir"
    say "Downloading bundle from Hugging Face: $HF_REPO :: $HF_FILE ($HF_REV)"
    # Prefer the official client (resumable; handles gated repos via login / HF_TOKEN).
    local cli=""
    command -v hf >/dev/null 2>&1 && cli="hf"
    [ -z "$cli" ] && command -v huggingface-cli >/dev/null 2>&1 && cli="huggingface-cli"
    if [ -n "$cli" ]; then
        if "$cli" download "$HF_REPO" "$HF_FILE" --revision "$HF_REV" --local-dir "$destdir"; then
            [ -f "$dest" ] || cp -f "$destdir/$HF_FILE" "$dest" 2>/dev/null || true
            [ -f "$dest" ] && return 0
        fi
        warn "$cli download failed; falling back to direct HTTPS"
    fi
    # Direct, resumable HTTPS -- works for public repos with no HF client installed.
    local url="https://huggingface.co/${HF_REPO}/resolve/${HF_REV}/${HF_FILE}?download=true"
    if command -v curl >/dev/null 2>&1; then
        curl -L --fail -C - "${CURL_AUTH[@]}" -o "$dest" "$url"
    elif command -v wget >/dev/null 2>&1; then
        wget -c "${WGET_AUTH[@]}" -O "$dest" "$url"
    else
        die "need 'hf', 'curl', or 'wget' installed to download from Hugging Face"
    fi
}

if [ ! -f "$ARCHIVE" ]; then
    [ -n "$HF_REPO" ] || die "archive not found: $ARCHIVE  (and no --hf <repo> given)"
    fetch_from_hf "$ARCHIVE"
    [ -f "$ARCHIVE" ] || die "download did not produce $ARCHIVE"
    ok "downloaded ($(du -h "$ARCHIVE" | cut -f1)) -> $ARCHIVE"
else
    ok "using local archive: $ARCHIVE"
fi

# ---- verify checksum (explicit --sha256, else <file>.sha256 from the HF repo) ----
if [ -z "$SHA256" ] && [ -n "$HF_REPO" ] && command -v curl >/dev/null 2>&1; then
    SHA256="$(curl -fsSL "${CURL_AUTH[@]}" \
        "https://huggingface.co/${HF_REPO}/resolve/${HF_REV}/${HF_FILE}.sha256" 2>/dev/null \
        | awk '{print $1}' | head -1 || true)"
fi
if [ -n "$SHA256" ]; then
    if command -v sha256sum >/dev/null 2>&1; then
        got="$(sha256sum "$ARCHIVE" | awk '{print $1}')"
        [ "$got" = "$SHA256" ] && ok "sha256 verified" \
            || die "sha256 MISMATCH for $ARCHIVE\n      expected $SHA256\n      got      $got\n      delete the file and re-download."
    else
        warn "sha256sum not available; skipping checksum verification"
    fi
else
    warn "no checksum provided/found; skipping verification (pass --sha256 to enforce)"
fi

# ---- extract --------------------------------------------------------------
say "Restoring $ARCHIVE -> $TARGET"
mkdir -p "$TARGET"
tar xzf "$ARCHIVE" -C "$TARGET"
ok "extracted"

# ComfyUI root: $TARGET itself, or $TARGET/ComfyUI
if   [ -f "$TARGET/main.py" ];          then ROOT="$TARGET"
elif [ -f "$TARGET/ComfyUI/main.py" ];  then ROOT="$TARGET/ComfyUI"
else warn "main.py not found; assuming ComfyUI root is $TARGET"; ROOT="$TARGET"
fi
ok "ComfyUI root: $ROOT"

# Detect a bundled portable python (python_embeded next to ComfyUI, or at target root).
PYEMBED=""
for c in "$TARGET/python_embeded" "$ROOT/python_embeded" "$ROOT/../python_embeded"; do
    if [ -x "$c/bin/python3" ] || [ -x "$c/python" ]; then PYEMBED="$(cd "$c" && pwd)"; break; fi
done

MANIFEST="$TARGET/.flippix-backup/manifest.txt"
[ -f "$MANIFEST" ] || MANIFEST="$ROOT/.flippix-backup/manifest.txt"
if [ -f "$MANIFEST" ]; then echo "  --- manifest ---"; sed 's/^/      /' "$MANIFEST"; fi

# ---- fix a dangling models symlink (source box pointed it at external storage) ----
fix_models() {
    local m="$ROOT/models"
    if [ -L "$m" ] && [ ! -e "$m" ]; then
        local tgt; tgt="$(readlink "$m")"
        rm -f "$m"; mkdir -p "$m"
        warn "models was a dangling symlink (-> $tgt); replaced with an empty models/ dir."
        warn "Add weights there, re-create the symlink, or use extra_model_paths.yaml."
    fi
}

# ===========================================================================
# PORTABLE layout: python_embeded is bundled -> nothing to install.
# ===========================================================================
if [ -n "$PYEMBED" ]; then
    say "Portable install detected (python_embeded bundled) -- no build needed"
    ok "python_embeded: $PYEMBED"

    # Make launchers + the embedded interpreter executable (tar usually preserves this).
    find "$TARGET" -maxdepth 2 -name '*.sh' -exec chmod +x {} \; 2>/dev/null || true
    [ -x "$PYEMBED/bin/python3" ] || chmod +x "$PYEMBED/bin/python3" 2>/dev/null || true
    PYBIN="$PYEMBED/bin/python3"; [ -x "$PYBIN" ] || PYBIN="$PYEMBED/python"

    fix_models

    # The bundled launcher dir is whatever contains run_nvidia_gpu.sh (usually $TARGET).
    LAUNCH_DIR="$TARGET"
    [ -f "$LAUNCH_DIR/run_nvidia_gpu.sh" ] || LAUNCH_DIR="$ROOT"

    # Write a simple, non-interactive run.sh next to the bundled launcher.
    REL_PY="$(realpath --relative-to="$LAUNCH_DIR" "$PYBIN" 2>/dev/null || echo "$PYBIN")"
    REL_MAIN="$(realpath --relative-to="$LAUNCH_DIR" "$ROOT/main.py" 2>/dev/null || echo "$ROOT/main.py")"
    cat > "$LAUNCH_DIR/run.sh" <<RUN
#!/usr/bin/env bash
# Non-interactive launcher for this restored ComfyUI (http://0.0.0.0:8188).
cd "\$(dirname "\$0")"
exec "./$REL_PY" "$REL_MAIN" --listen 0.0.0.0 --port 8188 "\$@"
RUN
    chmod +x "$LAUNCH_DIR/run.sh"
    ok "wrote $LAUNCH_DIR/run.sh"

    if ! command -v nvidia-smi >/dev/null 2>&1; then
        warn "nvidia-smi not found. python_embeded ships CUDA PyTorch; you need an NVIDIA"
        warn "GPU + driver (in WSL: a recent Windows NVIDIA driver exposes the GPU)."
    fi

    say "Restore complete (portable)"
    echo "  ComfyUI : $ROOT"
    echo "  Launch  : cd \"$LAUNCH_DIR\" && ./run_nvidia_gpu.sh   (interactive, VRAM auto-detect)"
    echo "        or: cd \"$LAUNCH_DIR\" && ./run.sh               (non-interactive, :8188)"
    echo "  Then point FlipPix at this ComfyUI (host = this machine's IP, port 8188)."
    exit 0
fi

# ===========================================================================
# VENV layout: no python_embeded -> rebuild the environment.
# ===========================================================================
say "No python_embeded -- will rebuild a venv"

# run.sh launcher for the venv layout
cat > "$ROOT/run.sh" <<RUN
#!/usr/bin/env bash
cd "\$(dirname "\$0")"
source venv/bin/activate
exec python main.py --listen 0.0.0.0 --port 8188 "\$@"
RUN
chmod +x "$ROOT/run.sh"
ok "wrote run.sh launcher"
fix_models

if [ "$SKIP_DEPS" -eq 1 ]; then
    say "Done (--skip-deps): files restored, venv NOT built."
    echo "  Later:  cd $ROOT && python3 -m venv venv && source venv/bin/activate && pip install -r requirements.txt"
    exit 0
fi

command -v python3 >/dev/null 2>&1 || { echo "ERROR: python3 not found. sudo apt install -y python3 python3-venv git" >&2; exit 1; }
python3 -c 'import venv' >/dev/null 2>&1 || { echo "ERROR: python3-venv missing. sudo apt install -y python3-venv" >&2; exit 1; }

say "Creating venv at $ROOT/venv"
cd "$ROOT"
python3 -m venv venv
# shellcheck disable=SC1091
source venv/bin/activate
python -m pip install --upgrade pip wheel >/dev/null
ok "venv ready ($(python --version 2>&1))"

FREEZE="$TARGET/.flippix-backup/requirements-freeze.txt"
[ -f "$FREEZE" ] || FREEZE="$ROOT/.flippix-backup/requirements-freeze.txt"

if [ "$EXACT" -eq 1 ] && [ -f "$FREEZE" ]; then
    say "Exact reproduction from requirements-freeze.txt"
    if pip install --extra-index-url "$TORCH_INDEX" -r "$FREEZE"; then
        ok "installed frozen environment"
    else
        warn "exact install failed; falling back to torch + requirements.txt"; EXACT=0
    fi
fi

if [ "$EXACT" -ne 1 ]; then
    say "Installing PyTorch ($TORCH_INDEX)"
    pip install --index-url "$TORCH_INDEX" torch torchvision torchaudio
    ok "torch installed"
    if [ -f "$ROOT/requirements.txt" ]; then
        say "Installing ComfyUI requirements"; pip install -r "$ROOT/requirements.txt"; ok "done"
    fi
    say "Installing custom-node requirements"
    if [ -d "$ROOT/custom_nodes" ]; then
        for req in "$ROOT"/custom_nodes/*/requirements.txt; do
            [ -f "$req" ] || continue
            name="$(basename "$(dirname "$req")")"; echo "  - $name"
            pip install -r "$req" >/dev/null 2>&1 || warn "some deps for $name failed (continuing)"
        done
        ok "custom-node requirements installed"
    fi
fi

say "Restore complete (venv)"
echo "  ComfyUI : $ROOT"
echo "  Launch  : cd \"$ROOT\" && ./run.sh        (http://0.0.0.0:8188)"
echo "  Then point FlipPix at this ComfyUI."
