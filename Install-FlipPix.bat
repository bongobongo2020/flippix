@echo off
REM ===========================================================================
REM  FlipPix - one-click installer
REM
REM  Just DOUBLE-CLICK this file. A setup wizard (Windows 98 style) walks you
REM  through installing FlipPix on this computer, with an option to also install
REM  ComfyUI (the image/video engine FlipPix drives).
REM
REM  It bootstraps scripts\flippix-installer.ps1 with the right PowerShell
REM  execution policy so you never have to open a terminal.
REM ===========================================================================

setlocal
title FlipPix Setup

set "ROOT=%~dp0"
set "PS1=%ROOT%scripts\flippix-installer.ps1"

if not exist "%PS1%" (
    echo [x] Could not find the installer script:
    echo     "%PS1%"
    echo     Keep this .bat in the root of the flippix folder.
    echo.
    pause
    exit /b 1
)

REM -STA is required for the WinForms file/folder dialogs.
powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%PS1%" %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
    echo.
    echo  [x] Setup exited with error code %RC%.
    echo.
    pause
)
endlocal
exit /b %RC%
