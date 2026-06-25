<#
.SYNOPSIS
    Snapshot a REMOTE ComfyUI install over SSH and download it to this Windows PC
    as a single, restore-anywhere bundle.

.DESCRIPTION
    Connects to a remote Linux box over SSH (default: x2@192.168.1.10), where a
    ComfyUI install (a portable "ComfyUI-Easy-Install" tree with a bundled
    python_embeded), and streams a compressed snapshot of that install straight to a
    .tar.gz on this PC -- one SSH connection, no temp file on the remote, no mangling.

    Because the snapshot includes python_embeded (the whole Python environment with
    every dependency already installed), restoring is just "extract + run" -- no pip,
    no venv rebuild, no dependency drift. That is the lowest-friction "it just works"
    path for a new FlipPix machine. The snapshot contains:
      - python_embeded (portable Python env -- all deps for all custom nodes)
      - the ComfyUI source + every custom_nodes pack (their code + git, kept as-is)
      - the bundled launchers (run_nvidia_gpu.sh, etc.) and user/ workflows + settings
      - a manifest: ComfyUI commit, python version, every node's git remote+commit
      - requirements-freeze.txt (pip freeze of the live env) for reference

    If the install uses a venv instead of python_embeded, the venv is excluded and the
    restore script rebuilds it from requirements.

    Excluded by default (regenerable, machine-specific, or external):
      __pycache__, *.pyc, output/, temp/, venv/.venv, the saved run-config dotfiles
      (.output_dir_config / .listen_address_config / .vram_config), and models/ (often
      a symlink to external storage; opt in with -IncludeModels).

    Alongside the .tar.gz it drops restore-comfyui.sh + RESTORE-README.md, so the
    output folder is a self-contained bundle: copy it to any Ubuntu / WSL machine and
    run the restore script. Models are optional there too.

.PARAMETER RemoteHost
    SSH target host. Default: 192.168.1.10

.PARAMETER User
    SSH user. Default: x2

.PARAMETER RemotePath
    Path to the portable ComfyUI install on the remote (the folder that contains
    python_embeded + ComfyUI). The ComfyUI root inside it is auto-detected. A leading
    ~ is expanded remotely. Default: ~/jun1/ComfyUI-Easy-Install

.PARAMETER OutDir
    Local folder to write the bundle into. Default: %USERPROFILE%\FlipPix-ComfyUI-Backup

.PARAMETER IncludeModels
    Also include the models/ folder (can be tens of GB). Off by default.

.PARAMETER Port
    SSH port. Default: 22

.PARAMETER IdentityFile
    Path to a private key to authenticate with (passed to ssh -i). Optional; if omitted
    ssh uses your default keys / agent, or prompts for a password.

.PARAMETER HfUpload
    After the bundle is created, publish it (the .tar.gz + .sha256) to a Hugging Face
    model repo so users can restore with one command. Requires the Hugging Face CLI
    (`hf` or `huggingface-cli`) and a prior `hf auth login` (or HF_TOKEN env var).
    Requires -HfRepo.

.PARAMETER HfRepo
    Target Hugging Face repo id for -HfUpload, e.g. yourname/flippix-comfyui. The repo
    is created automatically if it doesn't exist (under your account).

.PARAMETER HfRemoteName
    The filename to upload the snapshot AS in the repo (the .sha256 gets the same name
    with .sha256 appended). Using a stable name lets users omit --hf-file on restore.
    Default: flippix-comfyui.tar.gz  (or flippix-comfyui-windows.tar.gz with -Windows)

.PARAMETER Windows
    Produce a NATIVE WINDOWS bundle from a LOCAL Windows ComfyUI (a portable
    python_embeded tree) instead of streaming the remote Linux install over SSH. Uses
    Windows' built-in tar.exe. The bundle is labelled "windows" and, with -HfUpload,
    published as flippix-comfyui-windows.tar.gz so the Windows restore path finds it.

.PARAMETER LocalPath
    With -Windows: the local Windows ComfyUI folder to snapshot (the one containing
    python_embeded + ComfyUI, e.g. ...\ComfyUI_windows_portable). If omitted, a few
    common locations are auto-detected.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\backup-comfyui-remote.ps1

.EXAMPLE
    # include the model weights too, write to D:\backups
    .\scripts\backup-comfyui-remote.ps1 -IncludeModels -OutDir D:\backups

