@echo off
REM ===========================================================================
REM  FlipPix - uninstaller launcher
REM
REM  Just DOUBLE-CLICK this file. A setup wizard (Windows 98 style) walks you
REM  through removing FlipPix from this computer: the program files, the desktop
REM  and Start Menu shortcuts, and (optionally) your settings and logs.
REM
REM  It simply runs Uninstall-FlipPix.exe, looking for it next to this .bat or in
REM  the publish\ folder.
REM ===========================================================================

setlocal
title FlipPix Uninstall

set "ROOT=%~dp0"
set "EXE=%ROOT%Uninstall-FlipPix.exe"

if not exist "%EXE%" set "EXE=%ROOT%publish\Uninstall-FlipPix.exe"

if not exist "%EXE%" (
    echo [x] Could not find the uninstaller:
    echo     "%ROOT%Uninstall-FlipPix.exe"
    echo     "%ROOT%publish\Uninstall-FlipPix.exe"
    echo     Keep this .bat next to Uninstall-FlipPix.exe.
    echo.
    pause
    exit /b 1
)

start "" "%EXE%"
endlocal
exit /b 0
