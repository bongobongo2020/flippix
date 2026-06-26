<#
.SYNOPSIS
    FlipPix one-click: download a prebuilt ComfyUI snapshot from Hugging Face and run it in WSL2,
    then point FlipPix at it - no 1TB of model downloads, no Windows portable/VC++ pain.

.DESCRIPTION
    Friction-free path for users who don't already have ComfyUI:
      1. Ensure WSL2 + a Linux distro exist (auto-install with `wsl --install` if missing; this
         needs admin + a reboot, after which you re-run this script).
      2. Run scripts/restore-comfyui.sh INSIDE WSL to pull the snapshot from the HF repo
         (default: bongo2k22/flippix-comfyui), extract it, and write a launcher.
      3. Launch ComfyUI in WSL (listening on 0.0.0.0:<port>) and wait for it to come up.
      4. Write FlipPix settings so the app talks to the WSL ComfyUI (BaseUrl + remote output
         folder via the \\wsl.localhost UNC path) and can relaunch it.

    The heavy lifting (download/verify/extract/launcher) is done by restore-comfyui.sh; this
    script is the Windows-side orchestration only.

.PARAMETER HfRepo
    Hugging Face repo holding the snapshot. Default: bongo2k22/flippix-comfyui

.PARAMETER Distro
    WSL distro to use. Default: the WSL default distro.

.PARAMETER Port
    Port ComfyUI listens on (and that FlipPix connects to). Default: 8188

.PARAMETER NoLaunch
    Restore only; don't start ComfyUI or wait for it.

.PARAMETER NoFlipPixSettings
    Don't modify %APPDATA%\FlipPix\settings.json.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\setup-comfyui-wsl.ps1
#>
[CmdletBinding()]
param(
    [string]$HfRepo = 'bongo2k22/flippix-comfyui',
    [string]$Distro  = '',
    [int]$Port = 8188,
    [string]$MinGlibc = '2.38',   # the snapshot's bundled python_embeded is built against this
    [switch]$NoLaunch,
    [switch]$NoFlipPixSettings
)

$ErrorActionPreference = 'Stop'
$env:WSL_UTF8 = '1'   # make wsl.exe emit UTF-8 (not UTF-16) so we can parse its output

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [ok] $m" -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "  [!] $m"  -ForegroundColor Yellow }
function Write-Err2($m) { Write-Host "  [x] $m"  -ForegroundColor Red }

$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$RestoreSh   = Join-Path $ScriptDir 'restore-comfyui.sh'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

# Distro selector for wsl.exe (@() = default distro).
$DistroArgs = if ($Distro) { @('-d', $Distro) } else { @() }

function Invoke-Wsl {
    # Run a bash command inside WSL (login shell so PATH is set) and return trimmed stdout.
    param([string]$BashCommand)
    $out = & wsl.exe @DistroArgs -- bash -lc $BashCommand
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed (exit $LASTEXITCODE): $BashCommand" }
    return ($out | Out-String).Trim()
}

