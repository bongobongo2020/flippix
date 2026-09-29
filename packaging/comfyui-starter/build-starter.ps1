<#
.SYNOPSIS
    Builds the FlipPix starter ComfyUI: the official Windows portable build plus exactly the node
    packs the phone remote needs, at pinned commits, with their Python dependencies installed.

.DESCRIPTION
    Output: release\comfyui-starter\flippix-starter-windows.7z (+ .sha256). The archive holds one
    folder, ComfyUI_FlipPix\, which Setup extracts to %USERPROFILE%\ComfyUI_FlipPix:

        ComfyUI_FlipPix\
          python_embeded\         torch/CUDA, from the portable build, plus the packs' pip deps
          ComfyUI\custom_nodes\   the packs in starter.json, without their .git folders
          run_flippix.bat         what FlipPix auto-starts (no browser tab, localhost only)
          flippix-starter.json    the manifest it was built from

    Nothing downloads models; the FlipPix Models window (scripts\flippix-models.ps1) does that.

    Verification starts the result on the CPU and checks that every node class the phone submits
    is registered (tools\starter_manifest.py). A pack whose import needs a GPU can fail on a
    machine without one; the script lists what failed so that can be told apart from a real break.

.PARAMETER SkipVerify
    Don't start ComfyUI to check the node classes.

.PARAMETER NoArchive
    Stop after staging; don't write the .7z.
#>
[CmdletBinding()]
param(
    [switch]$SkipVerify,
    [switch]$NoArchive
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [ok] $m" -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "  [!] $m" -ForegroundColor Yellow }

$Here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $Here '..\..')
$Manifest = Get-Content (Join-Path $Here 'starter.json') -Raw | ConvertFrom-Json
$Work     = Join-Path $RepoRoot 'release\comfyui-starter'
$Cache    = Join-Path $Work 'cache'
$Stage    = Join-Path $Work 'stage'
$Root     = Join-Path $Stage 'ComfyUI_FlipPix'
New-Item -ItemType Directory -Force -Path $Cache | Out-Null

function Get-Cached($url, $name) {
    $dst = Join-Path $Cache $name
    if (-not (Test-Path $dst)) {
        Write-Host "  downloading $name"
        & curl.exe -L --fail --retry 5 -C - -o "$dst.part" $url
        if ($LASTEXITCODE -ne 0) { throw "download failed: $url" }
        Move-Item "$dst.part" $dst
    }
    return $dst
}

# ---------------------------------------------------------------------------
Write-Step "ComfyUI $($Manifest.comfyui.tag) portable"
$seven = Get-Cached 'https://www.7-zip.org/a/7zr.exe' '7zr.exe'
$portable = Get-Cached $Manifest.comfyui.url $Manifest.comfyui.asset

if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null
& $seven x $portable "-o$Stage" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw "7zr could not extract $portable" }
$extracted = Get-ChildItem $Stage -Directory | Select-Object -First 1
Rename-Item $extracted.FullName 'ComfyUI_FlipPix'
$py = Join-Path $Root 'python_embeded\python.exe'
if (-not (Test-Path $py)) { throw "no python_embeded in the portable build: $Root" }
Write-Ok "extracted to $Root"

# ---------------------------------------------------------------------------
Write-Step 'Node packs (pinned)'
$nodesDir = Join-Path $Root 'ComfyUI\custom_nodes'
foreach ($p in $Manifest.packs) {
    $dst = Join-Path $nodesDir $p.dir
    & git clone --quiet --filter=blob:none $p.url $dst
    if ($LASTEXITCODE -ne 0) { throw "git clone failed: $($p.url)" }
    & git -C $dst -c advice.detachedHead=false checkout --quiet $p.commit
    if ($LASTEXITCODE -ne 0) { throw "commit $($p.commit) not found in $($p.url)" }
    Remove-Item -Recurse -Force (Join-Path $dst '.git')
    Write-Ok "$($p.dir) @ $($p.commit.Substring(0,8))"
}

# ---------------------------------------------------------------------------
# Only the packages each pack's manifest entry names, never a pack's requirements.txt: one of those
# once pulled a CPU torch over the CUDA one. torch and friends are pinned to what the portable build
# ships, and PYTHONNOUSERSITE keeps a per-user site-packages from shadowing anything.
Write-Step 'Python dependencies'
$env:PYTHONNOUSERSITE = '1'
$constraints = Join-Path $Work 'constraints.txt'
# pip freeze prints the portable build's wheels as "torch @ file:///..."; list gives name==version.
& $py -s -m pip list --format=freeze | Where-Object { $_ -match '^(torch|torchvision|torchaudio|numpy|pillow|triton-windows)==' } |
    Set-Content -Encoding ascii $constraints
