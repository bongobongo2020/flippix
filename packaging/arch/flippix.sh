#!/bin/bash
# FlipPix launcher.
#
# The app is a framework-dependent .NET build, so it needs the system runtime that
# dotnet-runtime-8.0 installs. Everything below is overridable from the environment.

set -euo pipefail

APP_DIR="/usr/lib/flippix"
APP_DLL="$APP_DIR/FlipPix.UI.Linux.dll"

# Arch installs the runtime here; honour an existing DOTNET_ROOT if the user set one.
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"

# Keep first-run chatter and usage reporting out of the way.
export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"
export DOTNET_NOLOGO="${DOTNET_NOLOGO:-1}"

# Avalonia writes its cached shaders and font atlases here.
export XDG_CACHE_HOME="${XDG_CACHE_HOME:-$HOME/.cache}"

if ! command -v dotnet >/dev/null 2>&1 && [[ ! -x "$DOTNET_ROOT/dotnet" ]]; then
    echo "flippix: no .NET runtime found. Install it with: sudo pacman -S dotnet-runtime-8.0" >&2
    exit 1
fi

if [[ ! -f "$APP_DLL" ]]; then
    echo "flippix: $APP_DLL is missing. The package looks damaged; reinstall it." >&2
    exit 1
fi

# Run from the app directory: the workflow/ and prompts/ trees are resolved relative
# to the assembly location, and several ViewModels use AppContext.BaseDirectory.
cd "$APP_DIR"
exec dotnet "$APP_DLL" "$@"
