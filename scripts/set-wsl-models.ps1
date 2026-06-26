<#
.SYNOPSIS
    Point a WSL-hosted ComfyUI at a Windows models folder by writing extra_model_paths.yaml
    inside WSL - no editing files in the Linux shell.

.DESCRIPTION
    A ComfyUI restored into WSL (see setup-comfyui-wsl.ps1) can't see your Windows models
    until it's told where they are. Windows drives auto-mount in WSL under /mnt (E:\ -> /mnt/e),
    so this translates your Windows models folder to its /mnt path and writes a flippix entry
    into the WSL ComfyUI's extra_model_paths.yaml. Run it again any time to change folders.

.PARAMETER ModelsDir
    Your Windows models folder - the one that CONTAINS checkpoints\ loras\ vae\ unet\ etc.
    e.g.  E:\ComfyUI\models   or   E:\models

.PARAMETER Distro
    WSL distro hosting ComfyUI. Default: the WSL default distro.

.PARAMETER Restart
    After writing, restart ComfyUI in WSL so it picks up the new paths.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\set-wsl-models.ps1 -ModelsDir "E:\ComfyUI\models"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModelsDir,
    [string]$Distro = '',
    [switch]$Restart
)

$ErrorActionPreference = 'Stop'
$env:WSL_UTF8 = '1'

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [ok] $m" -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "  [!] $m"  -ForegroundColor Yellow }
function Write-Err2($m) { Write-Host "  [x] $m"  -ForegroundColor Red }

# If no distro was given, pick the one that actually hosts ~/flippix-comfyui (it may not be
# the WSL default distro, e.g. after installing Ubuntu-24.04 alongside an older Ubuntu).
if (-not $Distro) {
    $all = @(& wsl.exe -l -q 2>$null | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($d in $all) {
        $has = & wsl.exe -d $d -- bash -lc 'test -f "$HOME/flippix-comfyui/ComfyUI/main.py" -o -f "$HOME/flippix-comfyui/main.py" && echo yes' 2>$null
        if ("$has".Trim() -eq 'yes') { $Distro = $d; break }
    }
}
$DistroArgs = if ($Distro) { @('-d', $Distro) } else { @() }

function Invoke-Wsl([string]$BashCommand) {
    $out = & wsl.exe @DistroArgs -- bash -lc $BashCommand
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed (exit $LASTEXITCODE): $BashCommand" }
    return ($out | Out-String).Trim()
}

# Windows path -> WSL /mnt path (E:\ComfyUI\models -> /mnt/e/ComfyUI/models).
function ConvertTo-WslMntPath([string]$winPath) {
    $p = $winPath.Trim().Trim('"')
    if ($p -match '^([A-Za-z]):[\\/]?(.*)$') {
        $drive = $matches[1].ToLower()
        $rest  = ($matches[2] -replace '\\', '/').Trim('/')
        if ($rest) { return "/mnt/$drive/$rest" } else { return "/mnt/$drive" }
    }
    return $null   # UNC / non-drive paths aren't auto-mounted in WSL
}

Write-Host "FlipPix - point WSL ComfyUI at a Windows models folder" -ForegroundColor Magenta

# --- validate the Windows folder ---
if (-not (Test-Path -LiteralPath $ModelsDir)) {
    Write-Warn2 "models folder not found on Windows: $ModelsDir  (continuing; check the path)"
}
$wslModels = ConvertTo-WslMntPath $ModelsDir
if (-not $wslModels) {
    Write-Err2 "Couldn't map '$ModelsDir' to a WSL path. Use a drive path like E:\ComfyUI\models (UNC \\server paths aren't auto-mounted in WSL)."
    exit 1
}
Write-Ok "Windows '$ModelsDir'  ->  WSL '$wslModels'"

# --- check WSL can see it (drive auto-mounted, folder exists) ---
$seen = Invoke-Wsl "[ -d '$wslModels' ] && echo yes || echo no"
if ($seen -ne 'yes') {
    Write-Err2 "WSL can't see '$wslModels'."
    Write-Host  "  - Make sure the drive is connected. Fixed drives auto-mount; a removable/USB" -ForegroundColor Yellow
    Write-Host  "    drive may need:  sudo mkdir -p $wslModels ; sudo mount -t drvfs $($ModelsDir.Substring(0,2)) /mnt/$($ModelsDir.Substring(0,1).ToLower())"
    exit 1
}
# Report which model subfolders are present (purely informational).
$found = Invoke-Wsl "ls -1 '$wslModels' 2>/dev/null | tr '\n' ' '"
Write-Ok "WSL sees the folder. Contains: $found"
if ($found -notmatch 'checkpoints|loras|vae|unet|diffusion_models|text_encoders|controlnet') {
    Write-Warn2 "none of the usual model subfolders (checkpoints/loras/vae/unet/...) were found here."
    Write-Warn2 "Point -ModelsDir at the folder that CONTAINS those subfolders."
}

# --- locate the ComfyUI base dir (where main.py + extra_model_paths.yaml live) ---
$comfyBase = Invoke-Wsl 'if [ -f "$HOME/flippix-comfyui/ComfyUI/main.py" ]; then echo "$HOME/flippix-comfyui/ComfyUI"; elif [ -f "$HOME/flippix-comfyui/main.py" ]; then echo "$HOME/flippix-comfyui"; else echo ""; fi'
if (-not $comfyBase) { Write-Err2 "Couldn't find the WSL ComfyUI under ~/flippix-comfyui. Run the WSL setup first."; exit 1 }
Write-Ok "ComfyUI base: $comfyBase"

# --- write extra_model_paths.yaml (LF, written inside WSL) ---
Write-Step 'Writing extra_model_paths.yaml'
$yaml = @"
flippix:
    base_path: '$wslModels'
    checkpoints: checkpoints
    clip: clip
    clip_vision: clip_vision
    controlnet: controlnet
    diffusion_models: diffusion_models
    unet: unet
    loras: loras
    vae: vae
    text_encoders: text_encoders
    upscale_models: upscale_models
"@
($yaml -replace "`r", '') | & wsl.exe @DistroArgs -- bash -c "cat > '$comfyBase/extra_model_paths.yaml'"
if ($LASTEXITCODE -ne 0) { Write-Err2 'failed to write extra_model_paths.yaml in WSL.'; exit 1 }
Write-Ok "wrote $comfyBase/extra_model_paths.yaml -> $wslModels"

if ($Restart) {
    Write-Step 'Restarting ComfyUI in WSL'
    Invoke-Wsl "pkill -f 'ComfyUI/main.py' 2>/dev/null; sleep 1; cd ~/flippix-comfyui && nohup ./run.sh > ~/comfyui.log 2>&1 & disown" | Out-Null
    Write-Ok 'restart requested (logs: ~/comfyui.log)'
} else {
    Write-Host ''
    Write-Host 'Restart ComfyUI so it picks up the models (close its window and relaunch,' -ForegroundColor Cyan
    Write-Host 'or re-run with -Restart).' -ForegroundColor Cyan
}
