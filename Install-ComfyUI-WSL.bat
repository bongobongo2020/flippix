@echo off
REM ===========================================================================
REM  FlipPix - one-click ComfyUI in WSL (Hugging Face snapshot)
REM
REM  DOUBLE-CLICK this for the smoothest setup if you DON'T already have ComfyUI:
REM  it downloads a prebuilt ComfyUI snapshot into WSL2 and runs it - no 1TB of
REM  model downloads and none of the Windows portable / VC++ headaches.
REM
REM  Bootstraps scripts\setup-comfyui-wsl.ps1. If WSL isn't installed yet, that
REM  script will tell you to run this once as administrator (right-click >
REM  "Run as administrator"), reboot, finish the Ubuntu first-run, then re-run.
REM
REM  Options pass through, e.g.:  Install-ComfyUI-WSL.bat -HfRepo you/your-repo
REM ===========================================================================

setlocal
title FlipPix - ComfyUI in WSL

set "ROOT=%~dp0"
set "PS1=%ROOT%scripts\setup-comfyui-wsl.ps1"

if not exist "%PS1%" (
    echo [x] Could not find the WSL setup script:
    echo     "%PS1%"
    echo     Make sure this .bat stays next to the scripts\ folder.
    echo.
    pause
    exit /b 1
)

echo.
echo  ============================================
echo   FlipPix - ComfyUI in WSL (one-click)
echo  ============================================
echo.
echo  Downloads a prebuilt ComfyUI snapshot from Hugging Face into WSL2 and
echo  starts it, then points FlipPix at it. The download is large.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo  [ok] Done. See the notes above for next steps.
) else (
    echo  [x] Exited with error code %RC%. Scroll up for details.
)
echo.
pause
endlocal
exit /b %RC%