.EXAMPLE
    # back up AND publish to Hugging Face in one go
    .\scripts\backup-comfyui-remote.ps1 -HfUpload -HfRepo yourname/flippix-comfyui

.EXAMPLE
    # different box / path / key
    .\scripts\backup-comfyui-remote.ps1 -User bob -RemoteHost 10.0.0.5 -RemotePath ~/ComfyUI -IdentityFile $env:USERPROFILE\.ssh\id_ed25519
#>

[CmdletBinding()]
param(
    [string]$RemoteHost   = '192.168.1.10',
    [string]$User         = 'x2',
    [string]$RemotePath   = '~/jun1/ComfyUI-Easy-Install',
    [string]$OutDir       = (Join-Path $env:USERPROFILE 'FlipPix-ComfyUI-Backup'),
    [switch]$IncludeModels,
    [int]$Port            = 22,
    [string]$IdentityFile = '',
    [switch]$HfUpload,
    [string]$HfRepo       = '',
    [string]$HfRemoteName = '',
    [switch]$Windows,
    [string]$LocalPath    = ''
)

$ErrorActionPreference = 'Stop'

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [ok] $m" -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "  [!] $m"  -ForegroundColor Yellow }
function Write-Err2($m) { Write-Host "  [x] $m"  -ForegroundColor Red }

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Stable upload name: Windows bundles get a distinct name so the Windows restore finds them.
if (-not $HfRemoteName) {
    $HfRemoteName = if ($Windows) { 'flippix-comfyui-windows.tar.gz' } else { 'flippix-comfyui.tar.gz' }
}

# ---------------------------------------------------------------------------
# preflight: OpenSSH client (remote backups only)
# ---------------------------------------------------------------------------
$ssh = $null
if (-not $Windows) {
    $ssh = Get-Command ssh.exe -ErrorAction SilentlyContinue
    if (-not $ssh) {
        Write-Err2 'ssh.exe not found. Enable the built-in OpenSSH client:'
        Write-Host '    Settings > System > Optional features > Add a feature > "OpenSSH Client"'
        Write-Host '    (or: Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0)'
        exit 1
    }
}

# ---------------------------------------------------------------------------
# preflight: Hugging Face CLI (fail fast BEFORE the long backup if -HfUpload)
# ---------------------------------------------------------------------------
$HfCli = $null
if ($HfUpload) {
    if (-not $HfRepo) {
        Write-Err2 '-HfUpload requires -HfRepo (e.g. -HfRepo yourname/flippix-comfyui).'
        exit 1
    }
    $HfCli = (Get-Command hf.exe, hf, huggingface-cli.exe, huggingface-cli -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not $HfCli) {
        Write-Err2 'Hugging Face CLI not found (needed for -HfUpload).'
        Write-Host '    Install:  pip install -U "huggingface_hub[cli]"'
        Write-Host '    Then:     hf auth login        (or set HF_TOKEN in the environment)'
        exit 1
    }
    Write-Ok "Hugging Face CLI: $($HfCli.Source)  ->  will publish to $HfRepo as $HfRemoteName"
}

# ---------------------------------------------------------------------------
# remote snapshot script (runs on the Linux box; streams a .tar.gz to stdout)
# Single-quoted here-string => bash $vars are NOT touched by PowerShell.
# Receives:  $1 = ComfyUI path,  $2 = include-models flag (0/1)
# IMPORTANT: only the tarball goes to stdout. All status goes to stderr.
# ---------------------------------------------------------------------------
$RemoteScript = @'
set -euo pipefail

COMFY_DIR="${1:-$HOME/jun1/ComfyUI-Easy-Install}"
INCLUDE_MODELS="${2:-0}"
# expand a leading ~ ourselves (in case it arrived quoted)
COMFY_DIR="${COMFY_DIR/#\~/$HOME}"

if [ ! -d "$COMFY_DIR" ]; then
    echo "ERROR: remote path not found: $COMFY_DIR" >&2
    exit 2
fi

# Locate the ComfyUI root (the dir with main.py): COMFY_DIR itself or COMFY_DIR/ComfyUI.
if [ -f "$COMFY_DIR/main.py" ]; then
    ROOT="$COMFY_DIR"
