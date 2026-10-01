<#
.SYNOPSIS
    Builds the FlipPix iOS Companion downloads in release\: FlipPix-iOS-Companion-Setup.exe (one file,
    double-click to install) and ios-companion.zip (the same package, unpacked by hand).

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

    FlipPix-iOS-Companion-Setup.exe (FlipPix.IosCompanion.Setup) embeds ios-companion.zip; when run it
    unpacks it under %LocalAppData%\FlipPix\ios-companion-setup\ and starts the wizard.

    Runs on Windows PowerShell 5.1 and on PowerShell 7 (macOS / Linux build boxes can cross-build).

.PARAMETER OutDir
    Where to stage the package. Default: <repo>\release

.PARAMETER NoZip
    Stage the folder but don't create the zip or the setup exe.

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

# Path parts joined with this OS's separator, so the script also cross-builds from macOS / Linux.
function P { [IO.Path]::Combine([string[]]@($args | ForEach-Object { $_ })) }   # flattens array arguments

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
if (-not $OutDir) { $OutDir = P $RepoRoot 'release' }
$Stage      = P $OutDir 'ios-companion'
$BuildDir   = P $OutDir 'ios-companion-build'
$SetupBuild = P $OutDir 'ios-companion-setup-build'
$SetupExe   = 'FlipPix-iOS-Companion-Setup.exe'

# The two graphs the iPad runs (FlipPix.Remote embeds the same files), as path parts under workflow.
$Workflows = @(@('image', 'krea', 'krea2RealismV1_krea2RealismV1WF.json'), @('video', 'h3-minimax', 'h3-minimax-i2v.json'))

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
& $dotnet.Source publish (P $RepoRoot 'FlipPix.IosCompanion' 'FlipPix.IosCompanion.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o $BuildDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
if (-not (Test-Path (P $BuildDir 'FlipPix.IosCompanion.exe'))) { throw 'FlipPix.IosCompanion.exe was not produced.' }
Write-Ok 'build complete'

# ---------------------------------------------------------------------------
# 2. stage
# ---------------------------------------------------------------------------
Write-Step 'Staging package'
if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null
foreach ($f in 'Install-iOS-Companion.bat', 'THIRD_PARTY_LICENSES.md', 'flippix.ico') {
    Copy-Item (P $RepoRoot $f) (P $Stage $f) -Force
}
# Only the app: no stray import libraries or symbols from the build folder.
$appDst = P $Stage 'publish-companion'
New-Item -ItemType Directory -Force -Path $appDst | Out-Null
Get-ChildItem $BuildDir -File | Where-Object { $_.Extension -notin '.lib', '.pdb' } |
    ForEach-Object { Copy-Item $_.FullName (P $appDst $_.Name) -Force }

$scriptsDst = P $Stage 'scripts'
New-Item -ItemType Directory -Force -Path $scriptsDst | Out-Null
foreach ($s in 'flippix-installer.ps1', 'setup-common.ps1', 'setup-comfyui-fresh.ps1', 'setup-llm.ps1',
               'flippix-custom-nodes-ios.txt', 'flippix-models-ios.txt') {
    Copy-Item (P $ScriptDir $s) (P $scriptsDst $s) -Force
}

foreach ($w in $Workflows) {
    $dst = P (@($Stage, 'workflow') + $w)
    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    Copy-Item (P (@($RepoRoot, 'workflow') + $w)) $dst -Force
}
$size = [math]::Round((Get-ChildItem $Stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Ok "staged $Stage ($size MB)"

# ---------------------------------------------------------------------------
# 3. zip
# ---------------------------------------------------------------------------
if (-not $NoZip) {
    Write-Step 'Creating zip'
    $zip = P $OutDir 'ios-companion.zip'
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path $Stage -DestinationPath $zip
    Write-Ok "zip created: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"

    # -----------------------------------------------------------------------
    # 4. the one-file installer, carrying the zip
    # -----------------------------------------------------------------------
    Write-Step "Building $SetupExe"
    if (Test-Path $SetupBuild) { Remove-Item -Recurse -Force $SetupBuild }
    & $dotnet.Source publish (P $RepoRoot 'FlipPix.IosCompanion.Setup' 'FlipPix.IosCompanion.Setup.csproj') `
        -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:DebugSymbols=false "-p:PayloadZip=$((Resolve-Path $zip).Path)" -o $SetupBuild
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the setup exe failed (exit $LASTEXITCODE)." }
    $exe = P $OutDir $SetupExe
    Copy-Item (P $SetupBuild $SetupExe) $exe -Force
    Remove-Item -Recurse -Force $SetupBuild
    Write-Ok "setup created: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
}

Write-Host ''
Write-Host "Users download $SetupExe to the RTX PC and double-click it (or unzip ios-companion.zip and"
Write-Host 'double-click Install-iOS-Companion.bat).'
