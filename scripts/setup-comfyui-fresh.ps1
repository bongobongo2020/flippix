<#
.SYNOPSIS
    FlipPix one-shot ComfyUI installer for Windows (inspired by ComfyUI-Easy-Install).

.DESCRIPTION
    Provisions a FRESH, self-contained ComfyUI install and adds every custom-node pack
    used by the FlipPix workflows, so the only thing left for the user to do afterwards
    is download model weights.

    Steps performed:
      1. Ensure git + a 7-Zip extractor are available (auto-downloads 7zr.exe; tries winget for git).
      2. Download the official ComfyUI Windows portable build (bundles python_embeded + torch/CUDA).
      3. Extract it into -InstallDir.
      4. Install ComfyUI-Manager + all curated custom-node packs (scripts/flippix-custom-nodes.txt)
         and their Python requirements into the embedded Python.
      5. Use ComfyUI-Manager's cm-cli to read the actual FlipPix workflow files and auto-install
         any custom nodes still missing (catches niche packs not in the curated list).
      6. Copy the FlipPix workflow library into the install for convenience.

    Models: step 7 asks for your current models folder. If it exists, ComfyUI is pointed at it
    (extra_model_paths.yaml) and nothing is downloaded; if it doesn't, you're offered the chance
    to create it and download the FlipPix models (manifest: scripts/flippix-models.txt).

.PARAMETER InstallDir
    Where to create the ComfyUI install. Default: %USERPROFILE%\ComfyUI_FlipPix

.PARAMETER ComfyUIArchiveUrl
    Override the ComfyUI portable .7z download URL. Default: latest GitHub release asset.

.PARAMETER ExistingComfyDir
    Skip the download/extract step and install nodes into an existing ComfyUI portable root
    (the folder that contains python_embeded and ComfyUI). Use this to add FlipPix nodes to
    a ComfyUI you already have.

.PARAMETER SkipMissingNodeScan
    Skip the cm-cli workflow scan (step 5). Faster, but may miss niche packs.

.PARAMETER ModelsDir
    Path to your CURRENT ComfyUI models folder (the one that already holds your weights).
    If it exists, the new install is pointed at it (via extra_model_paths.yaml) and NOTHING
    is downloaded. If it does NOT exist, you are offered the chance to create it and download
    the FlipPix models there. If omitted, you are prompted interactively.

.PARAMETER DownloadModels
    Auto-confirm creating + downloading models when the folder doesn't exist (for unattended
    runs). Without it, the script asks before downloading.

.PARAMETER SkipModels
    Don't touch models at all (no prompt, no download).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\setup-comfyui-fresh.ps1

.EXAMPLE
    .\setup-comfyui-fresh.ps1 -InstallDir D:\AI\ComfyUI

.EXAMPLE
    # reuse the weights from an existing ComfyUI, download nothing
    .\setup-comfyui-fresh.ps1 -ModelsDir "C:\ComfyUI_windows_portable\ComfyUI\models"
#>

[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:USERPROFILE 'ComfyUI_FlipPix'),
    [string]$ComfyUIArchiveUrl = '',
    [string]$ExistingComfyDir = '',
    [switch]$SkipMissingNodeScan,
    [string]$ModelsDir = '',
    [switch]$DownloadModels,
    [switch]$SkipModels
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # massively speeds up Invoke-WebRequest

# ---------------------------------------------------------------------------
# logging helpers
# ---------------------------------------------------------------------------
function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [ok] $m" -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "  [!] $m"  -ForegroundColor Yellow }
function Write-Err2($m) { Write-Host "  [x] $m"  -ForegroundColor Red }

# Run a native command quietly and return its exit code, WITHOUT letting harmless
# stderr output abort the script. Under $ErrorActionPreference = 'Stop', merging a
# native command's stderr (e.g. 'git clone ... 2>&1') makes Windows PowerShell 5.1
# wrap every progress line (like git's "Cloning into ...") into a *terminating*
# NativeCommandError. Relaxing the preference just around the call is the only
# reliable way to silence progress without killing the installer.
function Invoke-Quiet([scriptblock]$Command) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | Out-Null } finally { $ErrorActionPreference = $prev }
    return $LASTEXITCODE
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$NodeListFile = Join-Path $ScriptDir 'flippix-custom-nodes.txt'
$WorkflowSrc  = Join-Path $RepoRoot 'workflow'