elif [ -f "$COMFY_DIR/ComfyUI/main.py" ]; then
    ROOT="$COMFY_DIR/ComfyUI"
else
    ROOT="$COMFY_DIR"
    echo ">> warning: main.py not found under $COMFY_DIR; archiving it as-is" >&2
fi

# Detect the Python environment: a bundled python_embeded (portable -- travels in the
# archive), else a venv (excluded; rebuilt on restore).
PYEMBED=""
for c in "$COMFY_DIR/python_embeded" "$ROOT/python_embeded" "$ROOT/../python_embeded"; do
    if [ -x "$c/bin/python3" ] || [ -x "$c/python" ]; then PYEMBED="$c"; break; fi
done
PYBIN=""
if [ -n "$PYEMBED" ]; then
    [ -x "$PYEMBED/bin/python3" ] && PYBIN="$PYEMBED/bin/python3" || PYBIN="$PYEMBED/python"
    echo ">> portable python_embeded detected -> bundling the whole environment" >&2
else
    for c in "$ROOT/venv" "$ROOT/.venv" "$COMFY_DIR/venv" "$COMFY_DIR/.venv"; do
        if [ -x "$c/bin/python" ]; then PYBIN="$c/bin/python"; break; fi
    done
    echo ">> no python_embeded; venv will be excluded and rebuilt on restore" >&2
fi

# Stage generated metadata inside the tree so it travels with the archive.
STAGE="$COMFY_DIR/.flippix-backup"
rm -rf "$STAGE"; mkdir -p "$STAGE"

{
    echo "# FlipPix ComfyUI backup manifest"
    echo "date=$(date -u +%FT%TZ)"
    echo "host=$(hostname)"
    echo "comfy_dir=$COMFY_DIR"
    echo "root=$ROOT"
    echo "python_embeded=${PYEMBED:-none}"
    echo "python=$([ -n "$PYBIN" ] && "$PYBIN" --version 2>&1 || python3 --version 2>&1 || echo unknown)"
    echo "include_models=$INCLUDE_MODELS"
    if [ -d "$ROOT/.git" ]; then
        echo "comfyui_commit=$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
    fi
} > "$STAGE/manifest.txt"

# Every custom node + its git origin/commit (so restore can verify / re-pull).
NODES="$ROOT/custom_nodes"
if [ -d "$NODES" ]; then
    for d in "$NODES"/*/; do
        [ -d "$d" ] || continue
        name=$(basename "$d")
        url=""; commit=""
        if [ -d "$d/.git" ]; then
            url=$(git -C "$d" config --get remote.origin.url 2>/dev/null || true)
            commit=$(git -C "$d" rev-parse HEAD 2>/dev/null || true)
        fi
        echo "$name|$url|$commit"
    done > "$STAGE/custom_nodes.txt"
    echo ">> $(grep -c '' "$STAGE/custom_nodes.txt") custom node pack(s) found" >&2
fi

# pip freeze (best-effort), for reference / venv rebuilds.
if [ -n "$PYBIN" ]; then
    "$PYBIN" -m pip freeze > "$STAGE/requirements-freeze.txt" 2>/dev/null || true
    echo ">> captured pip freeze from $PYBIN" >&2
fi

# Build tar excludes (regenerable / machine-specific / external).
EXCL=(
    --exclude='__pycache__' --exclude='*/__pycache__' --exclude='*.pyc'
    --exclude='./output' --exclude='./ComfyUI/output'
    --exclude='./temp' --exclude='./ComfyUI/temp'
    --exclude='./ComfyUI/user/default/ComfyUI-Manager/cache'
    --exclude='./.output_dir_config' --exclude='./.listen_address_config'
    --exclude='./.vram_config' --exclude='./.comfyui_flags_config'
)
# Only exclude a venv if we are NOT relying on it (i.e. python_embeded is present).
if [ -n "$PYEMBED" ]; then
    EXCL+=( --exclude='./venv' --exclude='./.venv' --exclude='./ComfyUI/venv' --exclude='./ComfyUI/.venv' )
fi
if [ "$INCLUDE_MODELS" != "1" ]; then
    EXCL+=( --exclude='./models' --exclude='./ComfyUI/models' )
fi

