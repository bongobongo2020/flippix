#!/bin/bash
set -e

echo "Building FlipPix for Linux..."

# Check dependencies
command -v dotnet >/dev/null 2>&1 || { echo "dotnet not found. Install .NET 8 SDK first."; exit 1; }
command -v ffmpeg >/dev/null 2>&1 || echo "WARNING: ffmpeg not found - video features may not work"

OUTPUT_DIR="./publish-linux"
rm -rf "$OUTPUT_DIR"

dotnet publish FlipPix.UI.Linux/FlipPix.UI.Linux.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$OUTPUT_DIR"

# Copy workflow and prompts (if not already included via csproj)
if [ -d "workflow" ] && [ ! -d "$OUTPUT_DIR/workflow" ]; then
    cp -r workflow "$OUTPUT_DIR/"
fi
if [ -d "prompts" ] && [ ! -d "$OUTPUT_DIR/prompts" ]; then
    cp -r prompts "$OUTPUT_DIR/"
fi

# Make executable
chmod +x "$OUTPUT_DIR/FlipPix.UI.Linux"

echo ""
echo "Build complete! Run:"
echo "  $OUTPUT_DIR/FlipPix.UI.Linux"