# ---------------------------------------------------------------------------
# download helper: prefer curl.exe (fast, resumable) then fall back to BITS / IWR
# ---------------------------------------------------------------------------
function Get-File($Url, $OutFile) {
    if (Test-Path $OutFile) {
        $sizeMB = [math]::Round((Get-Item $OutFile).Length / 1MB, 1)
        Write-Ok "already downloaded ($sizeMB MB): $(Split-Path $OutFile -Leaf)"
        return
    }
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source -L --fail --retry 3 -C - -o $OutFile $Url
        if ($LASTEXITCODE -ne 0) { throw "curl failed downloading $Url" }
    } else {
        Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
    }
}

# ---------------------------------------------------------------------------
# 0. preflight: git + 7-Zip
# ---------------------------------------------------------------------------
function Install-MinGit {
    # Portable git: download the official MinGit (git-for-windows) zip and add it to the
    # session PATH. No admin, no winget - works on a clean Windows / Sandbox.
    $minDir = Join-Path $env:LOCALAPPDATA 'FlipPix\MinGit'
    $gitCmd = Join-Path $minDir 'cmd'
    $gitExe = Join-Path $gitCmd 'git.exe'
    if (Test-Path $gitExe) {
        $env:Path = "$gitCmd;$env:Path"
        if (Get-Command git -ErrorAction SilentlyContinue) { Write-Ok "using portable git at $minDir"; return $true }
    }
    Write-Warn2 'Downloading portable git (MinGit, ~45 MB) - no admin needed...'
    $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/git-for-windows/git/releases/latest' `
        -Headers @{ 'User-Agent' = 'flippix-setup' }
    $asset = $rel.assets |
        Where-Object { $_.name -match '^MinGit-.*-64-bit\.zip$' -and $_.name -notmatch 'busybox' } |
        Select-Object -First 1
    if (-not $asset) { throw 'could not find a MinGit 64-bit asset in the latest git-for-windows release' }
    $zip = Join-Path $env:TEMP $asset.name
    Get-File $asset.browser_download_url $zip
    New-Item -ItemType Directory -Force -Path $minDir | Out-Null
    Write-Host "  extracting MinGit -> $minDir"
    Expand-Archive -Path $zip -DestinationPath $minDir -Force
    if (-not (Test-Path $gitExe)) { throw "MinGit extracted but git.exe not found at $gitExe" }
    $env:Path = "$gitCmd;$env:Path"
    return [bool](Get-Command git -ErrorAction SilentlyContinue)
}

function Ensure-Git {
    if (Get-Command git -ErrorAction SilentlyContinue) { Write-Ok 'git found'; return }

    Write-Warn2 'git not found - attempting install via winget...'
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id Git.Git -e --source winget --accept-package-agreements --accept-source-agreements
        # winget does not refresh the current session PATH; add the default install location
        $gitCmd = Join-Path $env:ProgramFiles 'Git\cmd'
        if (Test-Path $gitCmd) { $env:Path = "$gitCmd;$env:Path" }
    } else {
        Write-Warn2 'winget not available (clean Windows / Sandbox) - will use portable git.'
    }
    if (Get-Command git -ErrorAction SilentlyContinue) { Write-Ok 'git installed'; return }

    # Portable fallback: download MinGit. Works on any clean Windows, no admin/winget.
    try {
        if (Install-MinGit) { Write-Ok 'git ready (portable MinGit)'; return }
    } catch {
        Write-Warn2 "portable git download failed: $($_.Exception.Message)"
    }

    throw "git is required and could not be auto-installed. Install it from https://git-scm.com/download/win, reopen the terminal, and re-run."
}

