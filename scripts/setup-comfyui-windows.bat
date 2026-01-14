@echo off
REM FlipPix ComfyUI Automatic Setup Script for Windows
REM Place this script in your ComfyUI root directory and run it
REM Requires: Git, wget or curl, and internet connection

echo ========================================
echo FlipPix ComfyUI Automatic Setup Script
echo ========================================
echo.
echo This script will:
echo - Install 6 custom nodes
echo - Download 13 model files (~45GB)
echo - Create necessary directories
echo.
echo Estimated time: 30-60 minutes depending on your connection
echo Required space: ~60GB
echo.
pause

REM Check if we're in the ComfyUI directory
if not exist "models" (
    echo ERROR: models folder not found!
    echo Please place this script in your ComfyUI root directory.
    pause
    exit /b 1
)

if not exist "custom_nodes" (
    echo ERROR: custom_nodes folder not found!
    echo Please place this script in your ComfyUI root directory.
    pause
    exit /b 1
)

echo.
echo ========================================
echo Step 1: Creating Model Directories
echo ========================================
echo.

mkdir models\clip 2>nul
mkdir models\vae 2>nul
mkdir models\unet 2>nul
mkdir models\unet\qwen 2>nul
mkdir models\loras 2>nul
mkdir models\loras\qwen 2>nul

echo Directories created successfully!

echo.
echo ========================================
echo Step 2: Installing Custom Nodes
echo ========================================
echo.

cd custom_nodes

REM Check if git is available
git --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: Git is not installed or not in PATH
    echo Please install Git from https://git-scm.com/
    pause
    exit /b 1
)

echo [1/6] Installing ComfyUI-Manager...
if exist "ComfyUI-Manager" (
    echo ComfyUI-Manager already exists, skipping...
) else (
    git clone https://github.com/ltdrdata/ComfyUI-Manager.git
    if errorlevel 1 (
        echo WARNING: Failed to clone ComfyUI-Manager
    ) else (
        echo SUCCESS: ComfyUI-Manager installed
    )
)

echo.
echo [2/6] Installing ComfyUI-QwenImageEdit-MZ...
if exist "ComfyUI-QwenImageEdit-MZ" (
    echo ComfyUI-QwenImageEdit-MZ already exists, skipping...
) else (
    git clone https://github.com/MinusZoneAI/ComfyUI-QwenImageEdit-MZ.git
    if errorlevel 1 (
        echo WARNING: Failed to clone ComfyUI-QwenImageEdit-MZ
    ) else (
        echo SUCCESS: ComfyUI-QwenImageEdit-MZ installed
        if exist "ComfyUI-QwenImageEdit-MZ\requirements.txt" (
            echo Installing dependencies...
            pip install -r ComfyUI-QwenImageEdit-MZ\requirements.txt
        )
    )
)

echo.
echo [3/6] Installing rgthree-comfy...
if exist "rgthree-comfy" (
    echo rgthree-comfy already exists, skipping...
) else (
    git clone https://github.com/rgthree/rgthree-comfy.git
    if errorlevel 1 (
        echo WARNING: Failed to clone rgthree-comfy
    ) else (
        echo SUCCESS: rgthree-comfy installed
    )
)

echo.
echo [4/6] Installing ComfyUI_Comfyroll_CustomNodes...
if exist "ComfyUI_Comfyroll_CustomNodes" (
    echo ComfyUI_Comfyroll_CustomNodes already exists, skipping...
) else (
    git clone https://github.com/Suzie1/ComfyUI_Comfyroll_CustomNodes.git
    if errorlevel 1 (
        echo WARNING: Failed to clone ComfyUI_Comfyroll_CustomNodes
    ) else (
        echo SUCCESS: ComfyUI_Comfyroll_CustomNodes installed
        if exist "ComfyUI_Comfyroll_CustomNodes\requirements.txt" (
            echo Installing dependencies...
            pip install -r ComfyUI_Comfyroll_CustomNodes\requirements.txt
        )
    )
)

echo.
echo [5/6] Installing ComfyUI-GGUF...
if exist "ComfyUI-GGUF" (
    echo ComfyUI-GGUF already exists, skipping...
) else (
    git clone https://github.com/city96/ComfyUI-GGUF.git
    if errorlevel 1 (
        echo WARNING: Failed to clone ComfyUI-GGUF
    ) else (
        echo SUCCESS: ComfyUI-GGUF installed
    )
)

echo.
echo [6/6] Installing ComfyUI-WanVideoGenerator...
if exist "ComfyUI-WanVideoGenerator" (
    echo ComfyUI-WanVideoGenerator already exists, skipping...
) else (
    git clone https://github.com/chaojie/ComfyUI-WanVideoGenerator.git
    if not errorlevel 1 (
        echo SUCCESS: ComfyUI-WanVideoGenerator installed
        if exist "ComfyUI-WanVideoGenerator\requirements.txt" (
            echo Installing dependencies...
            pip install -r ComfyUI-WanVideoGenerator\requirements.txt
        )
    ) else (
        echo WARNING: ComfyUI-WanVideoGenerator repository may not exist
        echo You may need to search for an alternative Wan node package
    )
)

