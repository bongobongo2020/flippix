#!/usr/bin/env bash
#
# FlipPix - one-command install for Arch Linux and derivatives.
#
#   ./install-arch.sh                  # build + install the pacman package (recommended)
#   ./install-arch.sh --local          # install into ~/.local, no root, no pacman package
#   ./install-arch.sh --local --self-contained   # bundle the .NET runtime too
#   ./install-arch.sh --uninstall      # remove whichever of the two is installed
#
# Options:
#   -y, --yes          pass --noconfirm to pacman and makepkg
#       --skip-deps    assume the dependencies are already installed
#   -h, --help         this text
#
# Run it from a checkout, or straight off the web:
#   curl -fsSL https://raw.githubusercontent.com/bongobongo2020/flippix/main/install-arch.sh | bash
#
set -euo pipefail

REPO_URL="https://github.com/bongobongo2020/flippix.git"
PKGNAME="flippix"

MODE="package"
SELF_CONTAINED=false
ASSUME_YES=false
UNINSTALL=false
SKIP_DEPS=false

# ---------------------------------------------------------------- output helpers
if [[ -t 1 ]]; then
    B=$'\e[1m'; G=$'\e[32m'; Y=$'\e[33m'; R=$'\e[31m'; C=$'\e[36m'; Z=$'\e[0m'
else
    B=""; G=""; Y=""; R=""; C=""; Z=""
fi
step() { printf '%s==>%s %s%s%s\n' "$C" "$Z" "$B" "$*" "$Z"; }
info() { printf '    %s\n' "$*"; }
warn() { printf '%swarning:%s %s\n' "$Y" "$Z" "$*" >&2; }
die()  { printf '%serror:%s %s\n' "$R" "$Z" "$*" >&2; exit 1; }
ok()   { printf '%s  ok%s %s\n' "$G" "$Z" "$*"; }

usage() { sed -n '3,14p' "$0" | cut -c3-; exit 0; }

for arg in "$@"; do
    case "$arg" in
        --local)          MODE="local" ;;
        --package)        MODE="package" ;;
        --self-contained) SELF_CONTAINED=true ;;
        -y|--yes)         ASSUME_YES=true ;;
        --skip-deps)      SKIP_DEPS=true ;;
        --uninstall)      UNINSTALL=true ;;
        -h|--help)        usage ;;
        *) die "unknown option: $arg  (try --help)" ;;
    esac
done

NOCONFIRM=()
[[ "$ASSUME_YES" == true ]] && NOCONFIRM=(--noconfirm)

# ---------------------------------------------------------------- sanity checks
[[ $EUID -eq 0 ]] && die "do not run this as root. It calls sudo only where it must, and makepkg refuses to run as root."
command -v pacman >/dev/null 2>&1 || die "pacman not found - this installer is for Arch Linux and its derivatives."

if [[ "$(uname -m)" != "x86_64" ]]; then
    die "FlipPix publishes for linux-x64 only; this machine is $(uname -m)."
fi

# ---------------------------------------------------------------- uninstall
if [[ "$UNINSTALL" == true ]]; then
    removed=false
    if pacman -Qq "$PKGNAME" >/dev/null 2>&1; then
        step "Removing the $PKGNAME package"
        sudo pacman -Rns "${NOCONFIRM[@]}" "$PKGNAME"
        removed=true
    fi
    for p in "$HOME/.local/lib/flippix" "$HOME/.local/bin/flippix" \
             "$HOME/.local/share/applications/flippix.desktop" \
             "$HOME/.local/share/pixmaps/flippix.png"; do
        if [[ -e "$p" ]]; then rm -rf "$p"; info "removed $p"; removed=true; fi
    done
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$HOME/.local/share/applications" >/dev/null 2>&1 || true
    fi
    if [[ "$removed" == true ]]; then
        ok "FlipPix removed."
        info "Your settings and outputs were kept: ~/.config/FlipPix, ~/Pictures/flippix-images, ~/Videos/flippix-vids"
    else
        info "Nothing to remove - FlipPix does not appear to be installed."
    fi
    exit 0
fi

# ---------------------------------------------------------------- dependencies
if [[ "$SKIP_DEPS" == true ]]; then
    step "Skipping dependency install (--skip-deps)"
    command -v dotnet >/dev/null 2>&1 || die "dotnet not found and --skip-deps was given. Install dotnet-sdk-8.0."
else
    deps=(dotnet-runtime-8.0 dotnet-sdk-8.0 fontconfig freetype2 libx11 libice libsm
          libxrandr libxi libxcursor libxext hicolor-icon-theme
          ffmpeg xdg-utils xdg-user-dirs git)
    [[ "$MODE" == "package" ]] && deps+=(base-devel)

    step "Installing dependencies with pacman"
    info "${deps[*]}"
    sudo pacman -S --needed "${NOCONFIRM[@]}" "${deps[@]}"
    ok "dependencies present"
fi

