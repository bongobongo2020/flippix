<#
.SYNOPSIS
    Build a clean, downloadable FlipPix release package.

.DESCRIPTION
    Produces release\FlipPix-Setup\ (and a matching .zip) containing exactly what a
    brand-new user needs to download and run the one-click installer:

        FlipPix-Setup\
          Install-FlipPix.bat
          Install-ComfyUI.bat
          flippix.ico
          publish\            (fresh self-contained single-file build, NO user output)
          workflow\           (for the ComfyUI installer's copy/scan steps)
          scripts\            (installer + ComfyUI scripts + node/model lists)

    The app is published self-contained, so end users need no .NET runtime.
    Generated runtime folders (output\, edited-images\) are never copied.

.PARAMETER OutDir
    Where to stage the package. Default: <repo>\release

.PARAMETER NoBuild
    Skip dotnet publish and reuse the existing <repo>\publish folder (must already
    contain FlipPix.UI.exe). Useful if you just built it.

.PARAMETER NoZip
    Stage the folder but don't create the .zip.

.PARAMETER IncludeEngine
    Also put the starter ComfyUI (release\comfyui-starter\flippix-starter-windows.7z, from
    packaging\comfyui-starter\build-starter.ps1) beside Setup, so installing needs no download
    for it. Without it, Setup downloads the engine from Hugging Face when it's chosen.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\make-release.ps1
#>

[CmdletBinding()]
param(
    [string]$OutDir = '',
    [switch]$NoBuild,
    [switch]$NoZip,
    [switch]$IncludeEngine
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [ok] $m" -ForegroundColor Green }

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$PublishDir = Join-Path $RepoRoot 'publish'
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'release' }
$Stage = Join-Path $OutDir 'FlipPix-Setup'
# Build into a CLEAN folder so stale DLLs from earlier builds (and the user's
# generated output\ in repo\publish) never leak into the release. -NoBuild reuses
# the existing repo\publish as-is.
$BuildDir = if ($NoBuild) { $PublishDir } else { Join-Path $OutDir 'app-build' }

Write-Host "FlipPix release packager" -ForegroundColor Magenta
Write-Host "Repo:  $RepoRoot"
Write-Host "Stage: $Stage"

# ---------------------------------------------------------------------------
# 1. build a clean self-contained single-file app
# ---------------------------------------------------------------------------
if (-not $NoBuild) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw "dotnet SDK not found. Install the .NET 8 SDK, or pass -NoBuild to reuse an existing publish folder." }
    # Stop any running FlipPix so the single-file exe isn't locked during publish.
    Get-Process FlipPix.UI -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Step "Stopping running FlipPix (pid $($_.Id)) so its exe can be rebuilt"
        $_ | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 500
    Write-Step 'Building FlipPix (Release, self-contained, single-file)'
    if (Test-Path $BuildDir) { Remove-Item -Recurse -Force $BuildDir }
    $csproj = Join-Path $RepoRoot 'FlipPix.UI\FlipPix.UI.csproj'
    & $dotnet.Source publish $csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:PublishReadyToRun=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o $BuildDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    Write-Ok 'build complete'
}

if (-not (Test-Path (Join-Path $BuildDir 'FlipPix.UI.exe'))) {
    throw "FlipPix.UI.exe not found in $BuildDir. Build first (omit -NoBuild) or run publish.bat release."
}

# Compile the standalone uninstaller into the build folder so it deploys into the
# install dir alongside FlipPix.UI.exe (the installer copies publish\).
Write-Step 'Building uninstaller'
& (Join-Path $ScriptDir 'build-uninstaller.ps1') -OutPath (Join-Path $BuildDir 'Uninstall-FlipPix.exe')

# ---------------------------------------------------------------------------
# 2. stage a clean folder
# ---------------------------------------------------------------------------
Write-Step 'Staging package'
if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null

# root files (launchers + the one-click backup entry point)
foreach ($f in 'Install-FlipPix.bat','Uninstall-FlipPix.bat','Install-ComfyUI.bat','Install-ComfyUI-WSL.bat','Backup-ComfyUI.bat','flippix.ico','THIRD_PARTY_LICENSES.md') {
    Copy-Item (Join-Path $RepoRoot $f) (Join-Path $Stage $f) -Force
}
Write-Ok 'copied launchers + icon'

