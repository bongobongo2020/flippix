@echo off
echo Publishing FlipPix Video Processor...

REM Kill any running FlipPix instances so files are not locked
echo Checking for running FlipPix instances...
taskkill /F /IM FlipPix.UI.exe >nul 2>&1
if %errorlevel% equ 0 (
    echo Killed running FlipPix.UI.exe — waiting for process to exit...
    timeout /t 2 /nobreak >nul
) else (
    echo No running instances found.
)

REM Clean previous publish exe so it can be overwritten.
REM For a PublishSingleFile build the only locked file is the exe itself —
REM we already killed FlipPix.UI.exe above, so a direct delete is enough.
REM The publish folder is NOT wiped; dotnet publish overwrites files in place.
if exist "publish\FlipPix.UI.exe" (
    del /F /Q "publish\FlipPix.UI.exe" >nul 2>&1
    if exist "publish\FlipPix.UI.exe" (
        echo WARNING: Could not delete publish\FlipPix.UI.exe — it may still be locked.
        echo          Build will attempt to overwrite it anyway.
    )
)

REM Clean build artifacts to ensure all changes are compiled
dotnet clean FlipPix.sln -c Release >nul 2>&1

REM Delete bin/obj to force full recompilation (prevents stale XAML/BAML cache)
echo Cleaning bin/obj directories...
if exist FlipPix.Core\bin rmdir /s /q FlipPix.Core\bin
if exist FlipPix.Core\obj rmdir /s /q FlipPix.Core\obj
if exist FlipPix.ComfyUI\bin rmdir /s /q FlipPix.ComfyUI\bin
if exist FlipPix.ComfyUI\obj rmdir /s /q FlipPix.ComfyUI\obj
if exist FlipPix.UI\bin rmdir /s /q FlipPix.UI\bin
if exist FlipPix.UI\obj rmdir /s /q FlipPix.UI\obj

REM Publish as self-contained Windows x64 application
dotnet publish FlipPix.UI/FlipPix.UI.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:PublishReadyToRun=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o publish

echo.
echo Copying required files...

REM Copy prompts directory (excluding .venv)
echo Copying prompts directory...
if not exist "publish\prompts" mkdir "publish\prompts"
if not exist "publish\prompts\prompt2json" mkdir "publish\prompts\prompt2json"

REM Copy prompt2json system prompt files
copy /Y "prompts\prompt2json\ltx_action_video_system_prompt.md" "publish\prompts\prompt2json\" >nul 2>&1
copy /Y "prompts\prompt2json\ltxv2_system_prompt_addition.md" "publish\prompts\prompt2json\" >nul 2>&1
copy /Y "prompts\prompt2json\wan-system.md" "publish\prompts\prompt2json\" >nul 2>&1
copy /Y "prompts\prompt2json\qwen2512.md" "publish\prompts\prompt2json\" >nul 2>&1
copy /Y "prompts\prompt2json\klien-story-10.md" "publish\prompts\prompt2json\" >nul 2>&1
copy /Y "prompts\prompt2json\.prompt2json_config.json" "publish\prompts\prompt2json\" >nul 2>&1
copy /Y "prompts\prompt2json\README.md" "publish\prompts\prompt2json\" >nul 2>&1

REM Copy workflow directory
echo Copying workflow directory...
if not exist "publish\workflow" mkdir "publish\workflow"
xcopy /Y /Q /S /E "workflow\*.json" "publish\workflow\" >nul

echo.
echo ============================================
echo Publishing complete!
echo ============================================
echo Output location: publish\
echo Executable: publish\FlipPix.UI.exe
echo.
echo Copied files:
echo - prompts\prompt2json\*.md (system prompts)
echo - prompts\prompt2json\*.json (config files)
echo - workflow\*.json (workflow files)
echo ============================================
pause