echo ">> archiving $COMFY_DIR (models included: $INCLUDE_MODELS) ..." >&2
cd "$COMFY_DIR"
# tarball -> stdout (the SSH channel -> the local file). Status above went to stderr.
# No -h: symlinks (e.g. a models -> /mnt link) are stored as links, not dereferenced.
tar czf - "${EXCL[@]}" .
'@

# ---------------------------------------------------------------------------
# build ssh arguments
# ---------------------------------------------------------------------------
$modelsFlag = if ($IncludeModels) { '1' } else { '0' }

# Embed the remote script as base64 in the command itself (no stdin piping). This keeps
# stdout a pure tar stream and sidesteps StreamWriter BOM/encoding issues on stdin.
$scriptLf  = $RemoteScript -replace "`r`n", "`n"
$scriptB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($scriptLf))
$remoteCmd = "printf %s $scriptB64 | base64 -d | bash -s -- $RemotePath $modelsFlag"

$sshArgs = New-Object System.Collections.Generic.List[string]
$sshArgs.Add('-o'); $sshArgs.Add('ConnectTimeout=30')
$sshArgs.Add('-o'); $sshArgs.Add('ServerAliveInterval=15')
if ($Port -ne 22)      { $sshArgs.Add('-p'); $sshArgs.Add("$Port") }
if ($IdentityFile)     { $sshArgs.Add('-i'); $sshArgs.Add($IdentityFile) }
$sshArgs.Add("$User@$RemoteHost")
$sshArgs.Add($remoteCmd)

# Manually quote into a single command line (Windows PowerShell 5.1 lacks ArgumentList).
function Quote-Arg($a) {
    if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
}
$argLine = ($sshArgs | ForEach-Object { Quote-Arg $_ }) -join ' '

# ---------------------------------------------------------------------------
# output paths
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$osTag   = if ($Windows) { 'windows-' } else { '' }
$tag     = $osTag + $(if ($IncludeModels) { 'with-models' } else { 'no-models' })
$OutFile = Join-Path $OutDir "flippix-comfyui-$tag-$stamp.tar.gz"