function Get-SevenZip {
    # Use an installed 7z if present, otherwise download the standalone 7zr.exe (handles .7z).
    foreach ($p in @(
        (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
        (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe'))) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    $sz = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($sz) { return $sz.Source }

    $zr = Join-Path $env:TEMP 'flippix_7zr.exe'
    Write-Warn2 '7-Zip not found - downloading standalone 7zr.exe...'
    Get-File 'https://www.7-zip.org/a/7zr.exe' $zr
    return $zr
}

function Ensure-VCRedist {
    # ComfyUI's bundled torch loads against the MS Visual C++ runtime (vcruntime140*.dll). A
    # clean Windows / Sandbox doesn't have it, so torch dies with
    # 'OSError: [WinError 126] ... c10.dll'. Detect via the runtime DLL and silently install.
    $sys = Join-Path $env:SystemRoot 'System32'
    if ((Test-Path (Join-Path $sys 'vcruntime140.dll')) -and (Test-Path (Join-Path $sys 'vcruntime140_1.dll'))) {
        Write-Ok 'Visual C++ runtime present'
        return
    }
    Write-Warn2 'Microsoft Visual C++ runtime not found - installing (torch needs it)...'
    $vc = Join-Path $env:TEMP 'vc_redist.x64.exe'
    try {
        Get-File 'https://aka.ms/vs/17/release/vc_redist.x64.exe' $vc
        $p = Start-Process -FilePath $vc -ArgumentList '/install','/quiet','/norestart' -Wait -PassThru
        if ($p.ExitCode -eq 0 -or $p.ExitCode -eq 3010) {
            Write-Ok 'Visual C++ runtime installed'
        } else {
            Write-Warn2 "vc_redist returned exit code $($p.ExitCode). If ComfyUI fails with a c10.dll error, install it manually: https://aka.ms/vc14/vc_redist.x64.exe"
        }
    } catch {
        Write-Warn2 "could not auto-install the VC++ runtime: $($_.Exception.Message). Install it manually: https://aka.ms/vc14/vc_redist.x64.exe"
    }
}

function Persist-GitForRuntime {
    # The installer adds (portable) git only to its OWN session PATH. ComfyUI-Manager runs
    # later in a fresh process (run_nvidia_gpu.bat) and uses GitPython, which then fails with
    # 'Bad git executable'. Persist git for future sessions: point GitPython straight at it
    # and add its folder to the user PATH.
    $g = Get-Command git -ErrorAction SilentlyContinue
    if (-not $g) { return }
    try {
        [Environment]::SetEnvironmentVariable('GIT_PYTHON_GIT_EXECUTABLE', $g.Source, 'User')
        $gitDir   = Split-Path $g.Source -Parent
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (($userPath -split ';') -notcontains $gitDir) {
            $newPath = (@($userPath, $gitDir) | Where-Object { $_ }) -join ';'
            [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        }
        Write-Ok "git persisted for ComfyUI runtime ($($g.Source))"
    } catch {
        Write-Warn2 "could not persist git for ComfyUI: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------
# 1. resolve ComfyUI portable download URL (latest release asset)
# ---------------------------------------------------------------------------
function Resolve-PortableUrl {
    if ($ComfyUIArchiveUrl) { return $ComfyUIArchiveUrl }
    Write-Step 'Resolving latest ComfyUI portable build from GitHub releases'
    $api = 'https://api.github.com/repos/comfyanonymous/ComfyUI/releases/latest'
    $rel = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'flippix-setup' }
    $asset = $rel.assets |
        Where-Object { $_.name -match 'windows_portable' -and $_.name -match 'nvidia' -and $_.name -match '\.7z$' } |
        Select-Object -First 1
    if (-not $asset) {
        $asset = $rel.assets | Where-Object { $_.name -match 'windows_portable.*\.7z$' } | Select-Object -First 1
    }
    if (-not $asset) { throw "Could not find a windows portable .7z asset in the latest release. Pass -ComfyUIArchiveUrl explicitly." }
    Write-Ok "found $($asset.name) ($([math]::Round($asset.size/1GB,2)) GB)"
    return $asset.browser_download_url
}

# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
Write-Host "FlipPix - ComfyUI fresh installer" -ForegroundColor Magenta
Write-Host "Repo: $RepoRoot"

Ensure-Git
Persist-GitForRuntime
Ensure-VCRedist
$SevenZip = Get-SevenZip
Write-Ok "7-Zip: $SevenZip"

# Locate / create the portable root (folder containing python_embeded + ComfyUI)
if ($ExistingComfyDir) {
    $PortableRoot = (Resolve-Path $ExistingComfyDir).Path
    Write-Step "Using existing ComfyUI at $PortableRoot"
} else {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $url = Resolve-PortableUrl
    $archive = Join-Path $InstallDir ([IO.Path]::GetFileName(($url -split '\?')[0]))

    Write-Step "Downloading ComfyUI portable -> $archive"
    Write-Warn2 'This is a large file (often 1.5-2.5 GB); please be patient.'
    Get-File $url $archive
    Write-Ok 'download complete'

    Write-Step "Extracting into $InstallDir"
    & $SevenZip x $archive "-o$InstallDir" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '7-Zip extraction failed.' }

    # The archive expands to a single top-level folder (e.g. ComfyUI_windows_portable)
    $PortableRoot = Get-ChildItem -Path $InstallDir -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'python_embeded') } |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $PortableRoot) { $PortableRoot = $InstallDir }
    Write-Ok "extracted to $PortableRoot"
}

$Py        = Join-Path $PortableRoot 'python_embeded\python.exe'
$ComfyDir  = Join-Path $PortableRoot 'ComfyUI'
$CustomDir = Join-Path $ComfyDir 'custom_nodes'

if (-not (Test-Path $Py))       { throw "python_embeded not found under $PortableRoot - is this a ComfyUI portable build?" }
if (-not (Test-Path $ComfyDir)) { throw "ComfyUI folder not found under $PortableRoot." }
New-Item -ItemType Directory -Force -Path $CustomDir | Out-Null
Write-Ok "embedded python: $Py"

# ---------------------------------------------------------------------------
# 2. clone custom-node packs + install their requirements
# ---------------------------------------------------------------------------
function Install-NodeRepo($Url) {
    $name = ($Url -split '/')[-1] -replace '\.git$', ''
    $dest = Join-Path $CustomDir $name
    # git writes normal progress to stderr; Invoke-Quiet relaxes ErrorActionPreference so that
    # progress does not abort the run under $ErrorActionPreference = 'Stop' (see helper above).
    if (Test-Path $dest) {
        Write-Ok "$name already present - pulling latest"
        Invoke-Quiet { git -C $dest pull --ff-only } | Out-Null
    } else {
        Write-Host "  cloning $name ..."
        Invoke-Quiet { git clone --depth 1 $Url $dest } | Out-Null
        if (-not (Test-Path $dest)) { Write-Err2 "failed to clone $name ($Url)"; return }
        Write-Ok "cloned $name"
    }
    $req = Join-Path $dest 'requirements.txt'
    if (Test-Path $req) {
        Write-Host "    installing requirements for $name ..."
        $rc = Invoke-Quiet { & $Py -s -m pip install -r $req --no-warn-script-location }
        if ($rc -ne 0) { Write-Warn2 "some requirements for $name failed (continuing)" }
    }
}

Write-Step 'Installing custom-node packs (curated list)'
if (-not (Test-Path $NodeListFile)) { throw "Missing node list: $NodeListFile" }
$repos = Get-Content $NodeListFile |
    ForEach-Object { ($_ -split '#')[0].Trim() } |
    Where-Object { $_ -ne '' }
foreach ($r in $repos) { Install-NodeRepo $r }

# Make sure ComfyUI-Manager's own deps are present (needed for cm-cli below)
$mgrReq = Join-Path $CustomDir 'ComfyUI-Manager\requirements.txt'
if (Test-Path $mgrReq) {
    Write-Host '  installing ComfyUI-Manager requirements ...'
    Invoke-Quiet { & $Py -s -m pip install -r $mgrReq --no-warn-script-location } | Out-Null
}

# ---------------------------------------------------------------------------
# 3. auto-install anything still missing, straight from the workflow files
# ---------------------------------------------------------------------------
if (-not $SkipMissingNodeScan) {
    $cmCli = Join-Path $CustomDir 'ComfyUI-Manager\cm-cli.py'
    if (Test-Path $cmCli) {
        Write-Step 'Scanning FlipPix workflows for any remaining missing nodes (cm-cli)'
        $tmpDeps = Join-Path $env:TEMP 'flippix_deps.json'
        $workflows = Get-ChildItem -Path $WorkflowSrc -Recurse -Filter *.json -ErrorAction SilentlyContinue
        $count = 0
        foreach ($wf in $workflows) {
            $count++
            try {
                Push-Location $ComfyDir
                Invoke-Quiet { & $Py -s $cmCli deps-in-workflow --workflow "$($wf.FullName)" --output "$tmpDeps" } | Out-Null
                if (Test-Path $tmpDeps) {
                    Invoke-Quiet { & $Py -s $cmCli install-deps --deps "$tmpDeps" } | Out-Null
                    Remove-Item $tmpDeps -ErrorAction SilentlyContinue
                }
            } catch {
                Write-Warn2 "cm-cli skipped $($wf.Name): $($_.Exception.Message)"
            } finally {
                Pop-Location
            }
        }
        Write-Ok "scanned $count workflow files"
    } else {
        Write-Warn2 'cm-cli not found - skipping auto missing-node scan. Use Manager > Install Missing Custom Nodes after first launch.'
    }
}

# ---------------------------------------------------------------------------
# 4. copy the FlipPix workflow library into the install
# ---------------------------------------------------------------------------
if (Test-Path $WorkflowSrc) {
    Write-Step 'Copying FlipPix workflows into ComfyUI (user/default/workflows/FlipPix)'
    $wfDest = Join-Path $ComfyDir 'user\default\workflows\FlipPix'
    New-Item -ItemType Directory -Force -Path $wfDest | Out-Null
    Copy-Item -Path (Join-Path $WorkflowSrc '*') -Destination $wfDest -Recurse -Force
    Write-Ok "workflows copied to $wfDest"
}

# ---------------------------------------------------------------------------
# 5. models: reuse an existing folder, or offer to create + download
# ---------------------------------------------------------------------------
$DefaultModels = Join-Path $ComfyDir 'models'

function Normalize-Path($p) { return ([IO.Path]::GetFullPath($p)).TrimEnd('\') }

# FlipPix model manifest, loaded from scripts/flippix-models.txt. Each entry:
# Path (relative to the models folder) | Size | Url.
# Get-File resumes/skips already-downloaded files.
$ModelListFile = Join-Path $ScriptDir 'flippix-models.txt'
$ModelManifest = @(
    if (Test-Path $ModelListFile) {
        Get-Content $ModelListFile | ForEach-Object {
            $line = $_.Trim()
            if ($line -eq '' -or $line.StartsWith('#')) { return }
            $parts = $line -split '\|', 3
            if ($parts.Count -ne 3) { return }
            @{ Path = $parts[0].Trim() -replace '/', '\'; Size = $parts[1].Trim(); Url = $parts[2].Trim() }
        }
    }
)

function Set-ModelPathLink($Dir) {
    # If the chosen folder isn't the install's own models dir, tell ComfyUI to look there too.
    if ((Normalize-Path $Dir) -ieq (Normalize-Path $DefaultModels)) {
        Write-Ok 'using the install''s own models folder (no extra_model_paths needed)'
        return
    }
    $base = (Normalize-Path $Dir) -replace '\\', '/'
    # A bare drive root (Normalize-Path turns 'Z:\' into 'Z:') must keep its slash: on Windows
    # 'Z:' means "current dir on Z", not the root, so without this ComfyUI builds 'Z:loras'
    # instead of 'Z:/loras'.
    if ($base -match '^[A-Za-z]:$') { $base += '/' }
    # Single-quote the path so ANY Windows path is a literal YAML scalar. Unquoted, a path
    # containing ': ' (e.g. a drive letter followed by a stray space) makes PyYAML read the
    # second colon as a mapping separator and ComfyUI dies with
    # "mapping values are not allowed here". Double any embedded single quotes for YAML.
    $baseYaml = "'" + ($base -replace "'", "''") + "'"
    $yaml = @"
flippix:
    base_path: $baseYaml
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
    $yamlPath = Join-Path $ComfyDir 'extra_model_paths.yaml'
    Set-Content -Path $yamlPath -Value $yaml -Encoding UTF8
    Write-Ok "linked ComfyUI to $base (extra_model_paths.yaml)"
}

function Download-Models($Dir) {
    if ($ModelManifest.Count -eq 0) {
        Write-Warn2 "model manifest is empty or missing ($ModelListFile) - skipping download"
        return
    }
    Write-Step "Downloading FlipPix models into $Dir (~45 GB total)"
    Write-Warn2 'Large download; interrupted files resume automatically on re-run.'
    $n = 0
    foreach ($m in $ModelManifest) {
        $n++
        $out = Join-Path $Dir $m.Path
        New-Item -ItemType Directory -Force -Path (Split-Path $out -Parent) | Out-Null
        Write-Host ("  [{0}/{1}] {2} ({3})" -f $n, $ModelManifest.Count, $m.Path, $m.Size)
        try { Get-File $m.Url $out } catch { Write-Warn2 "failed: $($m.Path) - $($_.Exception.Message)" }
    }
    Write-Ok 'model downloads finished (any failures are listed above; re-run to retry)'
}

function Format-ModelsPath([string]$raw) {
    # Normalize a user/param-supplied models path:
    #   - strip surrounding quotes / whitespace
    #   - drop a stray space after a drive colon ('Z: \x' / 'Z: ' -> 'Z:\x' / 'Z:')
    #   - turn a bare drive root ('Z:') into 'Z:\' so Test-Path / GetFullPath behave
    $p = $raw.Trim().Trim('"').Trim()
    $p = $p -replace '^([A-Za-z]):\s+', '$1:'
    if ($p -match '^[A-Za-z]:$') { $p += '\' }
    return $p
}

function Test-DriveAvailable([string]$path) {
    # False if the path names a drive letter that doesn't exist on this machine. Common in
    # Windows Sandbox, where host-mapped drives (e.g. Z:) are not present.
    if ($path -match '^([A-Za-z]):') {
        return [bool](Get-PSDrive -Name $matches[1] -ErrorAction SilentlyContinue)
    }
    return $true
}

if ($SkipModels) {
    Write-Step 'Models'
    Write-Ok 'skipping models (-SkipModels)'
} else {
    Write-Step 'Models'
    $target = $ModelsDir
    if ($target) {
        $target = Format-ModelsPath $target
        if (-not (Test-DriveAvailable $target)) {
            Write-Warn2 "drive for '$target' is not available on this machine - the path may not resolve."
        }
    } else {
        while ($true) {
            Write-Host "  Enter the path to your CURRENT ComfyUI models folder (where your weights"
            Write-Host "  already live) so this install can reuse them WITHOUT downloading."
            Write-Host "  Press Enter to use the new install's own folder:"
            Write-Host "    $DefaultModels" -ForegroundColor DarkGray
            $entered = Read-Host '  Models folder'
            if ([string]::IsNullOrWhiteSpace($entered)) { $target = $DefaultModels; break }
            $target = Format-ModelsPath $entered

            if ($target -match '^[A-Za-z]:\\?$') {
                Write-Warn2 "'$target' is a drive ROOT, not a models folder - this is usually a typo."
                if ((Read-Host '  Use the drive root anyway? [y/N]') -notmatch '^(y|yes)$') { continue }
            }
            if (-not (Test-DriveAvailable $target)) {
                Write-Warn2 "drive for '$target' is not available (unmapped / not present on this machine)."
                if ((Read-Host '  Use it anyway? [y/N]') -notmatch '^(y|yes)$') { continue }
            }
            break
        }
    }

    if (Test-Path $target) {
        Write-Ok "found existing models folder: $target"
        Set-ModelPathLink $target
        Write-Ok 'reusing existing models - nothing to download'
    } else {
        Write-Warn2 "models folder does not exist yet: $target"
        $doDownload = $DownloadModels
        if (-not $doDownload) {
            $ans = Read-Host "  Create it and download the FlipPix models there now (~45 GB)? [y/N]"
            $doDownload = ($ans -match '^(y|yes)$')
        }
        if ($doDownload) {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Set-ModelPathLink $target
            Download-Models $target
        } else {
            Write-Warn2 "skipping model download. Re-run later, or use ComfyUI-Manager's model manager."
        }
    }
}

# ---------------------------------------------------------------------------
# done
# ---------------------------------------------------------------------------
Write-Host "`n==================================================" -ForegroundColor Magenta
Write-Host " FlipPix ComfyUI setup complete" -ForegroundColor Magenta
Write-Host "==================================================" -ForegroundColor Magenta
Write-Host ""
Write-Host "ComfyUI install : $PortableRoot"
Write-Host "Custom nodes    : $CustomDir"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Models: if you skipped the download above, just re-run this installer"
Write-Host "     (it resumes/skips finished files), or use ComfyUI-Manager's model manager"
Write-Host "     / the links in COMFYUI_SETUP.md."
Write-Host "  2. Launch ComfyUI:  run_nvidia_gpu.bat   (in $PortableRoot)"
Write-Host "  3. First launch loads all custom nodes - watch the console for any that fail."
Write-Host "  4. If a workflow still shows red 'missing' nodes, open it and use"
Write-Host "     Manager > Install Missing Custom Nodes, then restart."
Write-Host "  5. Point FlipPix at this ComfyUI folder when prompted on startup."
Write-Host ""
