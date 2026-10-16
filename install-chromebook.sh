#!/usr/bin/env bash
#
# Installs FlipPix on a Chromebook — in ChromeOS's Linux development environment (Debian) — or on
# any Debian/Ubuntu machine. FlipPix then appears in the ChromeOS launcher like any other app.
#
#   ./install-chromebook.sh              build from this checkout and install
#   ./install-chromebook.sh --no-deps    skip the apt packages (already installed)
#   ./install-chromebook.sh --uninstall  remove the app, launcher and menu entry (keeps settings)
#
# The Image and Video Generators need a ComfyUI server with an NVIDIA GPU. A Chromebook has no such
# GPU, so ComfyUI runs on your PC and FlipPix connects to it over the network (see CHROMEBOOK.md).

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${PREFIX:-$HOME/.local/opt/flippix}"
BIN_DIR="$HOME/.local/bin"
APPS_DIR="$HOME/.local/share/applications"
ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"

INSTALL_DEPS=true
for arg in "$@"; do
    case "$arg" in
        --no-deps) INSTALL_DEPS=false ;;
        --uninstall)
            rm -rf "$PREFIX" "$BIN_DIR/flippix" "$APPS_DIR/flippix.desktop" "$ICON_DIR/flippix.png"
            command -v update-desktop-database >/dev/null && update-desktop-database "$APPS_DIR" 2>/dev/null || true
            echo "FlipPix removed. Settings are kept in ~/.config/FlipPix."
            exit 0 ;;
        -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

case "$(uname -m)" in
    x86_64)  RID=linux-x64 ;;
    aarch64) RID=linux-arm64 ;;
    *) echo "Unsupported CPU: $(uname -m)" >&2; exit 1 ;;
esac

step() { printf '\n\033[1;38;5;208m▶ %s\033[0m\n' "$*"; }

# ── 1. System packages ────────────────────────────────────────────────────────
if $INSTALL_DEPS; then
    step "Installing system packages (ffmpeg, libvlc, fonts) — you may be asked for your password"
    # Package names changed with Debian 13's 64-bit time_t transition; take whichever exists.
    pick() { for p in "$@"; do apt-cache show "$p" >/dev/null 2>&1 && { echo "$p"; return; }; done; }
    sudo apt-get update -q
    PKGS=(
        ffmpeg                     # frame grabs, joins, probes
        libvlc5 vlc-plugin-base    # in-app video playback
        fonts-noto-color-emoji     # the emoji in tab and button labels
        fonts-dejavu-core
        xdg-utils                  # "open file / folder" actions
        libice6 libsm6 libfontconfig1
        "$(pick libgtk-3-0t64 libgtk-3-0)"   # native file chooser
        curl ca-certificates
    )
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -q "${PKGS[@]}"
fi

# ── 2. .NET SDK (build only; the installed app is self-contained) ──────────────
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DOTNET=""
for d in "$(command -v dotnet || true)" "$HOME/.dotnet/dotnet"; do
    if [[ -n "$d" && -x "$d" ]] && "$d" --list-sdks 2>/dev/null | grep -q '^8\.'; then DOTNET="$d"; break; fi
done
if [[ -z "$DOTNET" ]]; then
    step "Installing the .NET 8 SDK into ~/.dotnet (used only to build FlipPix)"
    bash "$REPO/dotnet-install.sh" --channel 8.0 --install-dir "$HOME/.dotnet"
    DOTNET="$HOME/.dotnet/dotnet"
fi

# ── 3. Build ────────────────────────────────────────────────────────────────────
step "Building FlipPix for $RID (a few minutes the first time)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
"$DOTNET" publish "$REPO/FlipPix.UI.Linux/FlipPix.UI.Linux.csproj" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    -p:PublishReadyToRun=true \
    -p:DebugType=none -p:DebugSymbols=false \
    --output "$STAGE/app"

# ── 4. Install ──────────────────────────────────────────────────────────────────
step "Installing to $PREFIX"
rm -rf "$PREFIX"
mkdir -p "$(dirname "$PREFIX")"
mv "$STAGE/app" "$PREFIX"
chmod +x "$PREFIX/FlipPix.UI.Linux"
# Backends for other platforms that Avalonia.Desktop pulls in but Linux never loads.
rm -f "$PREFIX/Avalonia.Win32.dll" "$PREFIX/Avalonia.Native.dll"

# The shared view models look for ffmpeg.exe / ffprobe.exe beside the app and reveal files with
# explorer.exe; give them same-named stand-ins (the app re-creates these if they go missing).
ln -sf "$(command -v ffmpeg)"  "$PREFIX/ffmpeg.exe"  2>/dev/null || true
ln -sf "$(command -v ffprobe)" "$PREFIX/ffprobe.exe" 2>/dev/null || true
cat > "$PREFIX/explorer.exe" <<'SH'
#!/bin/sh
# explorer.exe stand-in for FlipPix on Linux: "explorer.exe /select,<file>" or "explorer.exe <folder>".
arg="$*"
case "$arg" in
  /select,*) target="${arg#/select,}"; target="${target#\"}"; target="${target%\"}"; target="$(dirname "$target")" ;;
  *) target="${arg#\"}"; target="${target%\"}" ;;
esac
exec xdg-open "$target" >/dev/null 2>&1
SH
chmod +x "$PREFIX/explorer.exe"

# ── 5. Launcher, icon, menu entry ───────────────────────────────────────────────
mkdir -p "$BIN_DIR" "$APPS_DIR" "$ICON_DIR"
# Local copies of every generated image / video, so browsing results never touches the share.
mkdir -p "$HOME/flippix" "$HOME/flippix-vids"
cat > "$BIN_DIR/flippix" <<SH
#!/usr/bin/env bash
# FlipPix launcher. Logs go to ~/.local/state/FlipPix/launch.log.
LOG_DIR="\${XDG_STATE_HOME:-\$HOME/.local/state}/FlipPix"
mkdir -p "\$LOG_DIR"
cd "$PREFIX"
exec "$PREFIX/FlipPix.UI.Linux" "\$@" >> "\$LOG_DIR/launch.log" 2>&1
SH
chmod +x "$BIN_DIR/flippix"

cp "$REPO/flippix.png" "$ICON_DIR/flippix.png"
cat > "$APPS_DIR/flippix.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=FlipPix
GenericName=AI Image & Video Generator
Comment=Generate images and videos with ComfyUI
Exec=$BIN_DIR/flippix
Icon=$ICON_DIR/flippix.png
Terminal=false
Categories=Graphics;AudioVideo;
StartupWMClass=FlipPix.UI.Linux
DESKTOP
command -v update-desktop-database >/dev/null && update-desktop-database "$APPS_DIR" 2>/dev/null || true

step "Done"
cat <<MSG
FlipPix is installed. Start it from the ChromeOS launcher (Linux apps → FlipPix)
or run:  flippix

Generated images are copied to ~/flippix and videos to ~/flippix-vids.

First run asks where ComfyUI is. On a Chromebook choose "Remote Server" and enter your
GPU PC's address, e.g. http://192.168.1.50:8188 — see CHROMEBOOK.md for the output-folder share.
MSG
