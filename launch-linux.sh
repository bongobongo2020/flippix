#!/usr/bin/env bash
# Runs a local publish-linux/ build without installing the package.
# For an installed package just run `flippix`.
set -euo pipefail

cd "$(dirname "$0")"
APP_DIR="./publish-linux"

LOG_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/FlipPix/logs"
mkdir -p "$LOG_DIR"
LOG="$LOG_DIR/launch.log"

if [[ ! -d "$APP_DIR" ]]; then
    echo "No build found. Run ./packaging/build-linux.sh first." >&2
    exit 1
fi

echo "[$(date --iso-8601=seconds)] Launching FlipPix..." >> "$LOG"

cd "$APP_DIR"
if [[ -x ./FlipPix.UI.Linux ]]; then
    exec ./FlipPix.UI.Linux "$@" >> "$LOG" 2>&1     # self-contained build
else
    export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"
    exec dotnet ./FlipPix.UI.Linux.dll "$@" >> "$LOG" 2>&1
fi
