@echo off
REM ===========================================================================
REM  FlipPix - one-click ComfyUI installer
REM
REM  Just DOUBLE-CLICK this file. It provisions a fresh, self-contained ComfyUI
REM  and installs every custom-node pack the FlipPix workflows need. The only
REM  thing left afterwards is downloading model weights.
REM
REM  It simply bootstraps scripts\setup-comfyui-fresh.ps1 with the right
REM  PowerShell execution policy so you don't have to touch a terminal.
REM
REM  Tip: drag any extra options onto this file or run it from a prompt, e.g.
REM       Install-ComfyUI.bat -InstallDir D:\AI\ComfyUI
REM ===========================================================================

setlocal
title FlipPix - ComfyUI Installer

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
echo   FlipPix - ComfyUI one-click installer
echo  ============================================
echo.
echo  This downloads ComfyUI (large, ~2 GB) and all FlipPix custom nodes.
echo  Models are NOT downloaded here. Grab a coffee - this can take a while.
echo.

REM -ExecutionPolicy Bypass: run the script without changing system policy.
REM %* forwards any extra arguments (e.g. -InstallDir D:\AI\ComfyUI).
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
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