# ---------------------------------------------------------------------------
# 1. ensure WSL2 + a distro
# ---------------------------------------------------------------------------
function Get-WslDistros {
    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if (-not $wsl) { return @() }
    try {
        return @(& wsl.exe -l -q 2>$null | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    } catch { return @() }
}

Write-Host "FlipPix - ComfyUI in WSL (Hugging Face snapshot)" -ForegroundColor Magenta
Write-Step 'Checking for WSL2'

$distros = Get-WslDistros
if ($distros.Count -eq 0) {
    Write-Warn2 'No WSL distro found.'
    if (-not (Test-Admin)) {
        Write-Err2 "Installing WSL needs an elevated terminal. Right-click PowerShell > 'Run as administrator', then re-run this script."
        exit 1
    }
    # Install Ubuntu 24.04 specifically: the snapshot's bundled Python needs glibc >= 2.38,
    # which 22.04 (glibc 2.35) doesn't have. 24.04 ships glibc 2.39.
    Write-Step 'Installing WSL2 + Ubuntu 24.04 (wsl --install -d Ubuntu-24.04)'
    & wsl.exe --install -d Ubuntu-24.04
    Write-Host ''
    Write-Warn2 'WSL was installed. You must now:'
    Write-Host  '    1. REBOOT Windows.'
    Write-Host  '    2. Launch "Ubuntu 24.04" once and create your Linux username + password.'
    Write-Host  '    3. Re-run this script to download and start ComfyUI.'
    exit 0
}
if ($Distro -and ($distros -notcontains $Distro)) {
    Write-Err2 "Distro '$Distro' not found. Installed: $($distros -join ', ')"
    exit 1
}
$ActiveDistro = if ($Distro) { $Distro } else { $distros[0] }
Write-Ok "using WSL distro: $ActiveDistro"

# Sanity-check the distro actually runs.
try { $null = Invoke-Wsl 'true' }
catch { Write-Err2 "WSL distro '$ActiveDistro' isn't ready. Launch it once to finish first-run setup, then re-run."; exit 1 }

# glibc preflight: the snapshot bundles a portable Python built against glibc >= $MinGlibc.
# An older distro (e.g. Ubuntu 22.04 = glibc 2.35) can't run it ("GLIBC_2.38 not found").
# Check BEFORE the large download so we fail fast with actionable guidance.
function Get-WslGlibc {
    try {
        $v = Invoke-Wsl 'getconf GNU_LIBC_VERSION 2>/dev/null || ldd --version 2>/dev/null | head -1'
        if ($v -match '(\d+)\.(\d+)') { return [version]("$($matches[1]).$($matches[2])") }
    } catch {}
    return $null
}
$glibc = Get-WslGlibc
if ($glibc) {
    if ($glibc -lt [version]$MinGlibc) {
        Write-Err2 "Distro '$ActiveDistro' has glibc $glibc, but the snapshot's bundled Python needs glibc $MinGlibc+."
        Write-Host  '  This fails with "GLIBC_2.38 not found" at launch. Use a newer distro:' -ForegroundColor Yellow
        Write-Host  '      wsl --install -d Ubuntu-24.04'
        Write-Host  '  finish its first-run, then re-run:'
        Write-Host  '      Install-ComfyUI-WSL.bat -Distro Ubuntu-24.04'
        exit 1
    }
    Write-Ok "glibc $glibc (>= $MinGlibc required)"
} else {
    Write-Warn2 "couldn't determine the distro's glibc; continuing. If launch fails with a GLIBC error, use Ubuntu-24.04."
}

# ---------------------------------------------------------------------------
# 2. run restore-comfyui.sh inside WSL (downloads the HF snapshot)
# ---------------------------------------------------------------------------
if (-not (Test-Path $RestoreSh)) { Write-Err2 "restore script not found: $RestoreSh"; exit 1 }

Write-Step "Restoring ComfyUI from Hugging Face ($HfRepo) inside WSL"
Write-Warn2 'This downloads the snapshot (large) and extracts it under ~/flippix-comfyui in WSL.'

# Pipe the script in via stdin (CR-stripped) so Windows line endings / paths can't break bash.
$restoreBody = (Get-Content $RestoreSh -Raw) -replace "`r", ''
$restoreBody | & wsl.exe @DistroArgs -- bash -s -- --hf $HfRepo
if ($LASTEXITCODE -ne 0) { Write-Err2 "restore-comfyui.sh failed inside WSL (exit $LASTEXITCODE)."; exit 1 }
Write-Ok 'snapshot restored in WSL'

# Resolve WSL identity/paths for launcher + FlipPix settings.
$LinuxUser = Invoke-Wsl 'whoami'
$TargetDir = Invoke-Wsl 'echo "$HOME/flippix-comfyui"'
# Pick the non-interactive launcher the restore wrote (run.sh), else the interactive one.
$LaunchRel = Invoke-Wsl 'if [ -f "$HOME/flippix-comfyui/run.sh" ]; then echo run.sh; elif [ -f "$HOME/flippix-comfyui/run_nvidia_gpu.sh" ]; then echo run_nvidia_gpu.sh; else echo ""; fi'
if (-not $LaunchRel) { Write-Warn2 'no run.sh / run_nvidia_gpu.sh found after restore; you may need to launch ComfyUI manually.' }

# ---------------------------------------------------------------------------
# 3. a Windows launcher that starts the WSL ComfyUI (manual + FlipPix auto-restart)
# ---------------------------------------------------------------------------
$AppDir = Join-Path $env:APPDATA 'FlipPix'
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
$LauncherBat = Join-Path $AppDir 'Start-ComfyUI-WSL.bat'
$distroFlag  = if ($Distro) { "-d $Distro " } else { '' }
$launchCmd   = if ($LaunchRel) { "cd ~/flippix-comfyui && ./$LaunchRel" } else { 'cd ~/flippix-comfyui && ./run.sh' }
@"
@echo off
REM Launches the WSL-hosted ComfyUI restored by setup-comfyui-wsl.ps1.
wsl.exe $distroFlag-- bash -lc "$launchCmd"
"@ | Set-Content -Path $LauncherBat -Encoding ascii
Write-Ok "wrote launcher: $LauncherBat"

# ---------------------------------------------------------------------------
# 4. launch + wait for readiness
# ---------------------------------------------------------------------------
$WslIp = ''
try { $WslIp = (Invoke-Wsl 'hostname -I').Split(' ')[0].Trim() } catch {}
# WSL2 forwards listening sockets to localhost; prefer localhost, fall back to the WSL IP.
$candidates = @("http://localhost:$Port")
if ($WslIp) { $candidates += "http://${WslIp}:$Port" }

$ReadyUrl = ''
if (-not $NoLaunch -and $LaunchRel) {
    Write-Step 'Launching ComfyUI in WSL'
    # Detached so it survives this script; logs to ~/comfyui.log.
    & wsl.exe @DistroArgs -- bash -lc "cd ~/flippix-comfyui && nohup ./$LaunchRel > ~/comfyui.log 2>&1 & disown" | Out-Null
    Write-Ok 'ComfyUI starting (logs: ~/comfyui.log in WSL)'

    Write-Step 'Waiting for ComfyUI to respond (up to 180s)'
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline -and -not $ReadyUrl) {
        foreach ($u in $candidates) {
            try {
                $r = Invoke-WebRequest -Uri "$u/system_stats" -TimeoutSec 4 -UseBasicParsing
                if ($r.StatusCode -eq 200) { $ReadyUrl = $u; break }
            } catch {}
        }
        if (-not $ReadyUrl) { Start-Sleep -Seconds 4 }
    }
    if ($ReadyUrl) { Write-Ok "ComfyUI is up at $ReadyUrl" }
    else { Write-Warn2 "ComfyUI didn't respond yet. It may still be loading models; check ~/comfyui.log in WSL." }
}