# ---------------------------------------------------------------- locate the source tree
find_source() {
    # Running from inside a checkout?
    local here=""
    if [[ -n "${BASH_SOURCE[0]:-}" && -f "${BASH_SOURCE[0]}" ]]; then
        here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    fi
    local candidate
    for candidate in "$here" "$PWD"; do
        if [[ -n "$candidate" && -f "$candidate/FlipPix.UI.Linux/FlipPix.UI.Linux.csproj" ]]; then
            echo "$candidate"
            return
        fi
    done

    # Piped from curl, or run from elsewhere: clone (or refresh) a working copy.
    local dir="${FLIPPIX_SRC:-$HOME/.cache/flippix/src}"
    if [[ -d "$dir/.git" ]]; then
        step "Updating the FlipPix checkout in $dir" >&2
        git -C "$dir" pull --ff-only >&2 || warn "could not fast-forward; building the checkout as it stands"
    else
        step "Cloning FlipPix into $dir" >&2
        mkdir -p "$(dirname "$dir")"
        git clone --depth 1 "$REPO_URL" "$dir" >&2
    fi
    echo "$dir"
}

REPO_ROOT="$(find_source)"
[[ -f "$REPO_ROOT/FlipPix.UI.Linux/FlipPix.UI.Linux.csproj" ]] || die "no FlipPix source tree at $REPO_ROOT"
ok "source tree: $REPO_ROOT"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# ---------------------------------------------------------------- install: pacman package
if [[ "$MODE" == "package" ]]; then
    [[ -f "$REPO_ROOT/packaging/arch/PKGBUILD" ]] || die "packaging/arch/PKGBUILD is missing from $REPO_ROOT"

    step "Building the $PKGNAME package with makepkg"
    info "this compiles the Avalonia app; the first run downloads NuGet packages and takes a few minutes"
    (
        cd "$REPO_ROOT/packaging/arch"
        export _repo="$REPO_ROOT"
        makepkg -si "${NOCONFIRM[@]}"
    )
    ok "installed: $(pacman -Q "$PKGNAME")"
    LAUNCH="flippix"

# ---------------------------------------------------------------- install: ~/.local
else
    PREFIX="$HOME/.local"
    APP_DIR="$PREFIX/lib/flippix"

    step "Publishing FlipPix (self-contained=$SELF_CONTAINED)"
    build_args=()
    [[ "$SELF_CONTAINED" == true ]] && build_args+=(--self-contained)
    "$REPO_ROOT/packaging/build-linux.sh" "${build_args[@]}"

    [[ -d "$REPO_ROOT/publish-linux" ]] || die "the build produced no publish-linux/ directory"

    step "Installing into $APP_DIR"
    rm -rf "$APP_DIR"
    mkdir -p "$APP_DIR" "$PREFIX/bin" "$PREFIX/share/applications" "$PREFIX/share/pixmaps"
    cp -a "$REPO_ROOT/publish-linux/." "$APP_DIR/"

    if [[ "$SELF_CONTAINED" == true && -f "$APP_DIR/FlipPix.UI.Linux" ]]; then
        # The runtime is bundled, so the packaged launcher's dotnet lookup would only
        # get in the way. Run the apphost directly instead.
        chmod +x "$APP_DIR/FlipPix.UI.Linux"
        cat > "$PREFIX/bin/flippix" <<LAUNCHER
#!/bin/bash
# FlipPix launcher (self-contained build installed by install-arch.sh --local).
set -euo pipefail
export XDG_CACHE_HOME="\${XDG_CACHE_HOME:-\$HOME/.cache}"
cd "$APP_DIR"          # workflow/ and prompts/ resolve against the assembly directory
exec "$APP_DIR/FlipPix.UI.Linux" "\$@"
LAUNCHER
    else
        # The packaged launcher, repointed at $PREFIX.
        sed "s|^APP_DIR=.*|APP_DIR=\"$APP_DIR\"|" \
            "$REPO_ROOT/packaging/arch/flippix.sh" > "$PREFIX/bin/flippix"
    fi
    chmod +x "$PREFIX/bin/flippix"

    sed "s|^Exec=flippix|Exec=$PREFIX/bin/flippix|" \
        "$REPO_ROOT/packaging/arch/flippix.desktop" > "$PREFIX/share/applications/flippix.desktop"
    cp "$REPO_ROOT/flippix.png" "$PREFIX/share/pixmaps/flippix.png"
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$PREFIX/share/applications" >/dev/null 2>&1 || true
    fi

    ok "installed to $APP_DIR ($(du -sh "$APP_DIR" | cut -f1))"
    case ":$PATH:" in
        *":$PREFIX/bin:"*)
            LAUNCH="flippix" ;;
        *)
            warn "$PREFIX/bin is not on your PATH - add it to ~/.bashrc or ~/.zshrc:"
            info "export PATH=\"\$HOME/.local/bin:\$PATH\""
            LAUNCH="$PREFIX/bin/flippix" ;;
    esac
fi

# ---------------------------------------------------------------- done
echo
ok "FlipPix is installed."
echo
info "Launch it:            $LAUNCH"
info "Or find 'FlipPix' in your application menu."
echo
info "On first launch it asks whether your ComfyUI is local or remote. FlipPix does not"
info "install ComfyUI - point it at a running server (default http://127.0.0.1:8188)."
echo
info "Settings and logs:    ~/.config/FlipPix  .  ~/.local/state/FlipPix/logs"
info "Output:               ~/Pictures/flippix-images  .  ~/Videos/flippix-vids"
info "Black window?         retry with FLIPPIX_SOFTWARE_RENDER=1 $LAUNCH"
info "Uninstall:            ./install-arch.sh --uninstall"
