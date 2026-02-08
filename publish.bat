@echo off
echo Publishing FlipPix Video Processor...

REM Clean previous publish
if exist publish rmdir /s /q publish

REM Clean build artifacts to ensure all changes are compiled
dotnet clean FlipPix.UI/FlipPix.UI.csproj -c Release >nul 2>&1

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
copy /Y "prompts\prompt2json\.prompt2json_config.json" "publish\prompts\prompt2json\" >nul 2>&1
copy /Y "prompts\prompt2json\README.md" "publish\prompts\prompt2json\" >nul 2>&1

REM Copy workflow directory
echo Copying workflow directory...
if not exist "publish\workflow" mkdir "publish\workflow"
xcopy /Y /Q "workflow\*.json" "publish\workflow\" >nul

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