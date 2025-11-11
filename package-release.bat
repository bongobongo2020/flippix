@echo off
REM FlipPix Release Packaging Script
REM Creates a distributable ZIP package for Windows

echo ==================================================
echo FlipPix Release Packager
echo ==================================================
echo.

REM Check if publish folder exists
if not exist "publish\FlipPix.UI.exe" (
    echo ERROR: publish\FlipPix.UI.exe not found!
    echo Please run publish.bat first to build the application.
    pause
    exit /b 1
)

REM Clean up old release package
if exist "FlipPix-Release.zip" (
    echo Removing old release package...
    del /F /Q "FlipPix-Release.zip"
)

echo Creating release package...
echo.

REM Copy INSTALL.txt to publish folder
copy /Y INSTALL.txt publish\INSTALL.txt >nul 2>&1

REM Create ZIP archive excluding debug files and output folder
echo Excluding: .pdb files, output folder and all its contents
powershell -Command "& { $publishPath = 'publish'; $files = Get-ChildItem -Path $publishPath -Recurse -File | Where-Object { $_.FullName -notmatch '\\output\\' -and $_.Extension -ne '.pdb' }; $dirs = Get-ChildItem -Path $publishPath -Recurse -Directory | Where-Object { $_.FullName -notmatch '\\output' }; $allItems = $files + $dirs; if (Test-Path 'FlipPix-Release.zip') { Remove-Item 'FlipPix-Release.zip' -Force }; Compress-Archive -Path $allItems.FullName -DestinationPath 'FlipPix-Release.zip' -Force }"

if exist "FlipPix-Release.zip" (
    echo.
    echo ==================================================
    echo SUCCESS! Release package created successfully.
    echo ==================================================
    echo.
    echo Package location: %CD%\FlipPix-Release.zip
    echo.
    for %%A in ("FlipPix-Release.zip") do echo Package size: %%~zA bytes
    echo.
    echo Users can extract this ZIP and run FlipPix.UI.exe
    echo.
) else (
    echo.
    echo ERROR: Failed to create release package.
    echo Make sure the application is not currently running.
    echo.
)

pause
