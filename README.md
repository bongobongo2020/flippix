# FlipPix

![FlipPix Logo](flippix.png)

AI-powered image processing application that transforms image perspectives and camera angles using Qwen Image Edit models via ComfyUI.

## Overview

FlipPix processes images to apply camera angle transformations, perspective changes, and visual modifications. It requires a local ComfyUI server with specific custom nodes and models to function.

## Demo

https://github.com/user-attachments/assets/flippix.mp4

*Watch the demo video above to see FlipPix in action, or [download it directly](flippix.mp4).*

## Prerequisites

### System Requirements
- Windows x64 operating system
- .NET 8.0 runtime (included in self-contained build)
- Minimum 16GB RAM (32GB recommended for processing)
- NVIDIA GPU with 12GB+ VRAM recommended

### ComfyUI Setup

FlipPix requires **ComfyUI running on localhost** (default: `http://127.0.0.1:8188`).

#### 1. Install ComfyUI (if not already installed)

```bash
# Clone ComfyUI repository
git clone https://github.com/comfyanonymous/ComfyUI.git
cd ComfyUI

# Install dependencies
pip install -r requirements.txt
```

#### 2. Install Required Custom Nodes

FlipPix uses the following custom nodes. Install them in `ComfyUI/custom_nodes/`:

**GGUF Support (for Qwen models)**
```bash
cd ComfyUI/custom_nodes
git clone https://github.com/city96/ComfyUI-GGUF.git
```

**Qwen Image Edit Nodes**
```bash
cd ComfyUI/custom_nodes
git clone https://github.com/MinusZoneAI/ComfyUI-QwenImageEdit-MZ.git
```

**rgthree Custom Nodes (for Power Lora Loader)**
```bash
cd ComfyUI/custom_nodes
git clone https://github.com/rgthree/rgthree-comfy.git
```

**Additional Utility Nodes**
```bash
cd ComfyUI/custom_nodes
git clone https://github.com/Suzie1/ComfyUI_Comfyroll_CustomNodes.git
```

After installing custom nodes, restart ComfyUI or click "Restart" in the ComfyUI Manager.

#### 3. Download Required Models

Place the following models in their respective directories:

**CLIP Model**
- File: `qwen_2.5_vl_7b_fp8_scaled.safetensors`
- Location: `ComfyUI/models/clip/`
- Source: [Hugging Face - Qwen Models](https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/blob/main/split_files/text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors)

**VAE Model**
- File: `qwen_image_vae.safetensors`
- Location: `ComfyUI/models/vae/`
- Source: [Hugging Face - Qwen VAE](https://huggingface.co/QuantStack/Qwen-Image-GGUF/blob/main/VAE/Qwen_Image-VAE.safetensors)

**UNET Model (GGUF)**
- File: `Qwen-Image-Edit-2509-Q8_0.gguf`
- Location: `ComfyUI/models/unet/qwen/`
- Source: [Hugging Face - Qwen Image Edit](https://huggingface.co/QuantStack/Qwen-Image-Edit-2509-GGUF/blob/main/Qwen-Image-Edit-2509-Q8_0.gguf)

**LoRA Models**
- File: `Qwen-Image-Lightning-8steps-V2.0.safetensors`
- Location: `ComfyUI/models/loras/qwen/`
Source: [Hugging Face - Qwen LoRAs](https://huggingface.co/lightx2v/Qwen-Image-Lightning/blob/main/Qwen-Image-Lightning-8steps-V2.0.safetensors)
- File: `mult-angles.safetensors` (Rename the file to mult-angles.safetensors )
- Location: `ComfyUI/models/loras/qwen/`
- Source: [Hugging Face - Qwen LoRAs](https://huggingface.co/dx8152/Qwen-Edit-2509-Multiple-angles/blob/main/%E9%95%9C%E5%A4%B4%E8%BD%AC%E6%8D%A2.safetensors)

#### 4. Start ComfyUI

```bash
# From ComfyUI directory
python main.py

# Or with GPU optimization
python main.py --highvram
```

Verify ComfyUI is running by accessing `http://127.0.0.1:8188` in your browser.

#### 5. Verify Workflow Compatibility

Open the workflow file in ComfyUI to verify all nodes load correctly:
- File location: `/mnt/c/Users/x2/Documents/projects/wan-exp/flippix/workflow/qwen-edit-camera-API.json`
- Drag and drop the JSON file into ComfyUI interface
- Check that all nodes appear without red errors

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