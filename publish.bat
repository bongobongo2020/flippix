@echo off
setlocal
echo Publishing FlipPix Video Processor...

REM ── Build mode ────────────────────────────────────────────────────────────
REM   publish.bat          Fast incremental dev build (no clean, no ReadyToRun).
REM   publish.bat clean    Incremental build but wipe bin/obj first (use if a
REM                        stale XAML/BAML cache is suspected).
REM   publish.bat release  Full clean + ReadyToRun build for distribution.
set MODE=%1
set R2R=false
set DOCLEAN=0
if /I "%MODE%"=="release" (
    set R2R=true
    set DOCLEAN=1
)
if /I "%MODE%"=="clean" set DOCLEAN=1
echo Mode: %MODE%   (ReadyToRun=%R2R%  FullClean=%DOCLEAN%)

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

REM Full clean is opt-in. By default we keep bin/obj so the build is incremental —
REM only changed projects recompile, which is the single biggest time saver here.
if "%DOCLEAN%"=="1" (
    echo Full clean requested — wiping build artifacts...
    dotnet clean FlipPix.sln -c Release >nul 2>&1
    if exist FlipPix.Core\bin rmdir /s /q FlipPix.Core\bin
    if exist FlipPix.Core\obj rmdir /s /q FlipPix.Core\obj
    if exist FlipPix.ComfyUI\bin rmdir /s /q FlipPix.ComfyUI\bin
    if exist FlipPix.ComfyUI\obj rmdir /s /q FlipPix.ComfyUI\obj
    if exist FlipPix.UI\bin rmdir /s /q FlipPix.UI\bin
    if exist FlipPix.UI\obj rmdir /s /q FlipPix.UI\obj
)

REM Publish as self-contained Windows x64 application.
REM ReadyToRun is off by default: it AOT-compiles assemblies at publish time
REM (the largest cost, ~1-2 min) for only a marginally faster app cold start.
REM Use "publish.bat release" to re-enable it for distribution builds.
dotnet publish FlipPix.UI/FlipPix.UI.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:PublishReadyToRun=%R2R% ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o publish

REM Abort if the publish/build failed — do not copy files or launch.
if %errorlevel% neq 0 (
    echo.
    echo ============================================
    echo BUILD FAILED — FlipPix.UI.exe will NOT be launched.
    echo ============================================
    pause
    exit /b %errorlevel%
)

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
if exist "publish\FlipPix.UI.exe" (
    echo Launching FlipPix.UI.exe...
    start "" "publish\FlipPix.UI.exe"
) else (
    echo ERROR: publish\FlipPix.UI.exe not found — nothing to launch.
    pause
    exit /b 1
)
