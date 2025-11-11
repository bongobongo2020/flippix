@echo off
echo Publishing FlipPix Video Processor...

REM Clean previous publish
if exist publish rmdir /s /q publish

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
echo Publishing complete!
echo Output location: publish\
echo Executable: publish\FlipPix.UI.exe
pause