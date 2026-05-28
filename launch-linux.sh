#!/bin/bash
cd "$(dirname "$0")"
export DOTNET_ROOT=/usr/share/dotnet
LOG="$HOME/.config/FlipPix/launch.log"
mkdir -p "$(dirname "$LOG")"
echo "[$(date)] Launching FlipPix..." >> "$LOG"
exec ./publish-linux/FlipPix.UI.Linux "$@" >> "$LOG" 2>&1
