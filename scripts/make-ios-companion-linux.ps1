<#
.SYNOPSIS
    Builds the Ubuntu download of the FlipPix iOS Companion: release\FlipPix-iOS-Companion-Setup-Linux.sh,
    one self-extracting file the user runs with  bash FlipPix-iOS-Companion-Setup-Linux.sh

.DESCRIPTION
    The file is a short shell header followed by a .tar.gz of:

        flippix-companion-linux/
          install-ios-companion-linux.sh   <- the header unpacks to a temp folder and runs this
          app/                             (self-contained linux-x64 flippix-companion; no .NET needed)
          lists/                           (the iOS node and model lists, the same ones Windows uses)
          workflow/                        (only the two graphs the iPad runs, for the missing-node scan)

    No model weights are included; the installer downloads them from their publishers.
    Runs on Windows PowerShell 5.1 and on PowerShell 7 (needs tar, which Windows 10+ ships).

.PARAMETER OutDir
    Where to build. Default: <repo>\release

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\make-ios-companion-linux.ps1
#>

[CmdletBinding()]
param(
    [string]$OutDir = ''
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
$Work      = P $OutDir 'ios-companion-linux'
$BuildDir  = P $Work 'build'
$Stage     = P $Work 'flippix-companion-linux'
$SetupName = 'FlipPix-iOS-Companion-Setup-Linux.sh'

# The two graphs the iPad runs (FlipPix.Remote embeds the same files), as path parts under workflow.
$Workflows = @(@('image', 'krea', 'krea2RealismV1_krea2RealismV1WF.json'), @('video', 'h3-minimax', 'h3-minimax-i2v.json'))

# Text files the installer reads on Linux: LF only, no BOM.
function Copy-Lf($src, $dst) {
    $text = [IO.File]::ReadAllText($src) -replace "`r`n", "`n" -replace "`r", "`n"
    [IO.File]::WriteAllText($dst, $text, (New-Object Text.UTF8Encoding $false))
}

Write-Host 'FlipPix iOS Companion packager (Ubuntu)' -ForegroundColor Magenta

# ---------------------------------------------------------------------------
# 1. build the companion (linux-x64, self-contained, single file)
# ---------------------------------------------------------------------------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw 'dotnet SDK not found. Install the .NET 8 SDK.' }
if (-not (Get-Command tar -ErrorAction SilentlyContinue)) { throw 'tar not found (Windows 10 and later include it).' }

Write-Step 'Building flippix-companion (Release, linux-x64, self-contained, single-file)'
if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
& $dotnet.Source publish (P $RepoRoot 'FlipPix.IosCompanion.Linux' 'FlipPix.IosCompanion.Linux.csproj') `
    -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o $BuildDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
if (-not (Test-Path (P $BuildDir 'flippix-companion'))) { throw 'flippix-companion was not produced.' }
Write-Ok 'build complete'

# ---------------------------------------------------------------------------
# 2. stage
# ---------------------------------------------------------------------------
Write-Step 'Staging package'
$appDst = P $Stage 'app'
New-Item -ItemType Directory -Force -Path $appDst | Out-Null
# Only the app: the OnnxRuntime package also drops its Windows DLLs beside a linux build.
Copy-Item (P $BuildDir 'flippix-companion') (P $appDst 'flippix-companion') -Force
foreach ($f in 'THIRD_PARTY_LICENSES.md', 'NOTICE.txt') { Copy-Item (P $BuildDir $f) (P $appDst $f) -Force }

Copy-Lf (P $ScriptDir 'install-ios-companion-linux.sh') (P $Stage 'install-ios-companion-linux.sh')
$lists = P $Stage 'lists'
New-Item -ItemType Directory -Force -Path $lists | Out-Null
foreach ($s in 'flippix-custom-nodes-ios.txt', 'flippix-models-ios.txt') { Copy-Lf (P $ScriptDir $s) (P $lists $s) }

foreach ($w in $Workflows) {
    $dst = P (@($Stage, 'workflow') + $w)
    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    Copy-Item (P (@($RepoRoot, 'workflow') + $w)) $dst -Force
}
Write-Ok "staged $Stage"

# ---------------------------------------------------------------------------
# 3. the one-file installer: shell header + .tar.gz
# ---------------------------------------------------------------------------
Write-Step "Building $SetupName"
$payload = P $Work 'payload.tar.gz'
& tar -czf $payload -C $Work 'flippix-companion-linux'
if ($LASTEXITCODE -ne 0) { throw "tar failed (exit $LASTEXITCODE)." }

# POSIX sh, so "sh file" works as well as "bash file". __LINE__ is the line the archive starts on.
$header = @'
#!/bin/sh
# FlipPix iOS Companion - Ubuntu setup. Run it with:
#
#     bash FlipPix-iOS-Companion-Setup-Linux.sh
#
# It unpacks itself to a temporary folder and runs install-ios-companion-linux.sh, which installs
# ComfyUI, the models, the writing assistant and the companion service (into ~/FlipPix).
# Options are passed on: --dir DIR, --yes, --no-service, --no-linger, --help.
set -e
SKIP=__LINE__
TMP="$(mktemp -d "${TMPDIR:-/tmp}/flippix-companion-setup.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT
trap 'exit 130' INT TERM
echo "Unpacking FlipPix iOS Companion setup..."
tail -n +"$SKIP" "$0" | tar -xzf - -C "$TMP"
chmod +x "$TMP/flippix-companion-linux/app/flippix-companion"
status=0
bash "$TMP/flippix-companion-linux/install-ios-companion-linux.sh" "$@" || status=$?
exit $status
# ---- archive below ----
'@
$header = $header -replace "`r`n", "`n"
if (-not $header.EndsWith("`n")) { $header += "`n" }
$lines = ($header.ToCharArray() | Where-Object { $_ -eq "`n" }).Count
$header = $header.Replace('__LINE__', "$($lines + 1)")

$out = P $OutDir $SetupName
$fs = [IO.File]::Create($out)
try {
    $bytes = [Text.Encoding]::ASCII.GetBytes($header)
    $fs.Write($bytes, 0, $bytes.Length)
    $data = [IO.File]::ReadAllBytes($payload)
    $fs.Write($data, 0, $data.Length)
} finally { $fs.Dispose() }
Remove-Item -Recurse -Force $Work
Write-Ok "setup created: $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)"

Write-Host ''
Write-Host "Users copy $SetupName to the Ubuntu PC with the NVIDIA card and run:"
Write-Host "    bash $SetupName"
