# ComfyUI Setup Guide for FlipPix

This guide will walk you through setting up ComfyUI from scratch to run FlipPix, including all required custom nodes, models, and workflows.

## 🚀 Quick Setup (Automated)

**Want to skip manual installation?** Use our automated setup scripts that download everything for you!

See **[scripts/README.md](scripts/README.md)** for automated installation:
- **Windows**: `setup-comfyui-windows.bat`
- **Linux/macOS**: `setup-comfyui.sh`
- **Cross-Platform**: `setup-comfyui.py`

The scripts automatically install all 6 custom nodes and download all 13 models (~45GB) in 30-60 minutes.

---

## Manual Setup Guide

Prefer to install manually? Follow the detailed instructions below.

## Table of Contents
- [System Requirements](#system-requirements)
- [Step 1: Install ComfyUI](#step-1-install-comfyui)
- [Step 2: Install Custom Nodes](#step-2-install-custom-nodes)
- [Step 3: Download Models](#step-3-download-models)
- [Step 4: Verify Installation](#step-4-verify-installation)
- [Step 5: Start ComfyUI](#step-5-start-comfyui)
- [Troubleshooting](#troubleshooting)

## System Requirements

### Hardware
- **GPU**: NVIDIA GPU with 12GB+ VRAM (16GB+ recommended for video generation)
- **RAM**: 16GB minimum (32GB+ recommended)
- **Storage**: 60GB+ free space for models and ComfyUI

### Software
- **OS**: Windows 10/11, Linux, or macOS
- **Python**: 3.10 or 3.11 (recommended: 3.11.x)
- **Git**: For cloning repositories
- **CUDA**: 11.8 or 12.1 (for NVIDIA GPUs)

---

## Step 1: Install ComfyUI

### Option A: Portable Windows Installation (Recommended for Windows)

1. Download the latest portable version:
   - Visit: https://github.com/comfyanonymous/ComfyUI/releases
   - Download `ComfyUI_windows_portable_nvidia_cu121_or_cpu.7z` or the latest version
   - Extract to `C:\ComfyUI` (or your preferred location)

2. Test the installation:
   ```cmd
   cd C:\ComfyUI
   run_nvidia_gpu.bat
   ```

### Option B: Manual Installation (All Platforms)

1. **Install Python 3.11**
   - Windows: Download from https://www.python.org/downloads/
   - Linux: `sudo apt install python3.11 python3.11-venv`
   - macOS: `brew install python@3.11`

2. **Clone ComfyUI**
   ```bash
   git clone https://github.com/comfyanonymous/ComfyUI.git
   cd ComfyUI
   ```

3. **Create virtual environment and install dependencies**
   ```bash
   python -m venv venv

   # Windows
   venv\Scripts\activate

   # Linux/macOS
   source venv/bin/activate

   # Install PyTorch with CUDA support
   pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121

   # Install ComfyUI requirements
   pip install -r requirements.txt
   ```

---

## Step 2: Install Custom Nodes

Navigate to the `custom_nodes` directory and install the following nodes:

```bash
cd ComfyUI/custom_nodes
```

### 2.1 ComfyUI Manager (Highly Recommended)
```bash
git clone https://github.com/ltdrdata/ComfyUI-Manager.git
```
The ComfyUI Manager provides a UI for installing missing custom nodes automatically.

### 2.2 Qwen Image Edit Nodes (Required for Image Processing)
```bash
git clone https://github.com/MinusZoneAI/ComfyUI-QwenImageEdit-MZ.git
```
**Purpose**: Provides TextEncodeQwenImageEditPlus node for Qwen image editing
**Link**: https://github.com/MinusZoneAI/ComfyUI-QwenImageEdit-MZ

### 2.3 rgthree Custom Nodes (Required for LoRA Loading)
```bash
git clone https://github.com/rgthree/rgthree-comfy.git
```
**Purpose**: Provides Power Lora Loader node for loading multiple LoRAs
**Link**: https://github.com/rgthree/rgthree-comfy

### 2.4 ComfyUI_Comfyroll_CustomNodes (Required for Utility Nodes)
```bash
git clone https://github.com/Suzie1/ComfyUI_Comfyroll_CustomNodes.git
```
**Purpose**: Provides ImageScaleToTotalPixels, CR Aspect Ratio, and TextBox nodes
**Link**: https://github.com/Suzie1/ComfyUI_Comfyroll_CustomNodes

### 2.5 ComfyUI-GGUF (Optional - for GGUF model support)
```bash
git clone https://github.com/city96/ComfyUI-GGUF.git
```
**Purpose**: Enables loading GGUF quantized models (alternative to safetensors)
**Link**: https://github.com/city96/ComfyUI-GGUF

### 2.6 ComfyUI-WanVideoGenerator (Required for Video Generation)
```bash
git clone https://github.com/chaojie/ComfyUI-WanVideoGenerator.git
```
**Purpose**: Provides WanImageToVideo, CreateVideo, and SaveVideo nodes
**Link**: https://github.com/chaojie/ComfyUI-WanVideoGenerator
**Note**: If this repository doesn't exist, search for "ComfyUI Wan" or "ComfyUI-Wanx" alternatives

### 2.7 Install Dependencies for Custom Nodes
```bash
cd ComfyUI/custom_nodes/ComfyUI-QwenImageEdit-MZ
pip install -r requirements.txt

cd ../ComfyUI_Comfyroll_CustomNodes
pip install -r requirements.txt

cd ../ComfyUI-WanVideoGenerator
pip install -r requirements.txt
```

---

## Step 3: Download Models

Create the necessary model directories if they don't exist:

```bash
# From ComfyUI root directory
mkdir -p models/clip
mkdir -p models/vae
mkdir -p models/unet
mkdir -p models/unet/qwen
mkdir -p models/loras
mkdir -p models/loras/qwen
```

### 3.1 CLIP Models

#### Qwen 2.5 VL CLIP (for Image Editing)
- **File**: `qwen_2.5_vl_7b_fp8_scaled.safetensors`
- **Size**: ~4.2GB
- **Location**: `ComfyUI/models/clip/`
- **Download**: https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/blob/main/split_files/text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors

```bash
# Download using wget or browser
cd ComfyUI/models/clip
wget https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/resolve/main/split_files/text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors
```

#### UMT5 CLIP (for Video Generation)
- **File**: `umt5_xxl_fp8_e4m3fn_scaled.safetensors`
- **Size**: ~4.9GB
- **Location**: `ComfyUI/models/clip/`
- **Download**: https://huggingface.co/Kijai/WanVideoGenerator/blob/main/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors

```bash
cd ComfyUI/models/clip
wget https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors
```

#### Qwen 3 CLIP (for Z-Image Generation)
- **File**: `qwen_3_4b.safetensors`
- **Size**: ~2.5GB
- **Location**: `ComfyUI/models/clip/`
- **Download**: https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/blob/main/qwen_3_4b.safetensors

```bash
cd ComfyUI/models/clip
wget https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/qwen_3_4b.safetensors
```

### 3.2 VAE Models

#### Qwen Image VAE (for Image Editing)
- **File**: `qwen_image_vae.safetensors`
- **Size**: ~168MB
- **Location**: `ComfyUI/models/vae/`
- **Download**: https://huggingface.co/QuantStack/Qwen-Image-GGUF/blob/main/VAE/Qwen_Image-VAE.safetensors

```bash
cd ComfyUI/models/vae
wget https://huggingface.co/QuantStack/Qwen-Image-GGUF/resolve/main/VAE/Qwen_Image-VAE.safetensors -O qwen_image_vae.safetensors
```

#### Wan VAE (for Video Generation)
- **File**: `wan_2.1_vae.safetensors`
- **Size**: ~168MB
- **Location**: `ComfyUI/models/vae/`
- **Download**: https://huggingface.co/Kijai/WanVideoGenerator/blob/main/vae/wan_2.1_vae.safetensors

```bash
cd ComfyUI/models/vae
wget https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/vae/wan_2.1_vae.safetensors
```

#### Z-Image VAE (for Z-Image Generation)
- **File**: `ae.safetensors`
- **Size**: ~168MB
- **Location**: `ComfyUI/models/vae/`
- **Download**: https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/blob/main/ae.safetensors

```bash
cd ComfyUI/models/vae
wget https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/ae.safetensors
```

### 3.3 UNET Models (Diffusion Models)

#### Qwen Image Edit UNET (for Image Editing)
- **File**: `Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors`
- **Size**: ~3.8GB
- **Location**: `ComfyUI/models/unet/`
- **Download**: https://huggingface.co/Kijai/Qwen-Edit-2509_safetensors/blob/main/Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors

```bash
cd ComfyUI/models/unet
wget https://huggingface.co/Kijai/Qwen-Edit-2509_safetensors/resolve/main/Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors
```

**Alternative GGUF Version** (if you prefer GGUF):
- **File**: `Qwen-Image-Edit-2509-Q8_0.gguf`
- **Location**: `ComfyUI/models/unet/qwen/`
- **Download**: https://huggingface.co/QuantStack/Qwen-Image-Edit-2509-GGUF/blob/main/Qwen-Image-Edit-2509-Q8_0.gguf

```bash
mkdir -p ComfyUI/models/unet/qwen
cd ComfyUI/models/unet/qwen
wget https://huggingface.co/QuantStack/Qwen-Image-Edit-2509-GGUF/resolve/main/Qwen-Image-Edit-2509-Q8_0.gguf
```

#### Wan Video UNETs (for Video Generation)

**High Noise Model**:
- **File**: `wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors`
- **Size**: ~11GB
- **Location**: `ComfyUI/models/unet/`
- **Download**: https://huggingface.co/Kijai/WanVideoGenerator/blob/main/diffusion_models/wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors

```bash
cd ComfyUI/models/unet
wget https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/diffusion_models/wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors
```

**Low Noise Model**:
- **File**: `wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors`
- **Size**: ~11GB
- **Location**: `ComfyUI/models/unet/`
- **Download**: https://huggingface.co/Kijai/WanVideoGenerator/blob/main/diffusion_models/wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors

```bash
cd ComfyUI/models/unet
wget https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/diffusion_models/wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors
```

#### Z-Image UNET (for Image Generation)
- **File**: `z_image_turbo_bf16.safetensors`
- **Size**: ~5.8GB
- **Location**: `ComfyUI/models/unet/`
- **Download**: https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/blob/main/z_image_turbo_bf16.safetensors

```bash
cd ComfyUI/models/unet
wget https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/z_image_turbo_bf16.safetensors
```

### 3.4 LoRA Models (for Image Editing)

Create LoRA directory:
```bash
mkdir -p ComfyUI/models/loras/qwen
cd ComfyUI/models/loras/qwen
```

#### Qwen Image Lightning LoRA
- **File**: `Qwen-Image-Lightning-8steps-V2.0.safetensors`
- **Size**: ~383MB
- **Location**: `ComfyUI/models/loras/qwen/`
- **Download**: https://huggingface.co/lightx2v/Qwen-Image-Lightning/blob/main/Qwen-Image-Lightning-8steps-V2.0.safetensors

```bash
wget https://huggingface.co/lightx2v/Qwen-Image-Lightning/resolve/main/Qwen-Image-Lightning-8steps-V2.0.safetensors
```

#### Multiple Angles LoRA
- **File**: `mult-angles.safetensors`
- **Original Name**: `镜头转换.safetensors` (Rename after download)
- **Size**: ~383MB
- **Location**: `ComfyUI/models/loras/qwen/`
- **Download**: https://huggingface.co/dx8152/Qwen-Edit-2509-Multiple-angles/blob/main/%E9%95%9C%E5%A4%B4%E8%BD%AC%E6%8D%A2.safetensors

```bash
wget "https://huggingface.co/dx8152/Qwen-Edit-2509-Multiple-angles/resolve/main/%E9%95%9C%E5%A4%B4%E8%BD%AC%E6%8D%A2.safetensors" -O mult-angles.safetensors
```

---

## Step 4: Verify Installation

### 4.1 Check Directory Structure

Your ComfyUI directory should look like this:

```
ComfyUI/
├── models/
│   ├── clip/
│   │   ├── qwen_2.5_vl_7b_fp8_scaled.safetensors
│   │   ├── umt5_xxl_fp8_e4m3fn_scaled.safetensors
│   │   └── qwen_3_4b.safetensors
│   ├── vae/
│   │   ├── qwen_image_vae.safetensors
│   │   ├── wan_2.1_vae.safetensors
│   │   └── ae.safetensors
│   ├── unet/
│   │   ├── Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors
│   │   ├── wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors
│   │   ├── wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors
│   │   ├── z_image_turbo_bf16.safetensors
│   │   └── qwen/
│   │       └── Qwen-Image-Edit-2509-Q8_0.gguf (optional)
│   └── loras/
│       └── qwen/
│           ├── Qwen-Image-Lightning-8steps-V2.0.safetensors
│           └── mult-angles.safetensors
└── custom_nodes/
    ├── ComfyUI-Manager/
    ├── ComfyUI-QwenImageEdit-MZ/
    ├── rgthree-comfy/
    ├── ComfyUI_Comfyroll_CustomNodes/
    ├── ComfyUI-GGUF/ (optional)
    └── ComfyUI-WanVideoGenerator/
```

### 4.2 Test Workflows

1. Copy the workflow files from FlipPix to a convenient location
2. Start ComfyUI (see Step 5)
3. Open ComfyUI in browser: `http://127.0.0.1:8188`
4. Drag and drop each workflow JSON file into the ComfyUI interface:
   - `workflow/qwen-edit-camera-API.json` - Image editing workflow
   - `workflow/video_wan2_2_14B_i2vAPI.json` - Video generation workflow
   - `workflow/image_z_image-TEXTAPI.json` - Z-Image generation workflow
5. Check that all nodes load without red error markers

---

## Step 5: Start ComfyUI

### Windows (Portable)
```cmd
cd C:\ComfyUI
run_nvidia_gpu.bat
```

### Manual Installation

**Windows**:
```cmd
cd ComfyUI
venv\Scripts\activate
python main.py --highvram
```

**Linux/macOS**:
```bash
cd ComfyUI
source venv/bin/activate
python main.py --highvram
```

### Recommended Command Line Arguments

For high VRAM GPUs (16GB+):
```bash
python main.py --highvram
```

For medium VRAM GPUs (12-16GB):
```bash
python main.py --normalvram
```

For low VRAM GPUs (8-12GB):
```bash
python main.py --lowvram
```

For extreme optimization (slower but uses less VRAM):
```bash
python main.py --novram --cpu-vae
```

### Access ComfyUI

Once started, open your browser and navigate to:
```
http://127.0.0.1:8188
```

You should see the ComfyUI interface.

---

## Troubleshooting

### Missing Custom Nodes

**Problem**: Red nodes or "Unknown node type" errors

**Solution**:
1. Install ComfyUI-Manager if you haven't already
2. Restart ComfyUI
3. Click "Manager" button in ComfyUI interface
4. Click "Install Missing Custom Nodes"
5. Follow the prompts to install missing nodes
6. Restart ComfyUI again

### Model Not Found Errors

**Problem**: "Model not found" or "Failed to load model" errors

**Solution**:
1. Verify the model file is in the correct directory
2. Check file names match exactly (including case sensitivity)
3. Verify the file downloaded completely (check file size)
4. Re-download corrupt files
5. Check available disk space

### Out of Memory (CUDA OOM) Errors

**Problem**: "RuntimeError: CUDA out of memory"

**Solution**:
1. Close other GPU-intensive applications
2. Use `--lowvram` or `--novram` flags
3. Process smaller images/videos
4. Enable CPU offloading: `--cpu-vae`
5. Consider upgrading GPU VRAM

### ComfyUI Won't Start

**Problem**: ComfyUI crashes on startup

**Solution**:
1. Check Python version (3.10 or 3.11 required)
2. Verify CUDA installation: `nvidia-smi`
3. Reinstall PyTorch:
   ```bash
   pip uninstall torch torchvision torchaudio
   pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121
   ```
4. Check ComfyUI console output for specific errors
5. Try starting with `--cpu` flag to test without GPU

### FlipPix Can't Connect to ComfyUI

**Problem**: FlipPix shows "Connection failed" or similar errors

**Solution**:
1. Verify ComfyUI is running: Open `http://127.0.0.1:8188` in browser
2. Check Windows Firewall isn't blocking port 8188
3. Verify no other service is using port 8188:
   ```cmd
   netstat -ano | findstr :8188
   ```
4. Try restarting ComfyUI
5. In FlipPix, verify server settings: IP `127.0.0.1`, Port `8188`

### Slow Processing / Performance Issues

**Problem**: Processing takes extremely long

**Solution**:
1. Ensure you're using `--highvram` flag for high-VRAM GPUs
2. Check GPU utilization: `nvidia-smi`
3. Verify models loaded on GPU (not CPU fallback)
4. Close background applications
5. Update GPU drivers
6. Consider using FP8 quantized models for faster inference

### Git Clone Fails

**Problem**: Unable to clone repositories

**Solution**:
1. Verify Git is installed: `git --version`
2. Check internet connection
3. Try downloading as ZIP instead and extracting to custom_nodes:
   - Visit the GitHub repository in browser
   - Click "Code" → "Download ZIP"
   - Extract to `ComfyUI/custom_nodes/`

---

## Storage Requirements Summary

Total storage needed: **~60GB**

- ComfyUI installation: ~2GB
- Custom nodes: ~500MB
- Models:
  - CLIP models: ~12GB
  - VAE models: ~500MB
  - UNET models: ~32GB
  - LoRA models: ~800MB
- Working space: ~10GB (for input/output files)

---

## Useful Resources

- ComfyUI Documentation: https://docs.comfy.org/
- ComfyUI GitHub: https://github.com/comfyanonymous/ComfyUI
- ComfyUI Community Forum: https://www.comfyui.org/
- Hugging Face Models: https://huggingface.co/models
- ComfyUI Workflow Sharing: https://openart.ai/workflows

---

## Getting Help

If you encounter issues not covered in this guide:

1. Check the ComfyUI console output for detailed error messages
2. Visit the ComfyUI GitHub Issues: https://github.com/comfyanonymous/ComfyUI/issues
3. Join the ComfyUI Discord community
4. Check FlipPix GitHub issues: https://github.com/bongobongo2020/flippix/issues

---

## License

This setup guide is provided for educational and personal use with FlipPix. Please respect the licenses of ComfyUI, custom nodes, and models used.
