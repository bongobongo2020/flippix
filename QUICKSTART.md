# FlipPix Quick Start Guide

Get FlipPix running in 3 simple steps!

## Step 1: Install ComfyUI

### Windows (Easiest)
1. Download portable version: https://github.com/comfyanonymous/ComfyUI/releases
2. Extract to `C:\ComfyUI`
3. Done!

### Linux/macOS
```bash
git clone https://github.com/comfyanonymous/ComfyUI.git
cd ComfyUI
python -m venv venv
source venv/bin/activate  # or venv\Scripts\activate on Windows
pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121
pip install -r requirements.txt
```

## Step 2: Run Automated Setup

Copy the setup script to your ComfyUI directory and run it:

### Windows
```cmd
cd C:\ComfyUI
copy \path\to\flippix\scripts\setup-comfyui-windows.bat .
setup-comfyui-windows.bat
```

### Linux/macOS
```bash
cd /path/to/ComfyUI
cp /path/to/flippix/scripts/setup-comfyui.sh .
chmod +x setup-comfyui.sh
./setup-comfyui.sh
```

### Any Platform (Python)
```bash
cd /path/to/ComfyUI
python /path/to/flippix/scripts/setup-comfyui.py
```

**What the script does:**
- Installs 6 custom nodes
- Downloads 13 models (~45GB)
- Takes 30-60 minutes

**Go grab a coffee!** ☕

## Step 3: Start Everything

### Start ComfyUI

**Windows (Portable):**
```cmd
cd C:\ComfyUI
run_nvidia_gpu.bat
```

**Manual Installation:**
```bash
cd ComfyUI
python main.py --highvram
```

Wait for ComfyUI to start, then open http://127.0.0.1:8188 in your browser.

### Start FlipPix

1. Navigate to FlipPix directory
2. Run `publish\FlipPix.UI.exe` (or the executable name in your build)
3. Configure server: IP `127.0.0.1`, Port `8188`
4. Load an image and start processing!

## Verify Installation

Load a workflow in ComfyUI to verify everything works:
1. Open http://127.0.0.1:8188
2. Drag and drop `workflow/qwen-edit-camera-API.json` into the interface
3. All nodes should appear without red errors ✓

## System Requirements

### Minimum
- **GPU**: NVIDIA 12GB VRAM
- **RAM**: 16GB
- **Storage**: 60GB free space
- **OS**: Windows 10/11, Linux, or macOS

### Recommended
- **GPU**: NVIDIA 16GB+ VRAM
- **RAM**: 32GB
- **Storage**: 100GB+ free space (for outputs)

## Troubleshooting

### ComfyUI won't start
```bash
# Check Python version (need 3.10 or 3.11)
python --version

# Check CUDA
nvidia-smi
```

### FlipPix can't connect
1. Verify ComfyUI is running: Open http://127.0.0.1:8188
2. Check Windows Firewall
3. Try restarting ComfyUI

### Out of memory errors
Start ComfyUI with lower VRAM settings:
```bash
python main.py --lowvram
```

### Missing nodes / Red errors
1. Check if custom nodes installed: `ls custom_nodes/`
2. Restart ComfyUI
3. Use ComfyUI-Manager to install missing nodes

### Download failed
Re-run the setup script - it skips existing files and resumes downloads.

## What Each Workflow Does

FlipPix includes 3 AI workflows:

1. **qwen-edit-camera-API.json** - Image Editing
   - Camera angle transformations
   - Low/high angle shots
   - Rotation and perspective changes

2. **video_wan2_2_14B_i2vAPI.json** - Video Generation
   - Image-to-video animation
   - 81 frame videos
   - 16 FPS output

3. **image_z_image-TEXTAPI.json** - Image Generation
   - Text-to-image creation
   - Multiple aspect ratios
   - Fast turbo generation

## Need More Help?

- **📖 Detailed Setup**: [COMFYUI_SETUP.md](COMFYUI_SETUP.md)
- **🤖 Automated Scripts**: [scripts/README.md](scripts/README.md)
- **📁 Main README**: [README.md](README.md)
- **🐛 Report Issues**: https://github.com/bongobongo2020/flippix/issues

## Quick Command Reference

### ComfyUI Commands
```bash
# Start with high VRAM optimization
python main.py --highvram

# Start with low VRAM optimization
python main.py --lowvram

# Start with CPU VAE (saves VRAM)
python main.py --highvram --cpu-vae

# Check installed custom nodes
ls custom_nodes/
```

### Check Downloaded Models
```bash
# CLIP models (~12GB total)
ls -lh models/clip/

# VAE models (~500MB total)
ls -lh models/vae/

# UNET models (~32GB total)
ls -lh models/unet/

# LoRA models (~800MB total)
ls -lh models/loras/qwen/
```

### Manual Model Download (if script fails)
```bash
cd models/clip
wget https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/resolve/main/split_files/text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors
```

## Tips for Best Results

1. **Start small**: Process 1MP images first to test
2. **Use highvram**: If you have 16GB+ VRAM
3. **Close other apps**: Free up VRAM during processing
4. **Update GPU drivers**: Latest NVIDIA drivers recommended
5. **Check workflow settings**: Adjust steps and CFG in workflows for quality/speed balance

## Common Issues

| Issue | Solution |
|-------|----------|
| "CUDA out of memory" | Use `--lowvram` or process smaller images |
| "Model not found" | Verify file is in correct directory with exact name |
| "Unknown node type" | Install missing custom nodes via ComfyUI-Manager |
| "Connection refused" | Start ComfyUI before FlipPix |
| Slow processing | Use `--highvram` flag, close other apps |

---

**Ready to go?** Start with Step 1 above! 🚀
