@echo off
REM ===========================================================================
REM  FlipPix iOS Companion - one-click installer
REM
REM  Just DOUBLE-CLICK this file on the PC with the NVIDIA graphics card (RTX
REM  4070 Ti or better). A setup wizard (Windows 98 style) checks the PC, then
REM  installs only what the FlipPix iPad / iPhone app needs: the companion app,
REM  ComfyUI with Krea 2 (pictures) and MiniMax H3 (video), the Qwen2.5-VL
REM  writing assistant and the content filter, with a progress bar the whole
REM  way. Run it again to resume an interrupted install.
REM
REM  It bootstraps scripts\flippix-installer.ps1 -Companion with the right
REM  PowerShell execution policy so you never have to open a terminal.
REM ===========================================================================

setlocal
title FlipPix iOS Companion Setup

set "ROOT=%~dp0"
set "PS1=%ROOT%scripts\flippix-installer.ps1"

if not exist "%PS1%" (
    echo [x] Could not find the installer script:
    echo     "%PS1%"
    echo     Keep this .bat in the root of the ios-companion folder.
    echo.
    pause
    exit /b 1
)

REM -STA is required for the WinForms file/folder dialogs.
powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%PS1%" -Companion %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
    echo.
    echo  [x] Setup exited with error code %RC%.
    echo.
    pause
)
endlocal
exit /b %RC%