if ($Windows) {
    # =======================================================================
    # LOCAL Windows snapshot (no SSH) -> native Windows bundle
    # =======================================================================
    $LocalRoot = $LocalPath
    if (-not $LocalRoot) {
        foreach ($c in @(
            (Join-Path $env:USERPROFILE 'ComfyUI_FlipPix\ComfyUI_windows_portable'),
            (Join-Path $env:USERPROFILE 'ComfyUI_FlipPix'),
            'C:\ComfyUI_windows_portable',
            (Join-Path $env:USERPROFILE 'ComfyUI_windows_portable'))) {
            if (Test-Path $c) { $LocalRoot = $c; break }
        }
    }
    if (-not $LocalRoot -or -not (Test-Path $LocalRoot)) {
        Write-Err2 '-Windows: no local ComfyUI found. Pass -LocalPath <ComfyUI folder>.'
        exit 1
    }
    $LocalRoot = (Resolve-Path $LocalRoot).Path
    $looksOk = (Test-Path (Join-Path $LocalRoot 'run_nvidia_gpu.bat')) -or
               (Test-Path (Join-Path $LocalRoot 'python_embeded')) -or
               (Test-Path (Join-Path $LocalRoot 'ComfyUI\main.py')) -or
               (Test-Path (Join-Path $LocalRoot 'main.py'))
    if (-not $looksOk) { Write-Warn2 "$LocalRoot doesn't look like a ComfyUI install - archiving anyway." }

    $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $tarExe)) {
        Write-Err2 'Windows tar.exe not found (needs Windows 10 1803 or newer).'
        exit 1
    }

    Write-Host "FlipPix - local Windows ComfyUI backup" -ForegroundColor Magenta
    Write-Host "  source : $LocalRoot"
    Write-Host "  models : $(if ($IncludeModels) {'INCLUDED (large!)'} else {'excluded (default)'})"
    Write-Host "  output : $OutFile"
    Write-Step 'Archiving with tar.exe (this can take several minutes for a large install)'

    $parent = Split-Path $LocalRoot -Parent
    $leaf   = Split-Path $LocalRoot -Leaf
    # bsdtar: * spans '/', so */models excludes models at any depth. Keep python_embeded.
    $tarArgs = @('-C', $parent, '-czf', $OutFile,
        '--exclude=*/__pycache__', '--exclude=*.pyc',
        '--exclude=*/output', '--exclude=*/temp',
        '--exclude=*/.output_dir_config', '--exclude=*/.listen_address_config',
        '--exclude=*/.vram_config', '--exclude=*/.comfyui_flags_config')
    if (-not $IncludeModels) { $tarArgs += '--exclude=*/models' }
    $tarArgs += $leaf

    & $tarExe @tarArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Err2 "tar.exe failed (exit $LASTEXITCODE)."
        if ((Test-Path $OutFile) -and ((Get-Item $OutFile).Length -lt 1024)) { Remove-Item $OutFile -Force }
        exit 1
    }
}
else {
    # =======================================================================
    # REMOTE snapshot streamed over SSH
    # =======================================================================
    Write-Host "FlipPix - remote ComfyUI backup" -ForegroundColor Magenta
    Write-Host "  source : $User@${RemoteHost}:$RemotePath  (port $Port)"
    Write-Host "  models : $(if ($IncludeModels) {'INCLUDED (large!)'} else {'excluded (default)'})"
    Write-Host "  output : $OutFile"
    Write-Step 'Connecting + streaming snapshot (you may be prompted for the SSH password)'
    Write-Warn2 'Tip: set up an SSH key to avoid the password prompt (see RESTORE-README.md).'

    # run ssh with binary-safe stdout -> file (avoids PowerShell pipeline mangling)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $ssh.Source
    $psi.Arguments              = $argLine
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $false   # let remote status/progress show on the console

    $proc = [System.Diagnostics.Process]::Start($psi)

    # Stream raw bytes straight to the output file.
    $fs = [System.IO.File]::Create($OutFile)
    try {
        $proc.StandardOutput.BaseStream.CopyTo($fs)
    } finally {
        $fs.Close()
    }
    $proc.WaitForExit()

    if ($proc.ExitCode -ne 0) {
        Write-Err2 "ssh / remote backup failed (exit $($proc.ExitCode))."
        if (Test-Path $OutFile) {
            $len = (Get-Item $OutFile).Length
            if ($len -lt 1024) { Remove-Item $OutFile -Force }  # drop the empty stub
        }
        Write-Host ''
        Write-Host 'Common causes:' -ForegroundColor Yellow
        Write-Host '  - wrong path: pass -RemotePath (e.g. ~/jun1 or /home/x2/jun1)'
        Write-Host '  - auth: set up an SSH key, or check the username/host'
        Write-Host '  - host unreachable: ping 192.168.1.10 / confirm the box is on'
        exit $proc.ExitCode
    }
}

$sizeMB = [math]::Round((Get-Item $OutFile).Length / 1MB, 1)
Write-Ok "$(if ($Windows) {'snapshot created'} else {'snapshot downloaded'}): $sizeMB MB"

# ---------------------------------------------------------------------------
# checksum sidecar: <hash>  <filename>  (for upload + restore-time verification)
# ---------------------------------------------------------------------------
Write-Step 'Computing SHA-256 (for upload + restore verification)'
$sha     = (Get-FileHash -Algorithm SHA256 -Path $OutFile).Hash.ToLower()
$leaf    = Split-Path $OutFile -Leaf
$shaFile = "$OutFile.sha256"
# write a sha256sum-compatible line with a trailing LF, no BOM
[System.IO.File]::WriteAllText($shaFile, "$sha  $leaf`n", (New-Object System.Text.UTF8Encoding $false))
Write-Ok "sha256: $sha"

# ---------------------------------------------------------------------------
# drop the matching restore script + readme so the folder is self-contained
# ---------------------------------------------------------------------------
$restoreName = if ($Windows) { 'restore-comfyui-windows.ps1' } else { 'restore-comfyui.sh' }
$restoreSrc  = Join-Path $ScriptDir $restoreName
if (Test-Path $restoreSrc) {
    Copy-Item $restoreSrc (Join-Path $OutDir $restoreName) -Force
    Write-Ok "bundled $restoreName"
} else {
    Write-Warn2 "$restoreName not found next to this script ($restoreSrc) - copy it manually."
}