cd ..

echo.
echo ========================================
echo Step 3: Downloading Models
echo ========================================
echo.
echo This will download approximately 45GB of model files.
echo Downloads will be skipped if files already exist.
echo.
pause

REM Check for download tool
where wget >nul 2>&1
if not errorlevel 1 (
    set DOWNLOAD_CMD=wget -c -O
    echo Using wget for downloads...
) else (
    where curl >nul 2>&1
    if not errorlevel 1 (
        set DOWNLOAD_CMD=curl -L -C - -o
        echo Using curl for downloads...
    ) else (
        echo ERROR: Neither wget nor curl found!
        echo Please install wget from https://eternallybored.org/misc/wget/
        echo Or install curl from https://curl.se/windows/
        pause
        exit /b 1
    )
)

echo.
echo ========================================
echo Downloading CLIP Models (~12GB)
echo ========================================
echo.

cd models\clip

echo [1/3] Downloading qwen_2.5_vl_7b_fp8_scaled.safetensors (~4.2GB)...
if exist "qwen_2.5_vl_7b_fp8_scaled.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "qwen_2.5_vl_7b_fp8_scaled.safetensors" "https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/resolve/main/split_files/text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

echo.
echo [2/3] Downloading umt5_xxl_fp8_e4m3fn_scaled.safetensors (~4.9GB)...
if exist "umt5_xxl_fp8_e4m3fn_scaled.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "umt5_xxl_fp8_e4m3fn_scaled.safetensors" "https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

echo.
echo [3/3] Downloading qwen_3_4b.safetensors (~2.5GB)...
if exist "qwen_3_4b.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "qwen_3_4b.safetensors" "https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/qwen_3_4b.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

cd ..\..

echo.
echo ========================================
echo Downloading VAE Models (~500MB)
echo ========================================
echo.

cd models\vae

echo [1/3] Downloading qwen_image_vae.safetensors (~168MB)...
if exist "qwen_image_vae.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "qwen_image_vae.safetensors" "https://huggingface.co/QuantStack/Qwen-Image-GGUF/resolve/main/VAE/Qwen_Image-VAE.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

echo.
echo [2/3] Downloading wan_2.1_vae.safetensors (~168MB)...
if exist "wan_2.1_vae.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "wan_2.1_vae.safetensors" "https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/vae/wan_2.1_vae.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

echo.
echo [3/3] Downloading ae.safetensors (~168MB)...
if exist "ae.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "ae.safetensors" "https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/ae.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

cd ..\..

echo.
echo ========================================
echo Downloading UNET Models (~32GB)
echo ========================================
echo.

cd models\unet

echo [1/4] Downloading Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors (~3.8GB)...
if exist "Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors" "https://huggingface.co/Kijai/Qwen-Edit-2509_safetensors/resolve/main/Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

echo.
echo [2/4] Downloading wan2.2_i2v_A14b_high_noise (~11GB) - This will take a while...
if exist "wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors" "https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/diffusion_models/wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

echo.
echo [3/4] Downloading wan2.2_i2v_A14b_low_noise (~11GB) - This will take a while...
if exist "wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors" "https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/diffusion_models/wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

echo.
echo [4/4] Downloading z_image_turbo_bf16.safetensors (~5.8GB)...
if exist "z_image_turbo_bf16.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "z_image_turbo_bf16.safetensors" "https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/z_image_turbo_bf16.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

cd ..\..

echo.
echo ========================================
echo Downloading LoRA Models (~800MB)
echo ========================================
echo.

cd models\loras\qwen

echo [1/2] Downloading Qwen-Image-Lightning-8steps-V2.0.safetensors (~383MB)...
if exist "Qwen-Image-Lightning-8steps-V2.0.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "Qwen-Image-Lightning-8steps-V2.0.safetensors" "https://huggingface.co/lightx2v/Qwen-Image-Lightning/resolve/main/Qwen-Image-Lightning-8steps-V2.0.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

echo.
echo [2/2] Downloading mult-angles.safetensors (~383MB)...
if exist "mult-angles.safetensors" (
    echo File already exists, skipping...
) else (
    %DOWNLOAD_CMD% "mult-angles.safetensors" "https://huggingface.co/dx8152/Qwen-Edit-2509-Multiple-angles/resolve/main/%%E9%%95%%9C%%E5%%A4%%B4%%E8%%BD%%AC%%E6%%8D%%A2.safetensors"
    if errorlevel 1 echo WARNING: Download may have failed
)

cd ..\..\..

echo.
echo ========================================
echo Setup Complete!
echo ========================================
echo.
echo All custom nodes and models have been downloaded.
echo.
echo Next steps:
echo 1. Start ComfyUI (run_nvidia_gpu.bat or python main.py)
echo 2. Wait for all custom nodes to initialize
echo 3. Open http://127.0.0.1:8188 in your browser
echo 4. Load the FlipPix workflow files to verify setup
echo.
echo If you encounter any issues:
echo - Check the COMFYUI_SETUP.md for troubleshooting
echo - Verify all model files downloaded completely
echo - Restart ComfyUI to load all custom nodes
echo.
pause
