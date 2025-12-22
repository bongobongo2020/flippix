# FlipPix

![FlipPix Logo](flippix.png)

AI-powered creative platform for image processing, video generation, and multimedia content creation. FlipPix integrates multiple AI models through ComfyUI to provide camera angle transformations, video animation, image generation, and story creation capabilities.

## 🚀 Quick Start

**New to FlipPix?** Get up and running fast!

→ **[QUICKSTART.md](QUICKSTART.md)** - 3-step installation guide with automated setup

## Overview

FlipPix is a comprehensive AI content creation platform that offers:

- **Image Processing**: Camera angle transformations, perspective changes, and visual modifications
- **Video Generation**: Image-to-video animation with advanced AI models
- **Image Generation**: Text-to-image creation with multiple aspect ratios
- **Story Creation**: AI-powered narrative and visual story generation
- **Multimodal Integration**: Image analysis, text generation, and audio-visual content creation
- **Ollama Integration**: Local LLM support for enhanced text generation

The application requires a local ComfyUI server with specific custom nodes and models to function.

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

FlipPix uses multiple AI workflows:

**Core Workflows:**
- **Image Editing** (Qwen models) - Camera angle transformations and perspective changes
- **Video Generation** (Wan models) - Image-to-video animation with multiple aspect ratios
- **Image Generation** (Z-Image models) - Text-to-image creation with turbo generation

**Advanced Features:**
- **Story Video Creation** - Narrative-driven video generation with AI storytelling
- **Image-to-Video-to-Audio** (I2V2A) - Complete multimedia content generation
- **Image Analysis** - AI-powered image understanding and description
- **Prompt Enhancement** - Intelligent prompt optimization and generation
- **Remote API Integration** - Cloud-based AI service connectivity

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

**Image Processing:**
- **Camera Angle Transformations**: Low angle, high angle, rotation (90°), and perspective changes
- **Intelligent Image Scaling**: Automatically scales images to 1 megapixel for optimal processing
- **Multiple Perspective Options**: Ultra-low angle, bird's eye view, wide-angle lens effects
- **Subject Preservation**: Maintains subject identity, clothing, facial features, pose, and hairstyle

**Video Generation:**
- **Image-to-Video Animation**: Convert still images to animated videos
- **Multiple Aspect Ratios**: Support for various video formats and dimensions
- **High-Quality Output**: 16 FPS, 81-frame videos with smooth motion
- **SCAIL Integration**: Advanced video generation with temporal consistency

**Content Creation:**
- **AI Story Generation**: Create narratives and visual stories automatically
- **Multimodal Content**: Generate text, images, videos, and audio in coordinated workflows
- **Prompt Engineering**: Intelligent prompt optimization for better results
- **Batch Processing**: Handle multiple inputs efficiently

**Integration & Connectivity:**
- **ComfyUI API Integration**: Full integration with ComfyUI workflow API
- **Ollama LLM Support**: Local large language model integration
- **Remote API Services**: Cloud-based AI service connectivity
- **Settings Management**: Comprehensive configuration system

### Processing Details

- **Input**: Any image format supported by ComfyUI (JPEG, PNG, etc.)
- **Scaling**: Images are scaled to 1 megapixel (1,000,000 pixels total) using Lanczos resampling
- **Output**: Processed images maintain aspect ratio with enhanced perspective transformations

## Project Structure