if ($Windows) {
    $readme = @"
FlipPix ComfyUI backup bundle (WINDOWS, native -- no WSL)
========================================================

A self-contained snapshot of a working WINDOWS ComfyUI -- the embedded Python
(python_embeded), all custom nodes, and the launchers -- made on $stamp from
$LocalRoot.

Restoring is "extract + run": no pip, no venv, no reinstalling custom nodes.

Contents
  $leaf   <- the snapshot (models $(if($IncludeModels){'INCLUDED'}else{'NOT included'}))
  restore-comfyui-windows.ps1   <- run this on Windows to restore

Restore on any Windows PC (needs an NVIDIA GPU + recent driver)
---------------------------------------------------------------
  powershell -ExecutionPolicy Bypass -File restore-comfyui-windows.ps1 -Archive "$leaf"
Then run run_nvidia_gpu.bat in the extracted folder and point FlipPix at 127.0.0.1:8188.

Models
------
$(if ($IncludeModels) {
"Model weights ARE bundled - nothing else to download."
} else {
"Model weights are NOT bundled. After restoring, add weights under the install's
models\ folder, or use extra_model_paths.yaml."
})

Publish to Hugging Face (users then restore with one command)
-------------------------------------------------------------
  hf upload <user>/flippix-comfyui "$leaf"         $HfRemoteName
  hf upload <user>/flippix-comfyui "$leaf.sha256"  $HfRemoteName.sha256
Then on Windows:  restore-comfyui-windows.ps1 -HfRepo <user>/flippix-comfyui
(Or just re-run this backup with  -HfUpload -HfRepo <user>/flippix-comfyui.)
"@
} else {
    $readme = @"
FlipPix ComfyUI backup bundle
=============================

This folder is a self-contained snapshot of a working ComfyUI install -- the bundled
Python environment (python_embeded), all custom nodes, and the launchers -- made on
$stamp from $User@${RemoteHost}:$RemotePath.

Because the Python environment is bundled, restoring is "extract + run": no pip, no
venv, no reinstalling the (many) custom nodes. It just works.

Contents
  $leaf   <- the snapshot (models $(if($IncludeModels){'INCLUDED'}else{'NOT included'}))
  restore-comfyui.sh             <- run this on Ubuntu / WSL to restore

Restore on any Ubuntu / WSL machine (needs an NVIDIA GPU + recent driver)
-------------------------------------------------------------------------
  1. Copy this whole folder to the target machine (or mount it).
  2. Restore (just unpacks + fixes up paths -- no build step for python_embeded):
         bash restore-comfyui.sh "$leaf"
     Optional target dir:
         bash restore-comfyui.sh "$leaf" ~/ComfyUI
  3. Launch ComfyUI (auto-detects VRAM; Enter through the prompts for defaults):
         cd <restored>/   &&   ./run_nvidia_gpu.sh
     (or the non-interactive shortcut written by restore:  ./run.sh)
     ComfyUI then serves on http://0.0.0.0:8188
  4. Point FlipPix at this ComfyUI (host = the machine's IP, port 8188).

Note: python_embeded carries CUDA-built PyTorch, so the target needs an NVIDIA GPU
with a compatible driver. For a non-GPU/venv install, restore falls back to building
a venv from requirements (use --cpu for CPU-only torch).

Models
------
$(if ($IncludeModels) {
"Model weights ARE bundled in the snapshot above - nothing else to download."
} else {
"Model weights are NOT bundled (they live on external storage on the source box, and
kept the download small). On the source machine 'ComfyUI/models' is a symlink to an
external drive; on restore that dangling link is replaced with an empty models/ folder.
After restoring, either:
  - point ComfyUI/models at your weights (symlink or extra_model_paths.yaml), or
  - download them with the FlipPix model manifest (scripts/flippix-models.txt)."
})

Publish to Hugging Face (so users can restore with one command)
---------------------------------------------------------------
Upload the snapshot + its .sha256 to a HF model repo, then anyone can fetch + verify
+ restore in a single step. One-time:
  pip install -U "huggingface_hub[cli]"
  hf auth login
  hf repo create flippix-comfyui --repo-type model        # once
Upload (repeat when you refresh the bundle):
  hf upload flippix-comfyui "$leaf" flippix-comfyui.tar.gz
  hf upload flippix-comfyui "$leaf.sha256" flippix-comfyui.tar.gz.sha256
(Uploading under the stable name flippix-comfyui.tar.gz lets users omit --hf-file.)

Then users restore with just:
  bash restore-comfyui.sh --hf <your-username>/flippix-comfyui
It downloads (resumable), verifies the sha256, and restores. For a gated/private repo
the user sets HF_TOKEN=hf_xxx first.

Avoid the SSH password prompt next time
----------------------------------------
  ssh-keygen -t ed25519
  type `$env:USERPROFILE\.ssh\id_ed25519.pub | ssh $User@$RemoteHost "cat >> ~/.ssh/authorized_keys"
"@
}
Set-Content -Path (Join-Path $OutDir 'RESTORE-README.md') -Value $readme -Encoding UTF8
Write-Ok 'wrote RESTORE-README.md'

# ---------------------------------------------------------------------------
# optional: publish the bundle to Hugging Face (-HfUpload)
# ---------------------------------------------------------------------------
$HfPublished = $false
if ($HfUpload) {
    Write-Step "Publishing to Hugging Face: $HfRepo"
    Write-Warn2 "Uploading $sizeMB MB - this can take a while (the CLI does resumable chunked uploads)."
    $hf = $HfCli.Source
    $shaRemote = "$HfRemoteName.sha256"
    # `hf upload <repo> <local> <path-in-repo>` auto-creates the repo under your account.
    & $hf upload $HfRepo $OutFile  $HfRemoteName --repo-type model
    $rc1 = $LASTEXITCODE
    if ($rc1 -eq 0) {
        & $hf upload $HfRepo $shaFile $shaRemote --repo-type model
        $rc1 = $LASTEXITCODE
    }
    if ($rc1 -eq 0) {
        $HfPublished = $true
        Write-Ok "published $HfRemoteName (+ .sha256) to $HfRepo"
    } else {
        Write-Err2 "Hugging Face upload failed (exit $rc1). The local bundle is fine; retry with:"
        Write-Host "    `"$hf`" upload $HfRepo `"$OutFile`" $HfRemoteName --repo-type model"
        Write-Host "    `"$hf`" upload $HfRepo `"$shaFile`" $shaRemote --repo-type model"
        Write-Host "  (Make sure you've run 'hf auth login' or set HF_TOKEN.)"
    }
}

