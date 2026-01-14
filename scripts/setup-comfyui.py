#!/usr/bin/env python3
"""
FlipPix ComfyUI Automatic Setup Script (Cross-Platform)
Place this script in your ComfyUI root directory and run it
Requires: git, internet connection
"""

import os
import sys
import subprocess
import platform
from pathlib import Path
from typing import List, Tuple

# Color codes for terminal output
class Colors:
    HEADER = '\033[95m'
    OKBLUE = '\033[94m'
    OKCYAN = '\033[96m'
    OKGREEN = '\033[92m'
    WARNING = '\033[93m'
    FAIL = '\033[91m'
    ENDC = '\033[0m'
    BOLD = '\033[1m'

def print_header(text: str):
    print(f"\n{Colors.HEADER}{'='*50}{Colors.ENDC}")
    print(f"{Colors.HEADER}{text}{Colors.ENDC}")
    print(f"{Colors.HEADER}{'='*50}{Colors.ENDC}\n")

def print_success(text: str):
    print(f"{Colors.OKGREEN}✓ {text}{Colors.ENDC}")

def print_warning(text: str):
    print(f"{Colors.WARNING}⚠ {text}{Colors.ENDC}")

def print_error(text: str):
    print(f"{Colors.FAIL}✗ {text}{Colors.ENDC}")

def print_info(text: str):
    print(f"{Colors.OKCYAN}ℹ {text}{Colors.ENDC}")

