@echo off
REM ===========================================================================
REM  FlipPix - one-click REMOTE ComfyUI backup
REM
REM  Double-click this file to snapshot the ComfyUI install on the remote box
REM  (x2@192.168.1.10, the "jun1" folder) over SSH and download it here as a
REM  single restore-anywhere bundle. Custom nodes are included; models are NOT
REM  (pass -IncludeModels to bundle them too).
REM
REM  It bootstraps scripts\backup-comfyui-remote.ps1 with the right PowerShell
REM  execution policy, so you never have to open a terminal.
REM
REM  Examples (run from a prompt, or append after the filename):
REM       Backup-ComfyUI.bat
REM       Backup-ComfyUI.bat -IncludeModels -OutDir D:\backups
REM       Backup-ComfyUI.bat -User bob -RemoteHost 10.0.0.5 -RemotePath ~/ComfyUI
REM
REM  Publish to Hugging Face in the same run (needs the `hf` CLI + `hf auth login`):
REM       Backup-ComfyUI.bat -HfUpload -HfRepo yourname/flippix-comfyui
REM  Users then restore with one command:  bash restore-comfyui.sh --hf yourname/flippix-comfyui
REM
REM  Make + publish a NATIVE WINDOWS bundle from a LOCAL Windows ComfyUI (no SSH):
REM       Backup-ComfyUI.bat -Windows -HfUpload -HfRepo yourname/flippix-comfyui
REM       Backup-ComfyUI.bat -Windows -LocalPath "C:\ComfyUI_windows_portable"
REM  Windows users then restore with:  restore-comfyui-windows.ps1 -HfRepo yourname/flippix-comfyui
REM ===========================================================================

setlocal
title FlipPix - Remote ComfyUI Backup

set "ROOT=%~dp0"
set "PS1=%ROOT%scripts\backup-comfyui-remote.ps1"

if not exist "%PS1%" (
    echo [x] Could not find the backup script:
    echo     "%PS1%"
    echo     Make sure this .bat stays in the root of the flippix repo.
    echo.
    pause
    exit /b 1
)

echo.
echo  ============================================
echo   FlipPix - remote ComfyUI backup
echo  ============================================
echo.
echo  Connects to x2@192.168.1.10 (the jun1 ComfyUI) over SSH and downloads a
echo  restore-anywhere snapshot. You may be prompted for the SSH password.
echo  (Set up an SSH key to skip the prompt - see the bundle's RESTORE-README.md.)
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo  [ok] Backup finished. See the bundle folder shown above.
) else (
    echo  [x] Backup exited with error code %RC%. Scroll up for details.
)
echo.
pause
endlocal
exit /b %RC%