Write-Host "`n==================================================" -ForegroundColor Magenta
Write-Host " Backup complete" -ForegroundColor Magenta
Write-Host "==================================================" -ForegroundColor Magenta
Write-Host ""
Write-Host "Bundle folder : $OutDir"
Write-Host "Snapshot      : $leaf ($sizeMB MB)"
Write-Host "Checksum      : $leaf.sha256"
Write-Host ""
if ($HfPublished) {
    Write-Host "Published to Hugging Face: $HfRepo  (as $HfRemoteName)" -ForegroundColor Green
    Write-Host "Users restore with ONE command:" -ForegroundColor Cyan
    if ($Windows) {
        Write-Host "    restore-comfyui-windows.ps1 -HfRepo $HfRepo"
    } else {
        Write-Host "    bash restore-comfyui.sh --hf $HfRepo"
    }
} else {
    if ($Windows) {
        Write-Host "To restore on Windows: copy the folder over, then run" -ForegroundColor Cyan
        Write-Host "    powershell -ExecutionPolicy Bypass -File restore-comfyui-windows.ps1 -Archive `"$leaf`""
        Write-Host ""
        Write-Host "To publish to Hugging Face (Windows users then restore with one command):" -ForegroundColor Cyan
        Write-Host "    re-run with  -Windows -HfUpload -HfRepo <user>/flippix-comfyui"
        Write-Host "    then users:  restore-comfyui-windows.ps1 -HfRepo <user>/flippix-comfyui"
    } else {
        Write-Host "To restore on Ubuntu/WSL: copy the folder over, then run" -ForegroundColor Cyan
        Write-Host "    bash restore-comfyui.sh `"$leaf`""
        Write-Host ""
        Write-Host "To publish to Hugging Face (users then restore with one command):" -ForegroundColor Cyan
        Write-Host "    re-run with  -HfUpload -HfRepo <user>/flippix-comfyui"
        Write-Host "    or manually: hf upload <user>/flippix-comfyui `"$leaf`" flippix-comfyui.tar.gz --repo-type model"
        Write-Host "    then users:  bash restore-comfyui.sh --hf <user>/flippix-comfyui"
    }
}
Write-Host "See RESTORE-README.md in the bundle folder for full details." -ForegroundColor DarkGray
Write-Host ""