if (-not (Test-Path $constraints) -or -not (Select-String -Path $constraints -Pattern '^torch==' -Quiet)) {
    throw 'could not read the installed torch version to hold it in place'
}
Write-Host ("  holding: " + ((Get-Content $constraints) -join ', '))

$deps = @($Manifest.packs | ForEach-Object { $_.pip } | Where-Object { $_ } | Sort-Object -Unique)
$indexes = @($Manifest.packs | Where-Object { $_.pipIndex } | ForEach-Object { '--extra-index-url'; $_.pipIndex })
& $py -s -m pip install --no-cache-dir --no-warn-script-location -c $constraints @indexes @deps
if ($LASTEXITCODE -ne 0) { throw 'pip install failed' }
Write-Ok ("installed: " + ($deps -join ', '))

# ---------------------------------------------------------------------------
Write-Step 'Launcher + manifest'
# --windows-standalone-build would open a browser tab each time FlipPix starts ComfyUI.
@'
@echo off
rem Started by FlipPix (Settings > ComfyUI restart script). Double-click to run it yourself.
cd /d "%~dp0"
.\python_embeded\python.exe -s ComfyUI\main.py --listen 127.0.0.1 --port 8188 --disable-auto-launch %*
'@ | Set-Content -Encoding ascii (Join-Path $Root 'run_flippix.bat')
Copy-Item (Join-Path $Here 'starter.json') (Join-Path $Root 'flippix-starter.json')
Write-Ok 'run_flippix.bat'

# ---------------------------------------------------------------------------
if (-not $SkipVerify) {
    Write-Step 'Verify: start on the CPU and check every node class the phone submits'
    $port = 8199
    $log = Join-Path $Work 'verify.log'
    $proc = Start-Process -FilePath $py -WorkingDirectory $Root -PassThru -WindowStyle Hidden `
        -ArgumentList @('-s', 'ComfyUI\main.py', '--cpu', '--listen', '127.0.0.1', '--port', $port, '--disable-auto-launch') `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    try {
        $up = $false
        for ($i = 0; $i -lt 180 -and -not $proc.HasExited; $i++) {
            Start-Sleep -Seconds 2
            try { Invoke-WebRequest "http://127.0.0.1:$port/system_stats" -UseBasicParsing -TimeoutSec 3 | Out-Null; $up = $true; break } catch {}
        }
        if (-not $up) { throw "ComfyUI did not come up; see $log.err" }
        $failed = Select-String -Path "$log.err", $log -Pattern 'IMPORT FAILED|Cannot import' -ErrorAction SilentlyContinue
        foreach ($f in $failed) { Write-Warn2 $f.Line.Trim() }
        & python (Join-Path $RepoRoot 'tools\starter_manifest.py') --server "http://127.0.0.1:$port"
        $verifyExit = $LASTEXITCODE
    } finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
        Start-Sleep -Seconds 2
    }
    if ($verifyExit -ne 0) { throw "the starter is missing node classes the phone submits (see above)" }
    Write-Ok 'every node class the phone submits is registered'

    # Starting it wrote caches and user settings into the stage; ship it as it was built.
    foreach ($d in 'ComfyUI\user', 'ComfyUI\temp') {
        $p = Join-Path $Root $d; if (Test-Path $p) { Remove-Item -Recurse -Force $p }
    }
}
Get-ChildItem $Root -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force

if ($NoArchive) { Write-Ok "staged at $Root"; return }

# ---------------------------------------------------------------------------
Write-Step 'Archive'
$out = Join-Path $Work $Manifest.bundle.file
if (Test-Path $out) { Remove-Item -Force $out }
& $seven a -t7z -mx=7 -mmt=on $out $Root | Out-Null
if ($LASTEXITCODE -ne 0) { throw '7zr could not write the archive' }
$hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
"$hash  $($Manifest.bundle.file)" | Set-Content -Encoding ascii "$out.sha256"
$gb = [math]::Round((Get-Item $out).Length / 1GB, 2)
Write-Ok "$out ($gb GB)"
Write-Host "`nPublish it with: packaging\comfyui-starter\publish-starter.ps1"