# scripts the installers + backup/restore tooling use (NOT make-release.ps1 / dev helpers)
$scriptsDst = Join-Path $Stage 'scripts'
New-Item -ItemType Directory -Force -Path $scriptsDst | Out-Null
foreach ($s in 'flippix-installer.ps1','setup-common.ps1','setup-llm.ps1','setup-comfyui-fresh.ps1','setup-comfyui-wsl.ps1','set-wsl-models.ps1','flippix-custom-nodes.txt','flippix-models.txt',
               'flippix-custom-nodes-min.txt','flippix-models-min.txt','flippix-models-16gb-video.txt',
               'backup-comfyui-remote.ps1','restore-comfyui.sh','restore-comfyui-windows.ps1','README.md',
               'flippix-models.ps1') {
    Copy-Item (Join-Path $ScriptDir $s) (Join-Path $scriptsDst $s) -Force
}
Write-Ok 'copied installer + backup/restore scripts'

# The starter engine's manifest (Setup's engine option and FlipPix Models both read it).
$starterDir = Join-Path $RepoRoot 'packaging\comfyui-starter'
Copy-Item (Join-Path $starterDir 'starter.json') (Join-Path $scriptsDst 'starter.json') -Force
$sevenZr = Join-Path $RepoRoot 'release\comfyui-starter\cache\7zr.exe'
if (Test-Path $sevenZr) { Copy-Item $sevenZr (Join-Path $scriptsDst '7zr.exe') -Force }
if ($IncludeEngine) {
    $engine = Join-Path $RepoRoot ('release\comfyui-starter\' + (Get-Content (Join-Path $starterDir 'starter.json') -Raw | ConvertFrom-Json).bundle.file)
    if (-not (Test-Path $engine)) { throw "No engine to include: run packaging\comfyui-starter\build-starter.ps1 first ($engine)." }
    Copy-Item $engine $Stage -Force
    Write-Ok 'included the starter engine (no download needed at install)'
}
Write-Ok 'copied starter manifest + FlipPix Models'

# workflow library (used by the ComfyUI installer's copy + missing-node scan)
if (Test-Path (Join-Path $RepoRoot 'workflow')) {
    Copy-Item (Join-Path $RepoRoot 'workflow') (Join-Path $Stage 'workflow') -Recurse -Force
    Write-Ok 'copied workflow library'
}

# the app itself, EXCLUDING generated runtime folders
Write-Step 'Copying app (excluding generated output)'
$exclude = @('output','edited-images')
$pubDst = Join-Path $Stage 'publish'
New-Item -ItemType Directory -Force -Path $pubDst | Out-Null
Get-ChildItem -Path $BuildDir -Force | Where-Object { $exclude -notcontains $_.Name } | ForEach-Object {
    Copy-Item $_.FullName -Destination (Join-Path $pubDst $_.Name) -Recurse -Force
}
# Never ship debug symbols (stale or otherwise).
Get-ChildItem $pubDst -Recurse -Filter *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue
$appSize = [math]::Round((Get-ChildItem $pubDst -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Ok "app copied ($appSize MB)"

# Also surface the uninstaller at the package root so it can be run straight from
# the extracted download (it lives in publish\ too, which the installer deploys).
$uninst = Join-Path $pubDst 'Uninstall-FlipPix.exe'
if (Test-Path $uninst) {
    Copy-Item $uninst (Join-Path $Stage 'Uninstall-FlipPix.exe') -Force
    Write-Ok 'staged uninstaller'
}

# ---------------------------------------------------------------------------
# 3. zip
# ---------------------------------------------------------------------------
if (-not $NoZip) {
    Write-Step 'Creating zip'
    $zip = Join-Path $OutDir 'FlipPix-Setup.zip'
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path $Stage -DestinationPath $zip
    $zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Ok "zip created: $zip ($zipSize MB)"
}

Write-Host "`n==================================================" -ForegroundColor Magenta
Write-Host " Release package ready" -ForegroundColor Magenta
Write-Host "==================================================" -ForegroundColor Magenta
Write-Host "Folder: $Stage"
if (-not $NoZip) { Write-Host "Zip   : $(Join-Path $OutDir 'FlipPix-Setup.zip')  <- upload this to your GitHub Release" }
Write-Host ""
Write-Host "A new user downloads the zip, extracts it, and double-clicks Install-FlipPix.bat."
