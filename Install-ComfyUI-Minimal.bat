@echo off
REM ===========================================================================
REM  FlipPix - minimal one-click ComfyUI installer
REM
REM  Just DOUBLE-CLICK this file. It provisions a stripped-down, self-contained
REM  ComfyUI for the core creative subset (image generation + image editing) and
REM  downloads only the models that subset needs (~21 GB instead of ~45 GB).
REM
REM  VRAM is auto-detected: on a ~16 GB GPU, FlipPix is set to the memory-optimized
REM  "16gb" workflow tier so workflows fit instead of crashing. Force a tier by
REM  passing -Tier full or -Tier 16gb.
REM
REM  It bootstraps scripts\setup-comfyui-fresh.ps1 -Minimal -DownloadModels with
REM  the right PowerShell execution policy so you don't have to touch a terminal.
REM
REM  Tip: forward extra options, e.g.
REM       Install-ComfyUI-Minimal.bat -InstallDir D:\AI\ComfyUI -Tier 16gb
REM ===========================================================================

setlocal
title FlipPix - Minimal ComfyUI Installer

REM Resolve the folder this .bat lives in (works even when double-clicked).
set "ROOT=%~dp0"
set "PS1=%ROOT%scripts\setup-comfyui-fresh.ps1"

if not exist "%PS1%" (
    echo [x] Could not find the installer script:
    echo     "%PS1%"
    echo     Make sure this .bat stays in the root of the flippix repo.
    echo.
    pause
    exit /b 1
)

echo.
echo  ============================================
echo   FlipPix - MINIMAL ComfyUI installer
echo  ============================================
echo.
echo  Downloads ComfyUI (large, ~2 GB), the core custom nodes, and the core
echo  creative subset models (~21 GB). VRAM tier is auto-detected. Grab a coffee.
echo.

REM -ExecutionPolicy Bypass: run the script without changing system policy.
REM %* forwards any extra arguments (e.g. -InstallDir, -Tier 16gb).
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -Minimal -DownloadModels %*
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo  [ok] Installer finished. See the notes above for next steps.
) else (
    echo  [x] Installer exited with error code %RC%. Scroll up for details.
)
echo.
pause
endlocal
exit /b %RC%