```
flippix/
├── FlipPix.Core/                   # Core models and interfaces
├── FlipPix.ComfyUI/                # ComfyUI integration services
├── FlipPix.UI/                     # WPF user interface
│   ├── ViewModels/                 # MVVM view models
│   ├── Services/                   # Application services
│   ├── Commands/                   # Command implementations
│   ├── Windows/                    # WPF windows
│   └── Models/                     # Data models
├── workflow/                       # ComfyUI workflow definitions
│   ├── qwen-edit-camera-API.json   # Image editing workflow
│   ├── video_wan2_2_14B_i2vAPI.json # Video generation workflow
│   ├── image_z_image-TEXTAPI.json  # Image generation workflow
│   ├── i2v2aAPI.json               # Image-to-video-to-audio workflow
│   ├── qwen-zimageAPI.json         # Enhanced image generation
│   ├── QwenSTORY-API.json          # Story generation workflow
│   ├── SVI-Wan22-1207API.json      # Advanced video workflow
│   ├── wanvideo_SCAIL_API_final.json # SCAIL video integration
│   └── wanvideo_SCAIL_API_fixed_v2.json # Improved SCAIL workflow
├── scripts/                        # Setup and automation scripts
│   ├── setup-comfyui-windows.bat   # Windows setup script
│   ├── setup-comfyui.sh            # Linux/macOS setup script
│   ├── setup-comfyui.py            # Python setup script
│   └── run_scaill_chunks.py        # Chunk processing script
├── loras/                          # LoRA model files
├── publish/                        # Built executable files
├── publish.bat                     # Build script
└── package-release.bat             # Packaging script
```

## Building from Source

Run `publish.bat` to build a self-contained executable in the `publish` folder.

```bash
# Build with publish.bat
./publish.bat

# Or manually with dotnet
dotnet publish FlipPix.UI/FlipPix.UI.csproj -c Release -r win-x64 --self-contained true

# Package for distribution
./package-release.bat
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

## Workflow Files

FlipPix includes multiple specialized workflow files for different AI tasks:

### Image Processing Workflows
- **qwen-edit-camera-API.json** - Core camera angle transformation workflow
- **qwen-zimageAPI.json** - Enhanced image generation with Qwen models

### Video Generation Workflows
- **video_wan2_2_14B_i2vAPI.json** - Standard image-to-video generation
- **wanvideo_SCAIL_API_final.json** - SCAIL-enhanced video generation
- **wanvideo_SCAIL_API_fixed_v2.json** - Improved SCAIL workflow with bug fixes
- **SVI-Wan22-1207API.json** - Advanced video generation with enhanced quality

### Multimedia Workflows
- **i2v2aAPI.json** - Image-to-video-to-audio generation pipeline
- **image_z_image-TEXTAPI.json** - Text-to-image generation with Z-Image models

### AI Story Workflows
- **QwenSTORY-API.json** - AI-powered story generation and visualization

### Configuration Files
- **qwen-edit-camera-API.json** - Camera transformation API configuration
- **image_z_image-TEXTAPI.json** - Image generation API configuration

## Windows and Features

### Main Application Windows
- **ImageGeneratorWindow** - Image processing and generation interface
- **VideoGeneratorWindow** - Video creation and editing tools
- **StoryVideoWindow** - Story creation and visualization
- **I2V2AWindow** - Image-to-video-to-audio workflow manager
- **ImageAnalyzerWindow** - Image analysis and understanding tools
- **OllamaWindow** - Local LLM integration and management
- **SettingsWindow** - Application configuration and preferences

### Key Services
- **ComfyUIService** - ComfyUI API integration and workflow management
- **OllamaService** - Local LLM connectivity and prompt processing
- **PromptService** - Intelligent prompt engineering and optimization
- **WorkflowExecutionService** - Workflow orchestration and execution
- **ChunkCreatorService** - Content processing and chunking algorithms

## Technology Stack

- **.NET 8.0** - Modern cross-platform framework
- **WPF (Windows Presentation Foundation)** - Rich desktop UI framework
- **MVVM Pattern** - Model-View-ViewModel architecture
- **CommunityToolkit.Mvvm** - MVVM helpers and utilities
- **Serilog** - Structured logging framework
- **FFMpegCore** - Video processing and multimedia handling
- **YamlDotNet** - YAML configuration parsing

## Contributing

FlipPix is actively developed with contributions welcome. Key areas for contribution:
- Additional AI model integrations
- New workflow templates
- UI/UX improvements
- Performance optimizations
- Bug fixes and stability improvements

## License

This project is provided as-is for personal and educational use.