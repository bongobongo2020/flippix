#!/usr/bin/env bash
#
# Builds the optimized Linux publish of FlipPix without involving makepkg.
# Use this to test a build, or to produce a tarball for a machine that has no SDK.
#
#   ./packaging/build-linux.sh                 # framework-dependent (needs dotnet-runtime-8.0)
#   ./packaging/build-linux.sh --self-contained # bundles the runtime, no dotnet needed
#   ./packaging/build-linux.sh --tarball        # also produce flippix-linux-x64.tar.gz

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/FlipPix.UI.Linux/FlipPix.UI.Linux.csproj"
OUTPUT="$REPO_ROOT/publish-linux"

SELF_CONTAINED=false
MAKE_TARBALL=false
for arg in "$@"; do
    case "$arg" in
        --self-contained) SELF_CONTAINED=true ;;
        --tarball)        MAKE_TARBALL=true ;;
        -h|--help)        sed -n '2,10p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

command -v dotnet >/dev/null 2>&1 || {
    echo "dotnet not found. Install the SDK: sudo pacman -S dotnet-sdk-8.0" >&2
    exit 1
}

command -v ffmpeg >/dev/null 2>&1 || \
    echo "note: ffmpeg is not installed; video features will be disabled at runtime." >&2

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

echo "Publishing FlipPix (self-contained=$SELF_CONTAINED) ..."
rm -rf "$OUTPUT"

dotnet publish "$PROJECT" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained "$SELF_CONTAINED" \
    -p:PublishReadyToRun=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    --output "$OUTPUT"

# Backends for other platforms that Avalonia.Desktop pulls in but Linux never loads.
rm -f "$OUTPUT/Avalonia.Win32.dll" "$OUTPUT/Avalonia.Native.dll" "$OUTPUT/Avalonia.DesignerSupport.dll"

# A tree built on Windows arrives without the executable bit.
[[ -f "$OUTPUT/FlipPix.UI.Linux" ]] && chmod +x "$OUTPUT/FlipPix.UI.Linux"

echo
echo "Built to: $OUTPUT  ($(du -sh "$OUTPUT" | cut -f1))"
if [[ "$SELF_CONTAINED" == true ]]; then
    echo "Run with: $OUTPUT/FlipPix.UI.Linux"
else
    echo "Run with: dotnet $OUTPUT/FlipPix.UI.Linux.dll"
fi

if [[ "$MAKE_TARBALL" == true ]]; then
    TARBALL="$REPO_ROOT/flippix-linux-x64.tar.gz"
    tar -czf "$TARBALL" -C "$(dirname "$OUTPUT")" "$(basename "$OUTPUT")"
    echo "Tarball: $TARBALL ($(du -sh "$TARBALL" | cut -f1))"
fi
