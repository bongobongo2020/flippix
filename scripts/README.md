# FlipPix ComfyUI Setup Scripts

This directory contains automated setup scripts to download all required custom nodes and models for FlipPix.

## Available Scripts

### 1. setup-comfyui-windows.bat (Windows)
Batch script for Windows users.

**Usage:**
```cmd
cd C:\path\to\ComfyUI
path\to\setup-comfyui-windows.bat
```

### 2. setup-comfyui.sh (Linux/macOS)
Bash script for Linux and macOS users.

**Usage:**
```bash
cd /path/to/ComfyUI
chmod +x /path/to/setup-comfyui.sh
/path/to/setup-comfyui.sh
```

### 3. setup-comfyui.py (Cross-Platform)
Python script that works on all platforms.

**Usage:**
```bash
cd /path/to/ComfyUI
python /path/to/setup-comfyui.py
```

## Prerequisites

Before running any script, ensure you have:

- **Git** installed and in PATH
- **wget** or **curl** installed for downloading files
- **Python 3.10+** (for the Python script)
- **60GB+ free disk space**
- **Stable internet connection**

## What the Scripts Do

All three scripts perform the same tasks:

1. **Create Directory Structure**
   - Creates all necessary model directories
   - Sets up proper folder hierarchy

2. **Install Custom Nodes** (6 repositories)
   - ComfyUI-Manager
   - ComfyUI-QwenImageEdit-MZ
   - rgthree-comfy
   - ComfyUI_Comfyroll_CustomNodes
   - ComfyUI-GGUF
   - ComfyUI-WanVideoGenerator
   - Automatically installs Python dependencies

3. **Download Models** (~45GB)
   - 3 CLIP models (~12GB)
   - 3 VAE models (~500MB)
   - 4 UNET models (~32GB)
   - 2 LoRA models (~800MB)

## Features

- **Resume Support**: If a download is interrupted, restart the script and it will skip already downloaded files
- **Error Handling**: Continues even if some downloads fail, with warnings
- **Progress Information**: Clear progress indicators for each step
- **Automatic Dependency Installation**: Installs Python requirements for custom nodes

## Estimated Time

- **With fast internet (100+ Mbps)**: 30-45 minutes
- **With moderate internet (50 Mbps)**: 45-90 minutes
- **With slow internet (10 Mbps)**: 2-4 hours

## Installation Steps

### Option 1: Copy Script to ComfyUI Directory

1. Copy your preferred script to the ComfyUI root directory
2. Run the script from that location

**Windows:**
```cmd
cd C:\ComfyUI
setup-comfyui-windows.bat
```

**Linux/macOS:**
```bash
cd /path/to/ComfyUI
chmod +x setup-comfyui.sh
./setup-comfyui.sh
```

**Python (Any Platform):**
```bash
cd /path/to/ComfyUI
python setup-comfyui.py
```

### Option 2: Run Script from FlipPix Directory

**Windows:**
```cmd
cd C:\ComfyUI
C:\path\to\flippix\scripts\setup-comfyui-windows.bat
```

**Linux/macOS:**
```bash
cd /path/to/ComfyUI
/path/to/flippix/scripts/setup-comfyui.sh
```

**Python:**
```bash
cd /path/to/ComfyUI
python /path/to/flippix/scripts/setup-comfyui.py
```

## Troubleshooting

### Script Can't Find Git

**Windows:**
- Install Git from https://git-scm.com/
- Make sure "Git from the command line and also from 3rd-party software" is selected during installation
- Restart your terminal after installation

**Linux:**
```bash
sudo apt install git  # Debian/Ubuntu
sudo dnf install git  # Fedora
```

**macOS:**
```bash
brew install git
```

### Script Can't Find wget or curl

**Windows:**
- Download wget from https://eternallybored.org/misc/wget/
- Place wget.exe in C:\Windows\System32 or add to PATH
- Or install via Chocolatey: `choco install wget`

**Linux:**
```bash
sudo apt install wget curl  # Debian/Ubuntu
sudo dnf install wget curl  # Fedora
```

**macOS:**
```bash
brew install wget
# curl is pre-installed on macOS
```

### Download Fails or Times Out

- The script supports resume functionality
- Simply re-run the script and it will skip completed downloads
- For persistent issues, try downloading the model manually from the Hugging Face links in COMFYUI_SETUP.md

### Permission Denied Errors (Linux/macOS)

Make the script executable:
```bash
chmod +x setup-comfyui.sh
```

Or run with bash explicitly:
```bash
bash setup-comfyui.sh
```

### Not Enough Disk Space

The complete setup requires **~60GB**:
- Models: ~45GB
- Custom nodes: ~500MB
- Working space: ~10-15GB

Free up space or use a different drive.

## After Installation

Once the script completes:

1. **Start ComfyUI**
   ```bash
   python main.py --highvram
   ```

2. **Wait for initialization** (first startup takes 1-2 minutes as custom nodes load)

3. **Open browser** to http://127.0.0.1:8188

4. **Test workflows** by dragging FlipPix workflow JSON files into ComfyUI

5. **Verify no red nodes** (all nodes should load without errors)

## Manual Verification

After running the script, verify the installation:

### Check Custom Nodes
```bash
cd ComfyUI/custom_nodes
ls -la
```

You should see 6 directories:
- ComfyUI-Manager
- ComfyUI-QwenImageEdit-MZ
- rgthree-comfy
- ComfyUI_Comfyroll_CustomNodes
- ComfyUI-GGUF
- ComfyUI-WanVideoGenerator

### Check Models

**CLIP Models:**
```bash
ls -lh models/clip/
```
Should show 3 files (~12GB total)

**VAE Models:**
```bash
ls -lh models/vae/
```
Should show 3 files (~500MB total)

**UNET Models:**
```bash
ls -lh models/unet/
```
Should show 4 files (~32GB total)

**LoRA Models:**
```bash
ls -lh models/loras/qwen/
```
Should show 2 files (~800MB total)

## Support

If you encounter issues not covered here:

1. Check the main [COMFYUI_SETUP.md](../COMFYUI_SETUP.md) for detailed troubleshooting
2. Review the script output for specific error messages
3. Visit the FlipPix GitHub issues: https://github.com/bongobongo2020/flippix/issues
4. Check ComfyUI documentation: https://docs.comfy.org/

## Notes

- The scripts will **never overwrite** existing files - they skip files that already exist
- The scripts install **all** models needed for all three FlipPix workflows (Image Editing, Video Generation, and Image Generation)
- Custom node repositories may change URLs - if a clone fails, check for repository name changes
- Some model URLs may change - if a download fails, check [COMFYUI_SETUP.md](../COMFYUI_SETUP.md) for updated links

## License

These scripts are provided for convenience in setting up FlipPix. Please respect the licenses of ComfyUI, custom nodes, and models being downloaded.
