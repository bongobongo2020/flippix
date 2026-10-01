<#
.SYNOPSIS
    Builds the downloadable FlipPix iOS Companion package: release\ios-companion\ and ios-companion.zip.

.DESCRIPTION
    The package is everything a PC needs to serve the FlipPix iPad / iPhone app, and nothing else:

        ios-companion\
          Install-iOS-Companion.bat     <- the user double-clicks this
          THIRD_PARTY_LICENSES.md
          flippix.ico
          publish-companion\            (self-contained FlipPix.IosCompanion.exe; no .NET runtime needed)
          scripts\                      (the wizard, ComfyUI + writing-assistant installers, iOS lists)
          workflow\                     (only the two graphs the iPad runs, for the missing-node scan)

    No model weights are included; the wizard downloads them from their publishers.

.PARAMETER OutDir
    Where to stage the package. Default: <repo>\release

.PARAMETER NoZip
    Stage the folder but don't create the zip.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\make-ios-companion.ps1
#>

[CmdletBinding()]
param(
    [string]$OutDir = '',
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [ok] $m" -ForegroundColor Green }

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'release' }
$Stage    = Join-Path $OutDir 'ios-companion'
$BuildDir = Join-Path $OutDir 'ios-companion-build'

# The two graphs the iPad runs (FlipPix.Remote embeds the same files).
$Workflows = @('image\krea\krea2RealismV1_krea2RealismV1WF.json', 'video\h3-minimax\h3-minimax-i2v.json')

Write-Host 'FlipPix iOS Companion packager' -ForegroundColor Magenta

# ---------------------------------------------------------------------------
# 1. build the companion (self-contained, single file)
# ---------------------------------------------------------------------------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw 'dotnet SDK not found. Install the .NET 8 SDK.' }
Get-Process FlipPix.IosCompanion -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Step "Stopping the running companion (pid $($_.Id)) so its exe can be rebuilt"
    $_ | Stop-Process -Force
}
Write-Step 'Building FlipPix iOS Companion (Release, self-contained, single-file)'
if (Test-Path $BuildDir) { Remove-Item -Recurse -Force $BuildDir }
& $dotnet.Source publish (Join-Path $RepoRoot 'FlipPix.IosCompanion\FlipPix.IosCompanion.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o $BuildDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
if (-not (Test-Path (Join-Path $BuildDir 'FlipPix.IosCompanion.exe'))) { throw 'FlipPix.IosCompanion.exe was not produced.' }
Write-Ok 'build complete'

# ---------------------------------------------------------------------------
# 2. stage
# ---------------------------------------------------------------------------
Write-Step 'Staging package'
if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null
foreach ($f in 'Install-iOS-Companion.bat', 'THIRD_PARTY_LICENSES.md', 'flippix.ico') {
    Copy-Item (Join-Path $RepoRoot $f) (Join-Path $Stage $f) -Force
}
Copy-Item $BuildDir (Join-Path $Stage 'publish-companion') -Recurse -Force

$scriptsDst = Join-Path $Stage 'scripts'
New-Item -ItemType Directory -Force -Path $scriptsDst | Out-Null
foreach ($s in 'flippix-installer.ps1', 'setup-common.ps1', 'setup-comfyui-fresh.ps1', 'setup-llm.ps1',
               'flippix-custom-nodes-ios.txt', 'flippix-models-ios.txt') {
    Copy-Item (Join-Path $ScriptDir $s) (Join-Path $scriptsDst $s) -Force
}

foreach ($w in $Workflows) {
    $dst = Join-Path $Stage (Join-Path 'workflow' $w)
    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    Copy-Item (Join-Path $RepoRoot (Join-Path 'workflow' $w)) $dst -Force
}
$size = [math]::Round((Get-ChildItem $Stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Ok "staged $Stage ($size MB)"

# ---------------------------------------------------------------------------
# 3. zip
# ---------------------------------------------------------------------------
if (-not $NoZip) {
    Write-Step 'Creating zip'
    $zip = Join-Path $OutDir 'ios-companion.zip'
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path $Stage -DestinationPath $zip
    Write-Ok "zip created: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}

Write-Host ''
Write-Host 'Users download ios-companion.zip, extract it on the RTX PC and double-click Install-iOS-Companion.bat.'
