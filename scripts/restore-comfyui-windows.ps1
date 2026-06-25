<#
.SYNOPSIS
    Restore a NATIVE WINDOWS ComfyUI golden bundle (no WSL) - the Windows sibling of
    restore-comfyui.sh.

.DESCRIPTION
    Downloads a Windows ComfyUI bundle (a portable python_embeded tree) from Hugging Face
    if it isn't already local, verifies its SHA-256, and extracts it with Windows' built-in
    tar.exe. Because the bundle carries its own embedded Python + all custom nodes, there is
    no pip / venv / node-install step -- it "just works" (needs an NVIDIA GPU + driver).

    Produce the bundle with the FlipPix "Back up this ComfyUI" button (or Backup-ComfyUI)
    pointed at a working Windows ComfyUI, then `hf upload` it under -HfFile's name.

.PARAMETER Archive
    Local .tar.gz to restore. Optional if -HfRepo is given (it is then downloaded).

.PARAMETER Target
    Folder to extract into. Default: %USERPROFILE%\ComfyUI_FlipPix

.PARAMETER HfRepo
    Hugging Face repo id to download from, e.g. yourname/flippix-comfyui.

.PARAMETER HfFile
    Filename in the repo. Default: flippix-comfyui-windows.tar.gz

.PARAMETER HfRevision
    Branch / tag / commit. Default: main

.PARAMETER Sha256
    Expected checksum. If omitted, the script tries to fetch <HfFile>.sha256 from the repo.

.EXAMPLE
    .\restore-comfyui-windows.ps1 -HfRepo yourname/flippix-comfyui
#>

[CmdletBinding()]
param(
    [string]$Archive     = '',
    [string]$Target      = (Join-Path $env:USERPROFILE 'ComfyUI_FlipPix'),
    [string]$HfRepo      = '',
    [string]$HfFile      = 'flippix-comfyui-windows.tar.gz',
    [string]$HfRevision  = 'main',
    [string]$Sha256      = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference     = 'SilentlyContinue'

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  [ok] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [!] $m"  -ForegroundColor Yellow }
function Die($m)  { Write-Host "  [x] $m"  -ForegroundColor Red; exit 1 }

# --- tools -----------------------------------------------------------------
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path $tar)) { Die 'Windows tar.exe not found (needs Windows 10 1803+).' }
$curl = Get-Command curl.exe -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $Target | Out-Null

# --- acquire the archive (download from HF if not local) -------------------
function Get-Url($url, $outFile) {
    if ($curl) {
        & $curl.Source -L --fail -C - -o $outFile $url
        if ($LASTEXITCODE -ne 0) { throw "download failed: $url" }
    } else {
        Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing
    }
}

if (-not $Archive) {
    if (-not $HfRepo) { Die 'Provide -Archive <file> or -HfRepo <user/repo>.' }
    $Archive = Join-Path $Target $HfFile
    $url = "https://huggingface.co/$HfRepo/resolve/$HfRevision/$HfFile`?download=true"
    Step "Downloading Windows bundle: $HfRepo :: $HfFile"
    Warn 'Large download (often 10-20 GB). Interrupted downloads resume on re-run.'
    try {
        Get-Url $url $Archive
    } catch {
        Die "Could not download $HfFile from $HfRepo. Is a Windows bundle published there yet? ($($_.Exception.Message))"
    }
    Ok "downloaded -> $Archive"
} elseif (-not (Test-Path $Archive)) {
    Die "archive not found: $Archive"
}

# --- verify checksum -------------------------------------------------------
if (-not $Sha256 -and $HfRepo) {
    try {
        $shaText = ''
        $shaUrl = "https://huggingface.co/$HfRepo/resolve/$HfRevision/$HfFile.sha256"
        if ($curl) { $shaText = & $curl.Source -fsSL $shaUrl 2>$null }
        else { $shaText = (Invoke-WebRequest -Uri $shaUrl -UseBasicParsing).Content }
        if ($shaText) { $Sha256 = ($shaText -split '\s+')[0] }
    } catch { }
}
if ($Sha256) {
    Step 'Verifying SHA-256'
    $got = (Get-FileHash -Algorithm SHA256 -Path $Archive).Hash.ToLower()
    if ($got -ne $Sha256.ToLower()) {
        Die "sha256 MISMATCH`n      expected $Sha256`n      got      $got`n      Delete the file and re-download."
    }
    Ok 'sha256 verified'
} else {
    Warn 'no checksum provided/found; skipping verification (pass -Sha256 to enforce)'
}

# --- extract ---------------------------------------------------------------
Step "Extracting into $Target"
& $tar -xzf $Archive -C $Target
if ($LASTEXITCODE -ne 0) { Die 'tar extraction failed.' }
Ok 'extracted'

# --- find the launcher -----------------------------------------------------
$launcher = Get-ChildItem -Path $Target -Recurse -Filter 'run_nvidia_gpu.bat' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
$portableRoot = if ($launcher) { Split-Path $launcher -Parent } else { $Target }

Write-Host "`n==================================================" -ForegroundColor Magenta
Write-Host " Windows ComfyUI restore complete" -ForegroundColor Magenta
Write-Host "==================================================" -ForegroundColor Magenta
Write-Host ""
Write-Host "Install : $portableRoot"
if ($launcher) {
    Write-Host "Launch  : run_nvidia_gpu.bat   (in the folder above)" -ForegroundColor Cyan
} else {
    Warn "run_nvidia_gpu.bat not found - launch ComfyUI the way your bundle expects."
}
Write-Host "Then point FlipPix at 127.0.0.1:8188."
Write-Host ""
Write-Host "If models weren't bundled, add weights under the install's models\ folder"
Write-Host "or use extra_model_paths.yaml."
Write-Host ""