def check_command(command: str) -> bool:
    """Check if a command is available in PATH"""
    try:
        subprocess.run([command, '--version'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        return True
    except FileNotFoundError:
        return False

def run_command(command: List[str], cwd: str = None) -> bool:
    """Run a command and return success status"""
    try:
        result = subprocess.run(command, cwd=cwd, check=True, capture_output=True, text=True)
        return True
    except subprocess.CalledProcessError as e:
        print_error(f"Command failed: {' '.join(command)}")
        if e.stderr:
            print(f"Error: {e.stderr}")
        return False

def download_file(url: str, output_path: str, use_wget: bool = True) -> bool:
    """Download a file using wget or curl"""
    if Path(output_path).exists():
        print_info(f"File already exists, skipping: {Path(output_path).name}")
        return True

    print_info(f"Downloading {Path(output_path).name}...")

    if use_wget:
        command = ['wget', '-c', '-O', output_path, url]
    else:
        command = ['curl', '-L', '-C', '-', '-o', output_path, url]

    return run_command(command)

def clone_repo(url: str, target_dir: str) -> bool:
    """Clone a git repository"""
    if Path(target_dir).exists():
        print_info(f"{Path(target_dir).name} already exists, skipping...")
        return True

    print_info(f"Cloning {Path(target_dir).name}...")
    if run_command(['git', 'clone', url, target_dir]):
        print_success(f"{Path(target_dir).name} installed")

        # Install requirements if they exist
        requirements_file = Path(target_dir) / 'requirements.txt'
        if requirements_file.exists():
            print_info("Installing Python dependencies...")
            run_command([sys.executable, '-m', 'pip', 'install', '-r', str(requirements_file)])

        return True
    else:
        print_warning(f"Failed to clone {Path(target_dir).name}")
        return False

def main():
    print_header("FlipPix ComfyUI Automatic Setup Script")

    print("This script will:")
    print("- Install 6 custom nodes")
    print("- Download 13 model files (~45GB)")
    print("- Create necessary directories")
    print("\nEstimated time: 30-60 minutes depending on your connection")
    print("Required space: ~60GB")
    print("")

    input("Press Enter to continue or Ctrl+C to cancel...")

    # Check if we're in the ComfyUI directory
    if not (Path('models').exists() and Path('custom_nodes').exists()):
        print_error("models or custom_nodes folder not found!")
        print_error("Please place this script in your ComfyUI root directory.")
        sys.exit(1)

    # Check for required tools
    if not check_command('git'):
        print_error("Git is not installed or not in PATH")
        print_error("Please install Git from https://git-scm.com/")
        sys.exit(1)

    # Check for download tool
    use_wget = check_command('wget')
    use_curl = check_command('curl')

    if not use_wget and not use_curl:
        print_error("Neither wget nor curl found!")
        print_error("Please install wget or curl")
        sys.exit(1)

    download_tool = "wget" if use_wget else "curl"
    print_success(f"Using {download_tool} for downloads")

    # Step 1: Create directories
    print_header("Step 1: Creating Model Directories")

    directories = [
        'models/clip',
        'models/vae',
        'models/unet',
        'models/unet/qwen',
        'models/loras',
        'models/loras/qwen',
    ]

    for directory in directories:
        Path(directory).mkdir(parents=True, exist_ok=True)

    print_success("Directories created successfully!")

    # Step 2: Install custom nodes
    print_header("Step 2: Installing Custom Nodes")

    custom_nodes = [
        ("https://github.com/ltdrdata/ComfyUI-Manager.git", "custom_nodes/ComfyUI-Manager"),
        ("https://github.com/MinusZoneAI/ComfyUI-QwenImageEdit-MZ.git", "custom_nodes/ComfyUI-QwenImageEdit-MZ"),
        ("https://github.com/rgthree/rgthree-comfy.git", "custom_nodes/rgthree-comfy"),
        ("https://github.com/Suzie1/ComfyUI_Comfyroll_CustomNodes.git", "custom_nodes/ComfyUI_Comfyroll_CustomNodes"),
        ("https://github.com/city96/ComfyUI-GGUF.git", "custom_nodes/ComfyUI-GGUF"),
        ("https://github.com/chaojie/ComfyUI-WanVideoGenerator.git", "custom_nodes/ComfyUI-WanVideoGenerator"),
    ]

    for i, (url, target) in enumerate(custom_nodes, 1):
        print(f"\n[{i}/{len(custom_nodes)}] Installing {Path(target).name}...")
        clone_repo(url, target)

    # Step 3: Download models
    print_header("Step 3: Downloading Models")
    print("This will download approximately 45GB of model files.")
    print("Downloads will be skipped if files already exist.\n")
    input("Press Enter to continue...")

    # CLIP Models
    print_header("Downloading CLIP Models (~12GB)")

    clip_models = [
        ("https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/resolve/main/split_files/text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors",
         "models/clip/qwen_2.5_vl_7b_fp8_scaled.safetensors", "~4.2GB"),
        ("https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors",
         "models/clip/umt5_xxl_fp8_e4m3fn_scaled.safetensors", "~4.9GB"),
        ("https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/qwen_3_4b.safetensors",
         "models/clip/qwen_3_4b.safetensors", "~2.5GB"),
    ]

    for i, (url, output, size) in enumerate(clip_models, 1):
        print(f"\n[{i}/{len(clip_models)}] Downloading {Path(output).name} ({size})...")
        download_file(url, output, use_wget)

    # VAE Models
    print_header("Downloading VAE Models (~500MB)")

    vae_models = [
        ("https://huggingface.co/QuantStack/Qwen-Image-GGUF/resolve/main/VAE/Qwen_Image-VAE.safetensors",
         "models/vae/qwen_image_vae.safetensors", "~168MB"),
        ("https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/vae/wan_2.1_vae.safetensors",
         "models/vae/wan_2.1_vae.safetensors", "~168MB"),
        ("https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/ae.safetensors",
         "models/vae/ae.safetensors", "~168MB"),
    ]

    for i, (url, output, size) in enumerate(vae_models, 1):
        print(f"\n[{i}/{len(vae_models)}] Downloading {Path(output).name} ({size})...")
        download_file(url, output, use_wget)

    # UNET Models
    print_header("Downloading UNET Models (~32GB)")

    unet_models = [
        ("https://huggingface.co/Kijai/Qwen-Edit-2509_safetensors/resolve/main/Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors",
         "models/unet/Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors", "~3.8GB"),
        ("https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/diffusion_models/wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors",
         "models/unet/wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors", "~11GB"),
        ("https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/diffusion_models/wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors",
         "models/unet/wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors", "~11GB"),
        ("https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/z_image_turbo_bf16.safetensors",
         "models/unet/z_image_turbo_bf16.safetensors", "~5.8GB"),
    ]

    for i, (url, output, size) in enumerate(unet_models, 1):
        print(f"\n[{i}/{len(unet_models)}] Downloading {Path(output).name} ({size})...")
        if "11GB" in size:
            print_warning("This is a large file and will take a while...")
        download_file(url, output, use_wget)

    # LoRA Models
    print_header("Downloading LoRA Models (~800MB)")

    lora_models = [
        ("https://huggingface.co/lightx2v/Qwen-Image-Lightning/resolve/main/Qwen-Image-Lightning-8steps-V2.0.safetensors",
         "models/loras/qwen/Qwen-Image-Lightning-8steps-V2.0.safetensors", "~383MB"),
        ("https://huggingface.co/dx8152/Qwen-Edit-2509-Multiple-angles/resolve/main/%E9%95%9C%E5%A4%B4%E8%BD%AC%E6%8D%A2.safetensors",
         "models/loras/qwen/mult-angles.safetensors", "~383MB"),
    ]

    for i, (url, output, size) in enumerate(lora_models, 1):
        print(f"\n[{i}/{len(lora_models)}] Downloading {Path(output).name} ({size})...")
        download_file(url, output, use_wget)

    # Completion message
    print_header("Setup Complete!")
    print("All custom nodes and models have been downloaded.\n")
    print("Next steps:")
    print("1. Start ComfyUI (python main.py --highvram)")
    print("2. Wait for all custom nodes to initialize")
    print("3. Open http://127.0.0.1:8188 in your browser")
    print("4. Load the FlipPix workflow files to verify setup\n")
    print("If you encounter any issues:")
    print("- Check the COMFYUI_SETUP.md for troubleshooting")
    print("- Verify all model files downloaded completely")
    print("- Restart ComfyUI to load all custom nodes\n")

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n\nSetup cancelled by user.")
        sys.exit(0)
    except Exception as e:
        print_error(f"An error occurred: {str(e)}")
        sys.exit(1)
