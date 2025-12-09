# FlipPix

![FlipPix Logo](flippix.png)

AI-powered image processing application that transforms image perspectives and camera angles using Qwen Image Edit models via ComfyUI.

## 🚀 Quick Start

**New to FlipPix?** Get up and running fast!

→ **[QUICKSTART.md](QUICKSTART.md)** - 3-step installation guide with automated setup

## Overview

FlipPix processes images to apply camera angle transformations, perspective changes, and visual modifications. It requires a local ComfyUI server with specific custom nodes and models to function.

## Demo

<video src="https://github.com/bongobongo2020/flippix/raw/main/flippix.mp4" controls width="100%"></video>

> **[📥 Download demo video (20MB)](https://github.com/bongobongo2020/flippix/raw/main/flippix.mp4)** if the video doesn't play above.

## Prerequisites

### System Requirements
- Windows x64 operating system
- .NET 8.0 runtime (included in self-contained build)
- Minimum 16GB RAM (32GB recommended for processing)
- NVIDIA GPU with 12GB+ VRAM (16GB+ recommended for video generation)

### ComfyUI Setup

FlipPix requires **ComfyUI running on localhost** (default: `http://127.0.0.1:8188`).

**📖 [Complete ComfyUI Setup Guide](COMFYUI_SETUP.md)** - Comprehensive step-by-step instructions for setting up ComfyUI from scratch

**🚀 [Automated Setup Scripts](scripts/README.md)** - One-click scripts to download all custom nodes and models automatically

#### Quick Setup Summary

FlipPix uses three different AI workflows:
- **Image Editing** (Qwen models) - Camera angle transformations
- **Video Generation** (Wan models) - Image-to-video animation
- **Image Generation** (Z-Image models) - Text-to-image creation

**Required Custom Nodes:**
- [ComfyUI-QwenImageEdit-MZ](https://github.com/MinusZoneAI/ComfyUI-QwenImageEdit-MZ)
- [rgthree-comfy](https://github.com/rgthree/rgthree-comfy)
- [ComfyUI_Comfyroll_CustomNodes](https://github.com/Suzie1/ComfyUI_Comfyroll_CustomNodes)
- [ComfyUI-WanVideoGenerator](https://github.com/chaojie/ComfyUI-WanVideoGenerator)
- [ComfyUI-GGUF](https://github.com/city96/ComfyUI-GGUF) (optional)

**Storage Requirements:** ~60GB for all models

For detailed installation instructions, model downloads, and troubleshooting, see **[COMFYUI_SETUP.md](COMFYUI_SETUP.md)**

## Using FlipPix

### Quick Start

1. **Start ComfyUI** (must be running before launching FlipPix)
2. **Run FlipPix**: Execute `publish\WanVaceProcessor.UI.exe`
3. **Configure ComfyUI Connection**: Set server IP (default: `127.0.0.1`) and port (default: `8188`)
4. **Select Input Files**:
   - Choose your input image
   - Select style/reference images if required
5. **Start Processing**: Click "Start Processing"

### Features

- **Camera Angle Transformations**: Low angle, high angle, rotation (90°), and perspective changes
- **Intelligent Image Scaling**: Automatically scales images to 1 megapixel for optimal processing
- **Multiple Perspective Options**: Ultra-low angle, bird's eye view, wide-angle lens effects
- **Subject Preservation**: Maintains subject identity, clothing, facial features, pose, and hairstyle
- **ComfyUI API Integration**: Full integration with ComfyUI workflow API

### Processing Details

- **Input**: Any image format supported by ComfyUI (JPEG, PNG, etc.)
- **Scaling**: Images are scaled to 1 megapixel (1,000,000 pixels total) using Lanczos resampling
- **Output**: Processed images maintain aspect ratio with enhanced perspective transformations

## Project Structure

```
flippix/
├── WanVaceProcessor.Core/          # Core models and interfaces
├── WanVaceProcessor.ComfyUI/       # ComfyUI integration services
├── WanVaceProcessor.UI/            # WPF user interface
├── workflow/                       # ComfyUI workflow definitions
│   └── qwen-edit-camera-API.json  # Main processing workflow
├── publish/                        # Built executable files
└── publish.bat                     # Build script
```

## Building from Source

Run `publish.bat` to build a self-contained executable in the `publish` folder.

```bash
# Build with publish.bat
./publish.bat

# Or manually with dotnet
dotnet publish WanVaceProcessor.UI/WanVaceProcessor.UI.csproj -c Release -r win-x64 --self-contained true
```

## Troubleshooting

### ComfyUI Connection Issues
- Verify ComfyUI is running on `http://127.0.0.1:8188`
- Check Windows Firewall is not blocking local connections
- Ensure no other service is using port 8188

### Missing Node Errors
- Install all required custom nodes listed in the ComfyUI Setup section
- Restart ComfyUI after installing custom nodes
- Check ComfyUI console for error messages

### Model Loading Errors
- Verify all model files are in correct directories
- Check model file names match exactly (case-sensitive)
- Ensure sufficient disk space for models (30GB+ total)

### Out of Memory Errors
- Process smaller images or reduce batch size
- Close other GPU-intensive applications
- Consider upgrading GPU VRAM if processing high-resolution images

## License

This project is provided as-is for personal and educational use.