# Choose the URL to save: the one that answered, else localhost (WSL usually forwards it).
$BaseUrl = if ($ReadyUrl) { $ReadyUrl } else { "http://localhost:$Port" }

# ---------------------------------------------------------------------------
# 5. point FlipPix at the WSL ComfyUI
# ---------------------------------------------------------------------------
if (-not $NoFlipPixSettings) {
    Write-Step 'Pointing FlipPix at the WSL ComfyUI'
    try {
        # ComfyUI's output folder is reachable from Windows via the WSL UNC share.
        $uncOutput = "\\wsl.localhost\$ActiveDistro\home\$LinuxUser\flippix-comfyui\output"

        $file = Join-Path $AppDir 'settings.json'
        $settings = $null
        if (Test-Path $file) {
            try { $settings = Get-Content $file -Raw | ConvertFrom-Json } catch { $settings = $null }
        }
        if (-not $settings) { $settings = [PSCustomObject]@{} }

        $settings | Add-Member -NotePropertyName 'BaseUrl'                  -NotePropertyValue $BaseUrl -Force
        $settings | Add-Member -NotePropertyName 'RemoteOutputFolderPath'   -NotePropertyValue $uncOutput -Force
        $settings | Add-Member -NotePropertyName 'AutoRestartComfyUI'       -NotePropertyValue $true -Force
        $settings | Add-Member -NotePropertyName 'ComfyUIRestartScriptPath' -NotePropertyValue $LauncherBat -Force
        # A WSL ComfyUI is "remote" to FlipPix; clear any stale local folder so it uses the remote path.
        $settings | Add-Member -NotePropertyName 'ComfyUIFolderPath'        -NotePropertyValue '' -Force

        $settings | ConvertTo-Json -Depth 32 | Set-Content -Path $file -Encoding UTF8
        Write-Ok "FlipPix BaseUrl = $BaseUrl"
        Write-Ok "FlipPix remote output = $uncOutput"
    } catch {
        Write-Warn2 "could not update FlipPix settings: $($_.Exception.Message)"
    }
}

Write-Host "`n==================================================" -ForegroundColor Magenta
Write-Host " ComfyUI (WSL) ready" -ForegroundColor Magenta
Write-Host "==================================================" -ForegroundColor Magenta
Write-Host "Snapshot     : $HfRepo"
Write-Host "WSL location : $TargetDir  (distro: $ActiveDistro)"
Write-Host "Server URL   : $BaseUrl"
Write-Host "Relaunch     : double-click $LauncherBat  (or: wsl ${distroFlag}-- bash -lc '$launchCmd')"
Write-Host ""
Write-Host "Start FlipPix - it's already pointed at this ComfyUI." -ForegroundColor Cyan
