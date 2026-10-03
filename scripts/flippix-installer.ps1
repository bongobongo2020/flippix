<#
.SYNOPSIS
    FlipPix setup wizard with a deliberately retro Windows 98 look.

.DESCRIPTION
    One click-through installer that leaves this PC ready for the FlipPix iPad / phone app:
      * Options     - one install folder, start with Windows, shortcuts, and (desktop) how to get
                      ComfyUI: install it with every node and model (video optional), use the
                      ready-made FlipPix engine (packaging\comfyui-starter) and pick models in
                      FlipPix Models (flippix-models.ps1) afterwards, or use one already installed
      * System check - NVIDIA GPU with 12 GB+ VRAM, driver, free disk space, internet
      * Installing  - two classic segmented progress bars (overall + current item) while it:
            1. copies the FlipPix app
            2. installs ComfyUI + custom nodes + models (setup-comfyui-fresh.ps1 -Wizard)
            3. installs the writing assistant, llama-server + Qwen2.5-VL 7B (setup-llm.ps1 -Wizard)
            4. turns on the phone remote, adds a firewall rule and start-up shortcuts
            5. self-tests: the writing assistant answers, ComfyUI starts on the GPU
      * Finish      - results, and how to pair the iPad

    The child scripts run hidden; the wizard reads their '##FLIPPIX|...' markers (see
    setup-common.ps1) and watches each download's .part file grow to drive the bars. Everything is
    resumable: running Setup again skips finished work and resumes partial downloads.

    It is built with WinForms but intentionally does NOT enable visual styles, so Windows renders
    the old 3-D gray controls and the segmented progress bar - the Windows 98 aesthetic.

    FlipPix binaries are taken from the repo's publish\ folder if present; otherwise the wizard
    builds them with `dotnet publish` (requires the .NET SDK). The app is published
    self-contained, so the END USER needs no .NET runtime to run FlipPix.

.NOTES
    Launched by Install-FlipPix.bat in the repo root (double-click).
#>

[CmdletBinding()]
param(
    # Install the FlipPix iOS Companion (only what the iPad app needs) instead of the FlipPix desktop app.
    [switch]$Companion
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# NOTE: we deliberately do not call [Windows.Forms.Application]::EnableVisualStyles()
# so controls keep the classic Win9x 3-D look and the progress bar stays segmented.

# ---------------------------------------------------------------------------
# paths + state
# ---------------------------------------------------------------------------
$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot   = Split-Path -Parent $ScriptDir
$IconPath   = Join-Path $RepoRoot 'flippix.ico'
$PublishDir = Join-Path $RepoRoot 'publish'
$ComfyPs1   = Join-Path $ScriptDir 'setup-comfyui-fresh.ps1'
$LlmPs1     = Join-Path $ScriptDir 'setup-llm.ps1'

# What this run installs: the FlipPix desktop app, or (-Companion) the iOS Companion.
if ($Companion) {
    $Product    = 'FlipPix iOS Companion'
    $PublishDir = Join-Path $RepoRoot 'publish-companion'
    $AppExe     = 'FlipPix.IosCompanion.exe'
    $AppProject = 'FlipPix.IosCompanion\FlipPix.IosCompanion.csproj'
} else {
    $Product    = 'FlipPix'
    $AppExe     = 'FlipPix.UI.exe'
    $AppProject = 'FlipPix.UI\FlipPix.UI.csproj'
}
# The two graphs the iPad runs, relative to workflow\ (FlipPix.Remote embeds the same files). One
# comma-separated argument, because powershell -File can't pass an array.
$CompanionWorkflows = 'image\krea\krea2RealismV1_krea2RealismV1WF.json,video\h3-minimax\h3-minimax-i2v.json'

# The ready-made FlipPix engine and FlipPix Models (desktop only). starter.json sits beside this
# script in a release, and under packaging\ in the repo.
$ModelsPs1   = Join-Path $ScriptDir 'flippix-models.ps1'
$StarterJson = @((Join-Path $ScriptDir 'starter.json'), (Join-Path $RepoRoot 'packaging\comfyui-starter\starter.json')) |
    Where-Object { Test-Path $_ } | Select-Object -First 1
$EngineBytes = [long](2.45GB)   # the starter bundle; unpacks to ~6 GB

# Read-ModelManifest / ConvertTo-Bytes (the wizard sizes the model download from the manifests)
. (Join-Path $ScriptDir 'setup-common.ps1')

$MinVramMb         = 11000    # an RTX 4070 Ti reports ~12282 MB
$RecommendedDriver = 570
$LlmPort           = 8080
$LlmModelBytes     = [long](4683072032 + 853119712)   # Qwen2.5-VL Q4_K_M + mmproj (setup-llm.ps1)
$LlmServerBytes    = [long](263400928 + 391443627)    # llama.cpp CUDA build + runtime
# Everything that isn't a model weight: ComfyUI archive + extracted build + node/pip packages,
# the app, and llama-server unpacked. Generous on purpose; it only gates the disk check.
$BaseDiskBytes     = [long](22GB)
$BaseDownloadBytes = [long](4GB)                      # ComfyUI archive + pip wheels, roughly

$script:step       = 0
$script:Installed  = $false
$script:Busy       = $false
$script:Cancelled  = $false
$script:Child      = $null
$script:ChecksOk   = $false
$script:TestRows   = @()

$LogDir = Join-Path $env:LOCALAPPDATA 'FlipPix\setup-logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogFile = Join-Path $LogDir ('setup-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))

# ---------------------------------------------------------------------------
# machine facts used by several pages
# ---------------------------------------------------------------------------
function Get-GpuInfo {
    # nvidia-smi reports true total VRAM (WMI's AdapterRAM caps at 4 GB). Largest GPU wins.
    $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if (-not $smi) { return $null }
    try {
        $best = $null
        foreach ($line in (& $smi.Source --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits 2>$null)) {
            $f = $line -split ','
            if ($f.Count -lt 3) { continue }
            $mb = 0
            [void][int]::TryParse(($f[1] -replace '[^\d]', ''), [ref]$mb)
            if (-not $best -or $mb -gt $best.VramMb) {
                $best = @{ Name = $f[0].Trim(); VramMb = $mb; Driver = $f[2].Trim() }
            }
        }
        return $best
    } catch { return $null }
}
$Gpu = Get-GpuInfo
# Same rule as setup-comfyui-fresh.ps1 Resolve-VramTier: <= ~17 GB uses the 16gb workflow tier,
# and a full (video) install on that tier also pulls the low-VRAM LTX GGUF.
$Tier16 = ($Gpu -and $Gpu.VramMb -le 17408)

$LastRootFile = Join-Path $env:APPDATA 'FlipPix\setup-root.txt'

function Get-DefaultRoot {
    # An earlier install wins: the folder holding the most of this install's models, or the one
    # Setup last used. Only a first install picks the drive with the most free space. (Choosing
    # by free space alone moved the default to another drive once the first run's ~60 GB of models
    # had filled this one, and the next run downloaded them all again into an empty folder.)
    $fixed = @([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady })
    $candidates = @($fixed | ForEach-Object { Join-Path $_.Name 'FlipPix' })
    $last = $null
    try { if (Test-Path $LastRootFile) { $last = (Get-Content $LastRootFile -Raw).Trim() } } catch {}
    if ($last) { $candidates = @($last) + $candidates }
    $best = $null; $bestHave = [long]0
    foreach ($c in $candidates) {
        try {
            if (-not (Test-Path -LiteralPath $c)) { continue }
            $have = (Get-FilesProgress (Get-ModelFiles $c $true)).Have
            if ($have -gt $bestHave) { $best = $c; $bestHave = $have }
        } catch {}
    }
    if ($best) { return $best }
    if ($last -and (Test-Path -LiteralPath $last)) { return $last }

    $d = $fixed | Sort-Object AvailableFreeSpace -Descending | Select-Object -First 1
    if ($d) { return (Join-Path $d.Name 'FlipPix') }
    return (Join-Path $env:LOCALAPPDATA 'Programs\FlipPix')
}

function Get-ModelFiles([string]$Root, [bool]$Video, [string]$Mode = 'full') {
    # The model files this install should end up with: path, expected bytes. With the ready-made
    # engine or an existing ComfyUI, models are chosen afterwards in FlipPix Models.
    if (-not $Companion -and $Mode -ne 'full') { return @() }
    $list = if ($Companion) { 'flippix-models-ios.txt' } elseif ($Video) { 'flippix-models.txt' } else { 'flippix-models-min.txt' }
    $entries = @(Read-ModelManifest (Join-Path $ScriptDir $list))
    if ($Video -and $Tier16 -and -not $Companion) { $entries += @(Read-ModelManifest (Join-Path $ScriptDir 'flippix-models-16gb-video.txt')) }
    # IO.Path, not Join-Path: Join-Path throws for a drive letter this PC doesn't have, and this
    # runs on every keystroke in the folder box.
    $modelsDir = [IO.Path]::Combine($Root, 'models')
    @($entries | ForEach-Object { @{ Path = [IO.Path]::Combine($modelsDir, $_.Path); Bytes = $_.Bytes } })
}

function Get-FileSize([string]$Path) {
    # Size of a file, or -1 if it isn't there. Never throws: the child renames each .part the moment
    # its download finishes, so a Test-Path + Get-Item pair can lose the race ("Could not find item"),
    # and an exception here would abort the whole install.
    try {
        $fi = New-Object IO.FileInfo $Path
        if ($fi.Exists) { return [long]$fi.Length }
    } catch {}
    return [long]-1
}

function Get-FilesProgress($Files) {
    # Bytes already on disk for a file list: finished files count in full, partial .part files by size.
    $have = [long]0; $total = [long]0
    foreach ($f in $Files) {
        $total += $f.Bytes
        if ((Get-FileSize $f.Path) -ge 0) { $have += $f.Bytes; continue }
        $part = Get-FileSize "$($f.Path).part"
        if ($part -gt 0) { $have += [Math]::Min([long]$f.Bytes, $part) }
    }
    return @{ Have = $have; Total = $total }
}

function Format-Size([double]$bytes) {
    if ($bytes -ge 1GB) { return ('{0:N1} GB' -f ($bytes / 1GB)) }
    return ('{0:N0} MB' -f ($bytes / 1MB))
}

# ---------------------------------------------------------------------------
# Win98 palette + fonts
# ---------------------------------------------------------------------------
$clSilver  = [Drawing.Color]::FromArgb(192,192,192)   # classic ButtonFace
$clWhite   = [Drawing.Color]::White
$clNavy1   = [Drawing.Color]::FromArgb(0,0,128)        # banner gradient top
$clNavy2   = [Drawing.Color]::FromArgb(0,0,40)         # banner gradient bottom
$clOk      = [Drawing.Color]::FromArgb(0,128,0)
$clBad     = [Drawing.Color]::FromArgb(192,0,0)
$clWarn    = [Drawing.Color]::FromArgb(128,128,0)
$fnt       = New-Object Drawing.Font('MS Sans Serif', 8.25)
$fntBold   = New-Object Drawing.Font('MS Sans Serif', 8.25, [Drawing.FontStyle]::Bold)
$fntTitle  = New-Object Drawing.Font('MS Sans Serif', 14,   [Drawing.FontStyle]::Bold)
$fntMark   = New-Object Drawing.Font('Marlett', 11)       # 'a' = check mark, 'r' = cross

# ---------------------------------------------------------------------------
# form
# ---------------------------------------------------------------------------
$form = New-Object Windows.Forms.Form
$form.Text            = "$Product Setup"
$form.ClientSize      = New-Object Drawing.Size(497, 360)
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox     = $false
$form.MinimizeBox     = $true
$form.StartPosition   = 'CenterScreen'
$form.BackColor       = $clSilver
$form.Font            = $fnt
if (Test-Path $IconPath) { try { $form.Icon = New-Object Drawing.Icon($IconPath) } catch {} }

$bannerBmp = $null
if (Test-Path $IconPath) { try { $bannerBmp = (New-Object Drawing.Icon($IconPath, 48, 48)).ToBitmap() } catch {} }

# Build a navy gradient banner (left strip on welcome/finish pages).
function New-Banner {
    $b = New-Object Windows.Forms.Panel
    $b.Location = New-Object Drawing.Point(0,0)
    $b.Size     = New-Object Drawing.Size(164, 311)
    $b.Add_Paint({
        param($s,$e)
        $r = $s.ClientRectangle
        $g = New-Object Drawing.Drawing2D.LinearGradientBrush($r, $clNavy1, $clNavy2, 90)
        $e.Graphics.FillRectangle($g, $r)
        $g.Dispose()
        if ($bannerBmp) { $e.Graphics.DrawImage($bannerBmp, 22, 24, 48, 48) }
        $e.Graphics.DrawString('FlipPix', $fntTitle, [Drawing.Brushes]::White, 18, 82)
        $sub = New-Object Drawing.Font('MS Sans Serif', 8.25)
        $e.Graphics.DrawString("AI image & video`r`nstudio", $sub, [Drawing.Brushes]::Gainsboro, 20, 112)
        $e.Graphics.DrawString('Setup', $sub, [Drawing.Brushes]::Gainsboro, 20, 280)
    })
    return $b
}

# White header band used on the interior pages.
function New-Header($title, $desc) {
    $h = New-Object Windows.Forms.Panel
    $h.Location  = New-Object Drawing.Point(0,0)
    $h.Size      = New-Object Drawing.Size(497, 59)
    $h.BackColor = $clWhite
    $lblT = New-Object Windows.Forms.Label
    $lblT.Text = $title; $lblT.Font = $fntBold; $lblT.BackColor = $clWhite
    $lblT.Location = New-Object Drawing.Point(18, 10); $lblT.AutoSize = $true
    $lblD = New-Object Windows.Forms.Label
    $lblD.Text = $desc; $lblD.BackColor = $clWhite
    $lblD.Location = New-Object Drawing.Point(32, 30); $lblD.Size = New-Object Drawing.Size(400, 26)
    $h.Controls.AddRange(@($lblT, $lblD))
    if ($bannerBmp) {
        $pic = New-Object Windows.Forms.PictureBox
        $pic.Image = $bannerBmp; $pic.SizeMode = 'Zoom'
        $pic.Location = New-Object Drawing.Point(437, 6); $pic.Size = New-Object Drawing.Size(48,48)
        $pic.BackColor = $clWhite
        $h.Controls.Add($pic)
    }
    $h.Add_Paint({ param($s,$e)
        [Windows.Forms.ControlPaint]::DrawBorder3D($e.Graphics, 0, ($s.Height-2), $s.Width, 2, [Windows.Forms.Border3DStyle]::Etched) })
    return $h
}

function New-Label($text, $x, $y, $w, $h) {
    $l = New-Object Windows.Forms.Label
    $l.Text = $text; $l.Location = New-Object Drawing.Point($x,$y)
    $l.Size = New-Object Drawing.Size($w,$h)
    return $l
}

function New-Page {
    $p = New-Object Windows.Forms.Panel
    $p.Location = New-Object Drawing.Point(0,0)
    $p.Size     = New-Object Drawing.Size(497,311)
    return $p
}

# A status row: a Marlett check / cross (or '!') followed by text. Used by the system check and
# the self-test results.
function New-CheckRow($parent, $x, $y, $w) {
    $icon = New-Label '' $x $y 18 18
    $text = New-Label '' ($x + 22) ($y + 2) ($w - 22) 28
    $parent.Controls.AddRange(@($icon, $text))
    return @{ Icon = $icon; Text = $text }
}
function Set-CheckRow($row, [string]$state, [string]$text) {
    switch ($state) {
        'ok'   { $row.Icon.Font = $fntMark; $row.Icon.Text = 'a'; $row.Icon.ForeColor = $clOk }
        'fail' { $row.Icon.Font = $fntMark; $row.Icon.Text = 'r'; $row.Icon.ForeColor = $clBad }
        'warn' { $row.Icon.Font = $fntBold; $row.Icon.Text = ' !'; $row.Icon.ForeColor = $clWarn }
        default { $row.Icon.Font = $fntBold; $row.Icon.Text = ' ?'; $row.Icon.ForeColor = [Drawing.Color]::Gray }
    }
    $row.Text.Text = $text
}

# ===========================================================================
# Page 0 - Welcome
# ===========================================================================
$pgWelcome = New-Page
$pgWelcome.Controls.Add((New-Banner))
$wTitle = New-Label "Welcome to the $Product Setup Wizard" 180 24 300 40
$wTitle.Font = $fntBold
$wBody  = New-Label ("This sets up everything the FlipPix iPad app needs on this PC:`r`n`r`n" +
    $(if ($Companion) { "   - the FlipPix iOS Companion`r`n" } else { "   - the FlipPix desktop app`r`n" }) +
    $(if ($Companion) { "   - ComfyUI with Krea 2 (pictures) and MiniMax H3 (video)`r`n" } else { "   - ComfyUI, its custom nodes and models`r`n" }) +
    "   - the Qwen2.5-VL writing assistant`r`n`r`n" +
    "You need an NVIDIA graphics card with 12 GB of memory or more (RTX 4070 Ti or better). " +
    "Most of the time goes on downloads, so expect about an hour on a fast connection. " +
    "If anything stops it, run Setup again and it carries on where it left off.`r`n`r`n" +
    "Click Next to continue.") 180 70 300 230
$pgWelcome.Controls.AddRange(@($wTitle, $wBody))

# ===========================================================================
# Page 1 - License agreement
# ===========================================================================
$pgLicense = New-Page
$pgLicense.Controls.Add((New-Header 'License Agreement' 'Please read the following important information before continuing.'))
$txtLicense = New-Object Windows.Forms.TextBox
$txtLicense.Multiline = $true; $txtLicense.ReadOnly = $true; $txtLicense.ScrollBars = 'Vertical'
$txtLicense.BackColor = $clWhite
$txtLicense.Location = New-Object Drawing.Point(18,66); $txtLicense.Size = New-Object Drawing.Size(461,168)
$licenseCompanion = @"
FlipPix iOS Companion does not include any AI models. Setup downloads each one from its publisher onto this PC, and you may use it only under its own license.

KREA 2 (pictures): Krea 2 Community License Agreement
  - Commercial use only while your company's yearly revenue is under US`$1,000,000.
  - Content filters are required. The companion checks every picture and video, and every photo sent from the iPad.
  - https://krea.ai/krea-2-licensing

MINIMAX H3 (video): MiniMax H3 Community License Agreement
  - NOT licensed for use in the United States, the European Union, the United Kingdom or South Korea.
  - You must follow its Acceptable Use Policy: nothing illegal, harmful, deceptive or infringing.
  - https://huggingface.co/MiniMaxAI/MiniMax-H3/blob/main/LICENSE

Apache License 2.0: Qwen2.5-VL 7B (writing assistant), the Wan 2.1 VAE, the H3 turbo LoRA and latent upscaler, and the content filter (Falconsai nsfw_image_detection).

ComfyUI (GPL-3.0), llama.cpp (MIT) and the ComfyUI custom nodes are downloaded from their own projects under their own licenses.

By selecting "I accept the agreement" you agree to the license of every component above, including the Krea 2 and MiniMax H3 Acceptable Use Policies.
"@
$licenseDesktop = @"
FlipPix does not include any AI models. Setup downloads each one from its publisher onto this PC, and you may use it only under its own license.

Apache License 2.0: Qwen-Image, Qwen-Image-Edit 2509, Z-Image Turbo, Wan 2.1 / 2.2, their text encoders, VAEs and LoRAs, and Qwen2.5-VL 7B (writing assistant).

With video models on a GPU with 16 GB or less: the LTX-2.3 GGUF, under the LTX-2 Community License (companies with US`$10M or more yearly revenue need a paid license).

The ready-made FlipPix engine (a ComfyUI with only the node packs the phone uses) and the models FlipPix Models offers come from their publishers or the FlipPix Hugging Face repositories; each keeps its own license (Krea 2 and MiniMax H3 included, see the full list).

Not downloaded by Setup: the PixelDiT Gemma text encoder some image workflows ask for is for non-commercial use only (NVIDIA NSCLv1). FlipPix offers to fetch it the first time such a workflow needs it.

ComfyUI (GPL-3.0), llama.cpp (MIT) and the ComfyUI custom nodes are downloaded from their own projects under their own licenses.

By selecting "I accept the agreement" you agree to the license of every component above.
"@
$txtLicense.Text = $(if ($Companion) { $licenseCompanion } else { $licenseDesktop }).Replace("`r`n", "`n").Replace("`n", "`r`n")
$lnkLicenses = New-Object Windows.Forms.LinkLabel
$lnkLicenses.Text = 'Open the full list of licenses'
$lnkLicenses.Location = New-Object Drawing.Point(18,238); $lnkLicenses.AutoSize = $true
$lnkLicenses.Add_LinkClicked({
    $f = Join-Path $RepoRoot 'THIRD_PARTY_LICENSES.md'
    if (Test-Path $f) { try { Start-Process -FilePath $f } catch { Start-Process -FilePath 'notepad.exe' -ArgumentList "`"$f`"" } }
})
$rbAccept = New-Object Windows.Forms.RadioButton
$rbAccept.Text = 'I accept the agreement'
$rbAccept.Location = New-Object Drawing.Point(18,260); $rbAccept.Size = New-Object Drawing.Size(300,20)
$rbDecline = New-Object Windows.Forms.RadioButton
$rbDecline.Text = 'I do not accept the agreement'; $rbDecline.Checked = $true
$rbDecline.Location = New-Object Drawing.Point(18,282); $rbDecline.Size = New-Object Drawing.Size(300,20)
$rbAccept.Add_CheckedChanged({ if ($script:step -eq 1) { $btnNext.Enabled = $rbAccept.Checked } })
$pgLicense.Controls.AddRange(@($txtLicense, $lnkLicenses, $rbAccept, $rbDecline))

# ===========================================================================
# Page 2 - Options
# ===========================================================================
$pgOpts = New-Page
$pgOpts.Controls.Add((New-Header 'Choose options' 'Choose where FlipPix and its models go, and what to include.'))

$pgOpts.Controls.Add((New-Label 'Install everything to this folder:' 18 70 300 16))
$txtDir = New-Object Windows.Forms.TextBox
$txtDir.Location = New-Object Drawing.Point(18,88); $txtDir.Size = New-Object Drawing.Size(380,20)
$txtDir.Text = Get-DefaultRoot
$btnBrowse = New-Object Windows.Forms.Button
$btnBrowse.Text = 'Browse...'; $btnBrowse.Location = New-Object Drawing.Point(404,87)
$btnBrowse.Size = New-Object Drawing.Size(75,23)
$btnBrowse.Add_Click({
    $dlg = New-Object Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select a folder for FlipPix (the models need a lot of space)'
    if ($dlg.ShowDialog() -eq 'OK') { $txtDir.Text = (Join-Path $dlg.SelectedPath 'FlipPix') }
})

$grpParts = New-Object Windows.Forms.GroupBox
$grpParts.Location = New-Object Drawing.Point(18,114); $grpParts.Size = New-Object Drawing.Size(461,112)
$chkCore = New-Object Windows.Forms.CheckBox
$chkCore.Checked = $true; $chkCore.Enabled = $false
$chkCore.Location = New-Object Drawing.Point(12,20); $chkCore.Size = New-Object Drawing.Size(440,20)
$rbFull = New-Object Windows.Forms.RadioButton
$rbFull.Text = 'Install ComfyUI with every FlipPix node and model, ready to use'
$rbFull.Location = New-Object Drawing.Point(12,18); $rbFull.Size = New-Object Drawing.Size(440,20)
$chkVideo = New-Object Windows.Forms.CheckBox
$chkVideo.Location = New-Object Drawing.Point(30,38); $chkVideo.Size = New-Object Drawing.Size(422,20)
$rbStarter = New-Object Windows.Forms.RadioButton
$rbStarter.Text = 'Use the ready-made FlipPix engine (~2 GB), then choose models'
$rbStarter.Location = New-Object Drawing.Point(12,60); $rbStarter.Size = New-Object Drawing.Size(440,20)
$rbNone = New-Object Windows.Forms.RadioButton
$rbNone.Text = 'I already have ComfyUI'
$rbNone.Location = New-Object Drawing.Point(12,82); $rbNone.Size = New-Object Drawing.Size(440,20)
if ($Companion) {
    # The companion installs exactly what the iPad's two graphs need; there is nothing to choose.
    $grpParts.Text = 'Components'
    $chkCore.Text = 'FlipPix iOS Companion, ComfyUI with Krea 2 and MiniMax H3, writing assistant'
    $grpParts.Controls.Add($chkCore)
    $rbFull.Checked = $true
} else {
    $grpParts.Text = 'ComfyUI (the picture and video engine)'
    $grpParts.Controls.AddRange(@($rbFull, $chkVideo, $rbStarter, $rbNone))
    # Someone FlipPix already knows a ComfyUI for keeps it; everyone else gets the complete install.
    $hasComfy = $false
    try {
        $sf = Join-Path $env:APPDATA 'FlipPix\settings.json'
        if (Test-Path $sf) {
            $s0 = Get-Content $sf -Raw | ConvertFrom-Json
            $hasComfy = ($s0.ComfyUIFolderPath -and (Test-Path $s0.ComfyUIFolderPath)) -or
                        ($s0.BaseUrl -and $s0.BaseUrl -notmatch 'localhost|127\.0\.0\.1')
        }
    } catch {}
    if (-not $StarterJson) { $rbStarter.Enabled = $false }
    if ($hasComfy) { $rbNone.Checked = $true } else { $rbFull.Checked = $true }
}
$lblSize = New-Label '' 18 232 461 16

$chkStartup = New-Object Windows.Forms.CheckBox
$chkStartup.Text = "Start $Product when Windows starts, so the iPad can always connect"
$chkStartup.Checked = $true
$chkStartup.Location = New-Object Drawing.Point(18,252); $chkStartup.Size = New-Object Drawing.Size(460,20)
$chkDesktop = New-Object Windows.Forms.CheckBox
$chkDesktop.Text = 'Desktop shortcut'; $chkDesktop.Checked = $true
$chkDesktop.Location = New-Object Drawing.Point(18,274); $chkDesktop.Size = New-Object Drawing.Size(200,20)
$chkStart = New-Object Windows.Forms.CheckBox
$chkStart.Text = 'Start Menu shortcut'; $chkStart.Checked = $true
$chkStart.Location = New-Object Drawing.Point(230,274); $chkStart.Size = New-Object Drawing.Size(200,20)

$pgOpts.Controls.AddRange(@($txtDir, $btnBrowse, $grpParts, $lblSize, $chkStartup, $chkDesktop, $chkStart))

function Get-ComfyMode {
    if ($Companion -or $rbFull.Checked) { return 'full' }
    if ($rbStarter.Checked) { return 'starter' }
    return 'none'
}

function Get-Plan {
    # Sizes for the current choices; also used by the disk check.
    $root  = $txtDir.Text.Trim()
    $mode  = Get-ComfyMode
    $video = $chkVideo.Checked -and $mode -eq 'full'
    $models = Get-ModelFiles $root $video $mode
    $mp = Get-FilesProgress $models
    $llmHave = (Get-FilesProgress @(
        @{ Path = [IO.Path]::Combine($root, 'LLM\models\Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf'); Bytes = 4683072032 },
        @{ Path = [IO.Path]::Combine($root, 'LLM\models\mmproj-Qwen2.5-VL-7B-Instruct-Q8_0.gguf'); Bytes = 853119712 })).Have
    # What ComfyUI itself costs: built here (full), the ready-made engine, or nothing (already have it).
    $comfyDownload = switch ($mode) { 'full' { $BaseDownloadBytes } 'starter' { $EngineBytes } default { 0 } }
    $comfyDisk     = switch ($mode) { 'full' { $BaseDiskBytes } 'starter' { [long](10GB) } default { [long](2GB) } }
    $download = $comfyDownload + $LlmServerBytes + $LlmModelBytes + $mp.Total
    $stillNeed = $comfyDisk + ($LlmModelBytes - $llmHave) + ($mp.Total - $mp.Have)
    return @{ Root = $root; Mode = $mode; Video = $video; Models = $models; ModelBytes = $mp.Total
              Download = $download; DiskNeeded = [long]($stillNeed * 1.1) }
}

function Update-SizeLabel {
    $imageOnly = (Get-FilesProgress (Get-ModelFiles 'X:\' $false)).Total
    $withVideo = (Get-FilesProgress (Get-ModelFiles 'X:\' $true)).Total
    $chkVideo.Text = "Video models (adds about $(Format-Size ($withVideo - $imageOnly)) to the download)"
    # Runs on every keystroke in the folder box, so a blank or half-typed path must not throw.
    try {
        if ([string]::IsNullOrWhiteSpace($txtDir.Text)) { $lblSize.Text = ''; return }
        $p = Get-Plan
        $later = if ($p.Mode -ne 'full') { ' (plus the models you pick)' } else { '' }
        $lblSize.Text = "Download: about $(Format-Size $p.Download)$later.   Disk space needed: about $(Format-Size $p.DiskNeeded)."
    } catch { $lblSize.Text = '' }
}
$chkVideo.Add_CheckedChanged({ Update-SizeLabel })
foreach ($rb in $rbFull, $rbStarter, $rbNone) {
    $rb.Add_CheckedChanged({ $chkVideo.Enabled = $rbFull.Checked; Update-SizeLabel })
}
$txtDir.Add_TextChanged({ Update-SizeLabel })

# ===========================================================================
# Page 3 - System check
# ===========================================================================
$pgCheck = New-Page
$pgCheck.Controls.Add((New-Header 'System check' 'Setup checks that this PC can run FlipPix before downloading anything.'))
$rowWin   = New-CheckRow $pgCheck 24 76  450
$rowGpu   = New-CheckRow $pgCheck 24 106 450
$rowDrv   = New-CheckRow $pgCheck 24 136 450
$rowDisk  = New-CheckRow $pgCheck 24 166 450
$rowNet   = New-CheckRow $pgCheck 24 196 450
$lblCheck = New-Label '' 18 236 370 52
$btnRecheck = New-Object Windows.Forms.Button
$btnRecheck.Text = 'Check again'; $btnRecheck.Size = New-Object Drawing.Size(85,23)
$btnRecheck.Location = New-Object Drawing.Point(394,240)
$pgCheck.Controls.AddRange(@($lblCheck, $btnRecheck))

function Test-Internet {
    foreach ($u in 'https://huggingface.co', 'https://github.com') {
        try { Invoke-WebRequest -Uri $u -Method Head -UseBasicParsing -TimeoutSec 10 | Out-Null }
        catch { return $u }
    }
    return $null
}

function Invoke-SystemCheck {
    foreach ($r in $rowWin, $rowGpu, $rowDrv, $rowDisk, $rowNet) { Set-CheckRow $r 'pending' 'Checking...' }
    $lblCheck.Text = 'Checking...'
    $btnNext.Enabled = $false
    [Windows.Forms.Application]::DoEvents()
    $fail = $false

    if ([Environment]::Is64BitOperatingSystem) { Set-CheckRow $rowWin 'ok' 'Windows, 64-bit' }
    else { Set-CheckRow $rowWin 'fail' 'FlipPix needs 64-bit Windows 10 or 11.'; $fail = $true }

    $plan = Get-Plan
    $needsGpu = ($plan.Mode -eq 'full' -or $plan.Mode -eq 'starter')
    $script:Gpu = Get-GpuInfo
    if (-not $Gpu) {
        if ($needsGpu) {
            Set-CheckRow $rowGpu 'fail' "No NVIDIA graphics card found (or its driver isn't installed). ComfyUI needs an RTX 4070 Ti or better."
            Set-CheckRow $rowDrv 'fail' 'NVIDIA driver not found. Install it from nvidia.com/drivers, then click Check again.'
            $fail = $true
        } else {
            Set-CheckRow $rowGpu 'ok' 'No GPU needed (using existing ComfyUI)'
            Set-CheckRow $rowDrv 'ok' 'No GPU driver needed'
        }
    } else {
        $gb = '{0:N0} GB' -f ($Gpu.VramMb / 1024)
        if ($Gpu.VramMb -ge $MinVramMb) { Set-CheckRow $rowGpu 'ok' "$($Gpu.Name), $gb" }
        elseif ($needsGpu) { Set-CheckRow $rowGpu 'fail' "$($Gpu.Name) has $gb. ComfyUI needs 12 GB or more (RTX 4070 Ti or better)."; $fail = $true }
        else { Set-CheckRow $rowGpu 'ok' "$($Gpu.Name), $gb (not used - using existing ComfyUI)" }
        $major = 0; [void][int]::TryParse(($Gpu.Driver -split '\.')[0], [ref]$major)
        if ($major -ge $RecommendedDriver) { Set-CheckRow $rowDrv 'ok' "NVIDIA driver $($Gpu.Driver)" }
        else { Set-CheckRow $rowDrv 'warn' "NVIDIA driver $($Gpu.Driver) is old. Setup will continue, but updating from nvidia.com/drivers is recommended." }
    }
    $free = $null
    try {
        $full = [IO.Path]::GetFullPath($plan.Root)
        $free = ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($full))).AvailableFreeSpace
    } catch {}
    if ($null -eq $free) {
        Set-CheckRow $rowDisk 'fail' "Can't use '$($plan.Root)'. Click Back and choose another folder."; $fail = $true
    } elseif ($free -ge $plan.DiskNeeded) {
        Set-CheckRow $rowDisk 'ok' "$(Format-Size $free) free, about $(Format-Size $plan.DiskNeeded) needed"
    } else {
        Set-CheckRow $rowDisk 'fail' "Only $(Format-Size $free) free; about $(Format-Size $plan.DiskNeeded) is needed. Click Back and choose another drive."
        $fail = $true
    }

    $bad = Test-Internet
    if ($bad) { Set-CheckRow $rowNet 'fail' "Can't reach $bad. Check the internet connection."; $fail = $true }
    else { Set-CheckRow $rowNet 'ok' 'Internet connection works' }

    $script:ChecksOk = -not $fail
    if ($fail) { $lblCheck.Text = 'Fix the items marked with a cross, then click Check again.' }
    else { $lblCheck.Text = "Everything looks good. Click Install to start.`r`nYou can keep using your PC while Setup works." }
    $btnNext.Enabled = $script:ChecksOk
}
$btnRecheck.Add_Click({ Invoke-SystemCheck })

# ===========================================================================
# Page 4 - Installing (progress)
# ===========================================================================
$pgRun = New-Page
$pgRun.Controls.Add((New-Header 'Installing' 'Please wait while Setup downloads and installs FlipPix and everything it needs.'))
$lblStep = New-Label 'Preparing...' 18 68 461 16
$lblStep.Font = $fntBold
$pbAll = New-Object Windows.Forms.ProgressBar
$pbAll.Location = New-Object Drawing.Point(18,86); $pbAll.Size = New-Object Drawing.Size(461,20)
$pbAll.Minimum = 0; $pbAll.Maximum = 100
$lblItem = New-Label '' 18 114 461 16
$pbItem = New-Object Windows.Forms.ProgressBar
$pbItem.Location = New-Object Drawing.Point(18,132); $pbItem.Size = New-Object Drawing.Size(461,14)
$pbItem.Minimum = 0; $pbItem.Maximum = 100
$lstLog = New-Object Windows.Forms.ListBox
$lstLog.Location = New-Object Drawing.Point(18,156); $lstLog.Size = New-Object Drawing.Size(461,147)
$lstLog.BackColor = $clWhite
$lstLog.HorizontalScrollbar = $true
$pgRun.Controls.AddRange(@($lblStep, $pbAll, $lblItem, $pbItem, $lstLog))

# ===========================================================================
# Page 5 - Finish
# ===========================================================================
$pgDone = New-Page
$pgDone.Controls.Add((New-Banner))
$dTitle = New-Label $(if ($Companion) { 'Your PC is ready for the iPad' } else { 'FlipPix is ready' }) 180 20 300 24
$dTitle.Font = $fntBold
$rowTestLlm   = New-CheckRow $pgDone 180 50 300
$rowTestComfy = New-CheckRow $pgDone 180 80 300
$dIpad = New-Label '' 180 116 300 128
$chkLaunch = New-Object Windows.Forms.CheckBox
$chkLaunch.Text = "Start $Product now"; $chkLaunch.Checked = $true
$chkLaunch.Location = New-Object Drawing.Point(180,252); $chkLaunch.Size = New-Object Drawing.Size(300,20)
$dHint = New-Label 'Click Finish to exit Setup.' 180 282 300 20
# The ready-made engine and an existing ComfyUI hand over to FlipPix Models (it stays in the Start Menu).
$chkModels = New-Object Windows.Forms.CheckBox
$chkModels.Text = 'Choose models for your iPad now (FlipPix Models)'
$chkModels.Location = New-Object Drawing.Point(180,274); $chkModels.Size = New-Object Drawing.Size(300,20)
$chkModels.Visible = $false
$pgDone.Controls.AddRange(@($dTitle, $dIpad, $chkLaunch, $dHint, $chkModels))

$form.Controls.AddRange(@($pgWelcome, $pgLicense, $pgOpts, $pgCheck, $pgRun, $pgDone))

# ===========================================================================
# Button bar
# ===========================================================================
$bar = New-Object Windows.Forms.Panel
$bar.Location = New-Object Drawing.Point(0,311); $bar.Size = New-Object Drawing.Size(497,49)
$bar.Add_Paint({ param($s,$e)
    [Windows.Forms.ControlPaint]::DrawBorder3D($e.Graphics, 0, 0, $s.Width, 2, [Windows.Forms.Border3DStyle]::Etched) })
$btnBack = New-Object Windows.Forms.Button
$btnBack.Text = '< Back'; $btnBack.Size = New-Object Drawing.Size(75,23)
$btnBack.Location = New-Object Drawing.Point(252,13)
$btnNext = New-Object Windows.Forms.Button
$btnNext.Text = 'Next >'; $btnNext.Size = New-Object Drawing.Size(75,23)
$btnNext.Location = New-Object Drawing.Point(327,13)
$btnCancel = New-Object Windows.Forms.Button
$btnCancel.Text = 'Cancel'; $btnCancel.Size = New-Object Drawing.Size(75,23)
$btnCancel.Location = New-Object Drawing.Point(412,13)
$bar.Controls.AddRange(@($btnBack, $btnNext, $btnCancel))
$form.Controls.Add($bar)

# ---------------------------------------------------------------------------
# navigation
# ---------------------------------------------------------------------------
$pages = @($pgWelcome, $pgLicense, $pgOpts, $pgCheck, $pgRun, $pgDone)

function Show-Step($i) {
    $script:step = $i
    for ($k=0; $k -lt $pages.Count; $k++) { $pages[$k].Visible = ($k -eq $i) }
    switch ($i) {
        0 { $btnBack.Enabled=$false; $btnNext.Enabled=$true; $btnNext.Text='Next >'; $btnCancel.Visible=$true; $btnCancel.Enabled=$true }
        1 { $btnBack.Enabled=$true;  $btnNext.Enabled=$rbAccept.Checked; $btnNext.Text='Next >'; $btnCancel.Visible=$true; $btnCancel.Enabled=$true }
        2 { $btnBack.Enabled=$true;  $btnNext.Enabled=$true; $btnNext.Text='Next >'; $btnCancel.Visible=$true; $btnCancel.Enabled=$true; Update-SizeLabel }
        3 { $btnBack.Enabled=$true;  $btnNext.Text='Install'; $btnCancel.Visible=$true; $btnCancel.Enabled=$true; Invoke-SystemCheck }
        4 { $btnBack.Enabled=$false; $btnNext.Enabled=$false; $btnNext.Text='Next >'; $btnCancel.Enabled=$true }
        5 { $btnBack.Enabled=$false; $btnNext.Enabled=$true; $btnNext.Text='Finish'; $btnCancel.Visible=$false }
    }
}

function Write-Log($msg) {
    $line = "$msg"
    Add-Content -Path $LogFile -Value $line -ErrorAction SilentlyContinue
    [void]$lstLog.Items.Add($line)
    while ($lstLog.Items.Count -gt 500) { $lstLog.Items.RemoveAt(0) }
    $lstLog.TopIndex = $lstLog.Items.Count - 1
    [Windows.Forms.Application]::DoEvents()
}

# ---------------------------------------------------------------------------
# progress model
#
# The install is a fixed list of phases, each weighted by roughly how many bytes it moves (so the
# overall bar tracks time on a download-bound install instead of stalling at "step 7 of 13").
# Inside a phase the fraction comes from, in order of preference:
#   Files      - bytes already on disk for a known file list (models; survives resume)
#   download   - the current .part file vs. its expected size (from a child 'download' marker)
#   count      - item i of n (custom nodes, workflow scan)
#   Manual     - set directly by the wizard's own phases
#   Creep      - time-based, for steps with no measurable progress (extracting); stops at 95%
# ---------------------------------------------------------------------------
function New-Phase($key, $title, [double]$weight, $creep = 0) {
    @{ Key = $key; Title = $title; Weight = $weight; Creep = $creep; Files = $null }
}

function Initialize-Phases($plan) {
    $comfy = switch ($plan.Mode) {
        'full' { @(
            (New-Phase 'prereqs'        'Preparing (git, 7-Zip, Visual C++)'        0.3GB 60)
            (New-Phase 'comfy-download' 'Downloading ComfyUI'                       2GB)
            (New-Phase 'extract'        'Unpacking ComfyUI'                         1GB 120)
            (New-Phase 'nodes'          'Installing ComfyUI custom nodes'           3GB)
            (New-Phase 'scan'           'Checking workflows for missing nodes'      1GB)
            (New-Phase 'models'         'Downloading models'                        ([double]$plan.ModelBytes))
            (New-Phase 'link'           'Connecting FlipPix to ComfyUI'             0.05GB 5)) }
        'starter' { @(
            (New-Phase 'engine-download' 'Downloading the FlipPix engine'           ([double]$EngineBytes))
            (New-Phase 'engine-extract'  'Unpacking the FlipPix engine'             1GB 180)) }
        default { @() }
    }
    $script:Phases = @(
        (New-Phase 'app'            "Installing $Product"                       0.3GB)
        (New-Phase 'ffmpeg'         'Installing FFmpeg'                         0.15GB 30)) + $comfy + @(
        (New-Phase 'llm-server'     'Installing the writing assistant'          ([double]$LlmServerBytes))
        (New-Phase 'llm-model'      'Downloading Qwen2.5-VL 7B'                 ([double]$LlmModelBytes))
        (New-Phase 'llm-config'     'Setting up the writing assistant'          0.05GB 5)
        (New-Phase 'configure'      'Setting up iPad access and start-up'       0.1GB)
        (New-Phase 'selftest'       'Testing everything'                        1GB 300)
    )
    if ($plan.Mode -eq 'full') { ($script:Phases | Where-Object { $_.Key -eq 'models' }).Files = $plan.Models }
    $script:TotalWeight = ($script:Phases | ForEach-Object { $_.Weight } | Measure-Object -Sum).Sum
    $script:PhaseIdx = -1
}

function Enter-Phase($key) {
    $idx = -1
    for ($k = 0; $k -lt $script:Phases.Count; $k++) { if ($script:Phases[$k].Key -eq $key) { $idx = $k } }
    if ($idx -lt 0) { return }
    $script:PhaseIdx   = $idx
    $script:Phase      = $script:Phases[$idx]
    $script:PhaseStart = Get-Date
    $script:DoneWeight = 0.0
    for ($k = 0; $k -lt $idx; $k++) { $script:DoneWeight += $script:Phases[$k].Weight }
    $script:Dl = $null; $script:Count = $null; $script:Manual = $null; $script:PhaseBytes = [long]0
    $lblStep.Text = "Step $($idx + 1) of $($script:Phases.Count): $($script:Phase.Title)"
    $lblItem.Text = ''
    Write-Log "== $($script:Phase.Title)"
    Update-Progress
}

function Complete-Download {
    if ($script:Dl) { $script:PhaseBytes += $script:Dl.Bytes; $script:Dl = $null }
}

function Update-ProgressCore {
    $ph = $script:Phase
    if (-not $ph) { return }
    $frac = 0.0; $item = 0.0; $itemText = $lblItem.Text

    if ($script:Dl) {
        $d = $script:Dl
        $final = $d.Part -replace '\.part$', ''
        $size = Get-FileSize $d.Part
        if ($size -lt 0) { $size = $(if ((Get-FileSize $final) -ge 0) { $d.Bytes } else { 0 }) }
        $now = Get-Date
        $dt = ($now - $d.LastTime).TotalSeconds
        if ($dt -ge 1) {
            $rate = [Math]::Max(0.0, [double](($size - $d.LastSize) / $dt))
            $d.Speed = if ($d.Speed -gt 0) { 0.7 * $d.Speed + 0.3 * $rate } else { $rate }
            $d.LastSize = $size; $d.LastTime = $now
        }
        $itemText = "$($d.Label): $(Format-Size $size)"
        if ($d.Bytes -gt 0) { $item = $size / $d.Bytes; $itemText += " of $(Format-Size $d.Bytes)" }
        if ($d.Speed -gt 0 -and $d.Bytes -gt 0) {
            $itemText += ('  ({0:N1} MB/s' -f ($d.Speed / 1MB))
            $left = [Math]::Max(0.0, [double]($d.Bytes - $size)) / $d.Speed
            if ($left -ge 90) { $itemText += (', about {0:N0} min left)' -f ($left / 60)) } else { $itemText += ')' }
        }
        $frac = ($script:PhaseBytes + $size) / [Math]::Max([double]$ph.Weight, [double]($script:PhaseBytes + $d.Bytes))
    } elseif ($script:Count) {
        # Items with no measurable progress of their own: both bars fill as items finish.
        $frac = ($script:Count.I - 1) / [Math]::Max(1, $script:Count.N)
        $item = $frac
        $itemText = "$($script:Count.Label)  ($($script:Count.I) of $($script:Count.N))"
    } elseif ($null -ne $script:Manual) {
        $frac = $script:Manual; $item = $script:Manual
    } elseif ($ph.Creep -gt 0) {
        $frac = [Math]::Min(0.95, ((Get-Date) - $script:PhaseStart).TotalSeconds / $ph.Creep); $item = $frac
    }
    if ($ph.Files) {
        $fp = Get-FilesProgress $ph.Files
        if ($fp.Total -gt 0) { $frac = $fp.Have / $fp.Total }
    }

    $frac = [Math]::Max(0.0, [Math]::Min(1.0, $frac))
    $item = [Math]::Max(0.0, [Math]::Min(1.0, $item))
    $overall = ($script:DoneWeight + $frac * $ph.Weight) / [Math]::Max(1.0, $script:TotalWeight)
    $pbAll.Value  = [int]([Math]::Min(100, $overall * 100))
    $pbItem.Value = [int]($item * 100)
    $lblItem.Text = $itemText
}

function Update-Progress {
    # Only the bars: a problem drawing them must never stop the install (an exception here used to
    # abort Setup and kill the running download).
    try { Update-ProgressCore }
    catch {
        if (-not $script:ProgressError) {
            $script:ProgressError = $true
            Write-Log "Progress display: $($_.Exception.Message)"
        }
    }
}

function Set-Manual($frac, $text) {
    $script:Manual = $frac
    if ($text) { $lblItem.Text = $text }
    Update-Progress
    [Windows.Forms.Application]::DoEvents()
}

# ---------------------------------------------------------------------------
# child scripts
# ---------------------------------------------------------------------------
function ConvertTo-Arg([string]$s) {
    # Quote one argument for a Windows command line (CommandLineToArgvW rules).
    if ($s -notmatch '[\s"]' -and $s -ne '') { return $s }
    '"' + (($s -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

function Read-ChildLine([string]$line) {
    if ($line.StartsWith('##FLIPPIX|')) {
        $f = $line.Substring(10).Split('|')
        switch ($f[0]) {
            'phase' { Enter-Phase $f[1] }
            'count' {
                Complete-Download
                $script:Count = @{ I = [int]$f[1]; N = [int]$f[2]; Label = $f[3] }
            }
            'download' {
                Complete-Download
                $script:Dl = @{ Part = $f[1]; Bytes = [long]$f[2]; Label = $f[3]
                                LastSize = [long]0; LastTime = (Get-Date); Speed = 0.0 }
                $script:Dl.LastSize = [Math]::Max([long]0, (Get-FileSize $f[1]))
            }
        }
        return
    }
    $t = $line.Trim()
    if ($t) { Write-Log $t }
}

function Invoke-Child([string]$Script, [string[]]$Arguments, [string]$Name) {
    if ($script:Cancelled) { throw 'cancelled' }
    $log = Join-Path $LogDir ('{0}-{1:yyyyMMdd-HHmmss}.log' -f $Name, (Get-Date))
    # Children write UTF-8 under -Wizard (setup-common.ps1). A Decoder keeps a character that is
    # split across two reads intact.
    $decoder = (New-Object Text.UTF8Encoding $false).GetDecoder()
    $argLine = (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script) + $Arguments |
        ForEach-Object { ConvertTo-Arg $_ }) -join ' '
    $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $argLine -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $p.Handle   # caches the handle so ExitCode is readable after exit
    $script:Child = $p
    $pos = [long]0; $pending = ''
    while ($true) {
        $exited = $p.HasExited
        if (Test-Path -LiteralPath $log) {
            $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
            try {
                if ($fs.Length -gt $pos) {
                    $fs.Position = $pos
                    $buf = New-Object byte[] ([int]($fs.Length - $pos))
                    $read = $fs.Read($buf, 0, $buf.Length)
                    $pos += $read
                    $chars = New-Object char[] ($decoder.GetCharCount($buf, 0, $read))
                    [void]$decoder.GetChars($buf, 0, $read, $chars, 0)
                    $pending += (New-Object string (,$chars))
                }
            } finally { $fs.Dispose() }
            $lines = $pending -split "`r?`n"
            $pending = $lines[-1]
            if ($lines.Count -gt 1) { foreach ($l in $lines[0..($lines.Count - 2)]) { Read-ChildLine $l } }
        }
        if ($exited) { if ($pending) { Read-ChildLine $pending }; break }
        Update-Progress
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 120
    }
    $script:Child = $null
    Complete-Download
    if ($script:Cancelled) { throw 'cancelled' }
    if ($p.ExitCode -ne 0) {
        $err = (Get-Content "$log.err" -ErrorAction SilentlyContinue | Where-Object { $_.Trim() } | Select-Object -First 3) -join ' '
        if (-not $err) { $err = "exit code $($p.ExitCode)" }
        throw "$Name failed: $err"
    }
}

function Stop-Child {
    $p = $script:Child
    if ($p -and -not $p.HasExited) {
        # /T takes curl, git and python down with the PowerShell child.
        & taskkill.exe /T /F /PID $p.Id 2>$null | Out-Null
    }
}

# ---------------------------------------------------------------------------
# the wizard's own install steps
# ---------------------------------------------------------------------------
function Get-FlipPixSource {
    # Returns a folder that contains FlipPix.UI.exe, building it if necessary.
    if (Test-Path (Join-Path $PublishDir $AppExe)) {
        Write-Log "Using prebuilt binaries: $PublishDir"
        return $PublishDir
    }
    Write-Log 'No prebuilt binaries found - attempting to build from source...'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw "FlipPix is not built and the .NET SDK was not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download, then run Setup again (or run publish.bat first)."
    }
    $lblItem.Text = 'Building FlipPix from source (this can take a few minutes)...'
    Write-Log 'Running dotnet publish (self-contained, win-x64)...'
    $csproj = Join-Path $RepoRoot $AppProject
    $out = Join-Path $LogDir ('dotnet-publish-{0}.log' -f $PID)
    $publishArgs = @('publish', $csproj, '-c','Release','-r','win-x64','--self-contained','true',
              '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-o', $PublishDir)
    $p = Start-Process -FilePath $dotnet.Source -ArgumentList $publishArgs -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    while (-not $p.HasExited) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 200 }
    if ($p.ExitCode -ne 0) {
        $tail = (Get-Content "$out.err" -ErrorAction SilentlyContinue | Select-Object -Last 5) -join '; '
        throw "dotnet publish failed (exit $($p.ExitCode)). $tail"
    }
    Write-Log 'Build complete.'
    return $PublishDir
}

function New-Shortcut($lnkPath, $target, $workdir, $icon, $windowStyle = 1, $arguments = '') {
    $dir = Split-Path $lnkPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($lnkPath)
    $sc.TargetPath = $target
    $sc.WorkingDirectory = $workdir
    $sc.IconLocation = "$icon,0"
    $sc.WindowStyle = $windowStyle
    if ($arguments) { $sc.Arguments = $arguments }
    $sc.Description = 'FlipPix - AI image & video studio'
    $sc.Save()
}

function Install-App($appDir) {
    Enter-Phase 'app'
    $src = Get-FlipPixSource
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    Write-Log "App folder: $appDir"
    # Never copy debug symbols or the build machine's generated output.
    $files = @(Get-ChildItem -Path $src -Recurse -File | Where-Object {
        $_.Extension -ne '.pdb' -and $_.FullName.Substring($src.Length).TrimStart('\') -notlike 'output\*' })
    $total = [Math]::Max(1, $files.Count); $i = 0
    foreach ($f in $files) {
        $rel  = $f.FullName.Substring($src.Length).TrimStart('\')
        $dest = Join-Path $appDir $rel
        $dd   = Split-Path $dest -Parent
        if (-not (Test-Path $dd)) { New-Item -ItemType Directory -Force -Path $dd | Out-Null }
        Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
        $i++
        if (($i % 10) -eq 0 -or $i -eq $total) { Set-Manual ($i / $total) ("Copying files... ({0}/{1})" -f $i, $total) }
    }
    Write-Log "Copied $total files."

    $exe = Join-Path $appDir $AppExe
    if ($chkDesktop.Checked) {
        New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) "$Product.lnk") $exe $appDir $exe
        Write-Log 'Desktop shortcut created.'
    }
    if ($chkStart.Checked) {
        $sm = Join-Path ([Environment]::GetFolderPath('Programs')) "FlipPix\$Product.lnk"
        New-Shortcut $sm $exe $appDir $exe
        Write-Log 'Start Menu shortcut created.'
        $uninst = Join-Path $appDir 'Uninstall-FlipPix.exe'
        if (Test-Path $uninst) {
            $smU = Join-Path ([Environment]::GetFolderPath('Programs')) 'FlipPix\Uninstall FlipPix.lnk'
            New-Shortcut $smU $uninst $appDir $uninst
            Write-Log 'Uninstall shortcut created.'
        }
    }
}

function Install-FFmpeg($appDir) {
    Enter-Phase 'ffmpeg'

    $ffmpegDir = Join-Path $appDir 'ffmpeg\bin'
    $ffmpegExe = Join-Path $ffmpegDir 'ffmpeg.exe'

    # Skip if already installed
    if (Test-Path $ffmpegExe) {
        Write-Log "FFmpeg already installed at $ffmpegDir"
        Set-Manual 1.0 'FFmpeg ready'
        return
    }

    # Download
    $ffmpegUrl = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.7z'
    $ffmpegBytes = [long](34 * 1MB)
    $dlDir = Join-Path $env:TEMP 'FlipPix'
    New-Item -ItemType Directory -Force -Path $dlDir | Out-Null
    $archive = Join-Path $dlDir 'ffmpeg-release-essentials.7z'

    Write-Log "Downloading FFmpeg..."
    Get-File $ffmpegUrl $archive $ffmpegBytes 'FFmpeg'
    Write-Log 'FFmpeg download complete'

    # Extract
    Set-Manual 0.5 'Unpacking FFmpeg...'
    $seven = Get-7zr
    $tmpExtract = Join-Path $dlDir 'ffmpeg-extract'
    if (Test-Path $tmpExtract) { Remove-Item -Recurse -Force $tmpExtract }

    Write-Log "Extracting FFmpeg to $tmpExtract"
    & $seven x $archive "-o$tmpExtract" -y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg extraction failed (7zr exit $LASTEXITCODE)"
    }

    # Move binaries into place (archive extracts to ffmpeg-{version}-essentials_build/)
    $extracted = Get-ChildItem -Path $tmpExtract -Directory |
        Where-Object { $_.Name -match '^ffmpeg-.*-essentials' } |
        Select-Object -First 1

    if (-not $extracted) {
        throw "Could not find extracted FFmpeg folder in $tmpExtract"
    }

    $binSrc = Join-Path $extracted.FullName 'bin'
    if (-not (Test-Path $binSrc)) {
        throw "FFmpeg bin folder not found in extracted archive"
    }

    New-Item -ItemType Directory -Force -Path $ffmpegDir | Out-Null
    Copy-Item -Path "$binSrc\*" -Destination $ffmpegDir -Recurse -Force

    # Verify installation
    if (-not (Test-Path (Join-Path $ffmpegDir 'ffmpeg.exe'))) {
        throw "FFmpeg installation failed: ffmpeg.exe not found in $ffmpegDir"
    }
    if (-not (Test-Path (Join-Path $ffmpegDir 'ffprobe.exe'))) {
        Write-Log 'Warning: ffprobe.exe not found, but continuing'
    }

    # Cleanup
    Remove-Item -Recurse -Force $tmpExtract -ErrorAction SilentlyContinue
    Remove-Item $archive -ErrorAction SilentlyContinue

    Set-Manual 1.0 ''
    Write-Log "FFmpeg installed to $ffmpegDir"
}

# ---------------------------------------------------------------------------
# the ready-made FlipPix engine (packaging\comfyui-starter) and FlipPix Models
# ---------------------------------------------------------------------------

# Runs one external tool while the wizard keeps painting; $progress is polled every 250 ms. Cancel
# stops the tool too.
function Wait-Tool($proc, [scriptblock]$progress) {
    $null = $proc.Handle   # without this, ExitCode reads back empty once the process is gone
    $script:Child = $proc
    while (-not $proc.HasExited) {
        & $progress
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 250
    }
    $proc.WaitForExit()
    $script:Child = $null
    if ($script:Cancelled) { throw 'cancelled' }
    return $proc.ExitCode
}

function Get-7zr {
    foreach ($p in (Join-Path $ScriptDir '7zr.exe'), (Join-Path $RepoRoot '7zr.exe')) { if (Test-Path $p) { return $p } }
    $dst = Join-Path $env:TEMP 'FlipPix\7zr.exe'
    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    if (-not (Test-Path $dst)) {
        & curl.exe -L --fail --silent --show-error -o $dst 'https://www.7-zip.org/a/7zr.exe'
        if ($LASTEXITCODE -ne 0) { throw 'Could not download 7zr.exe (the extractor for the engine).' }
    }
    return $dst
}

# The bundle beside Setup when it was shipped that way (offline installs), else downloaded, resuming.
function Get-EngineBundle($starter) {
    Enter-Phase 'engine-download'
    $name = $starter.bundle.file
    foreach ($p in (Join-Path $RepoRoot $name), (Join-Path $ScriptDir $name), (Join-Path $RepoRoot "release\comfyui-starter\$name")) {
        if (Test-Path $p) { Write-Log "Using the engine next to Setup: $p"; return $p }
    }
    $dir = Join-Path $env:TEMP 'FlipPix'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $dst = Join-Path $dir $name
    $total = [int64]0
    try {
        $head = & curl.exe -sIL $starter.bundle.url
        $len = $head | Where-Object { $_ -match '^content-length:\s*(\d+)' } | Select-Object -Last 1
        if ($len -match '(\d+)') { $total = [int64]$Matches[1] }
    } catch {}
    if (-not ((Test-Path $dst) -and $total -gt 0 -and (Get-Item $dst).Length -eq $total)) {
        Write-Log "Downloading the engine: $($starter.bundle.url)"
        $part = "$dst.part"
        $script:Dl = @{ Part = $part; Bytes = $(if ($total -gt 0) { $total } else { $EngineBytes }); Label = 'FlipPix engine'
                        LastSize = [long]0; LastTime = (Get-Date); Speed = 0.0 }
        $cargs = @('-L', '--fail', '--silent', '--show-error', '--retry', '5', '--retry-delay', '5', '-C', '-', '-o', "`"$part`"", "`"$($starter.bundle.url)`"")
        $p = Start-Process -FilePath 'curl.exe' -ArgumentList $cargs -PassThru -WindowStyle Hidden
        $code = Wait-Tool $p { Update-Progress }
        $script:Dl = $null
        if ($code -ne 0) { throw "The engine download stopped (curl exit $code). Run Setup again to resume it." }
        Move-Item $part $dst -Force
    }

    Set-Manual 0.99 'Checking the download...'
    $expected = ((& curl.exe -sL --fail "$($starter.bundle.url).sha256") -join ' ') -split '\s+' | Select-Object -First 1
    if ($expected -match '^[0-9a-fA-F]{64}$') {
        $actual = (Get-FileHash $dst -Algorithm SHA256).Hash
        if ($actual -ne $expected.ToUpper()) {
            Remove-Item $dst -Force
            throw 'The engine download was damaged (checksum mismatch) and has been removed. Run Setup again.'
        }
        Write-Log 'Download verified.'
    } else {
        Write-Log 'No checksum published for the engine; skipped verification.'
    }
    return $dst
}

function Install-Engine($engineDir) {
    $starter = Get-Content $StarterJson -Raw | ConvertFrom-Json
    $bundle = Get-EngineBundle $starter
    $seven = Get-7zr

    # Extract beside the target, then move into place: the archive's folder is ComfyUI_FlipPix, and a
    # half-extracted engine must never look like an installed one.
    Enter-Phase 'engine-extract'
    $parent = Split-Path $engineDir -Parent
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $tmp = Join-Path $parent '.flippix-engine-extract'
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    $log = Join-Path $LogDir 'engine-extract.log'
    $p = Start-Process -FilePath $seven -ArgumentList @('x', "`"$bundle`"", "`"-o$tmp`"", '-y', '-bsp1') `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $log
    $code = Wait-Tool $p {
        $pct = 0
        try {
            $m = [regex]::Matches((Get-Content $log -Raw -ErrorAction SilentlyContinue), '(\d+)%')
            if ($m.Count) { $pct = [int]$m[$m.Count - 1].Groups[1].Value }
        } catch {}
        Set-Manual ($pct / 100) "Unpacking the FlipPix engine... $pct%"
    }
    if ($code -ne 0) { throw "Unpacking the engine failed (7zr exit $code)." }
    $new = Join-Path $tmp 'ComfyUI_FlipPix'

    # Reinstalling over an earlier engine keeps its models, outputs and model-folder registrations.
    if (Test-Path $engineDir) {
        $old = "$engineDir.old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
        Rename-Item $engineDir (Split-Path $old -Leaf)
        foreach ($keep in 'ComfyUI\models', 'ComfyUI\output', 'ComfyUI\extra_model_paths.yaml') {
            $from = Join-Path $old $keep
            if (Test-Path $from) {
                $to = Join-Path $new $keep
                if (Test-Path $to) { Remove-Item -Recurse -Force $to }
                Move-Item $from $to
            }
        }
        Write-Log "The previous engine was moved to $old (its models were kept). Delete it when you're happy."
    }
    Move-Item $new $engineDir
    Remove-Item -Recurse -Force $tmp
    Write-Log "Engine installed: $engineDir"

    Set-Manual 1.0 'Pointing FlipPix at the engine...'
    $dir = Join-Path $env:APPDATA 'FlipPix'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir 'settings.json'
    $settings = $null
    if (Test-Path $file) { try { $settings = Get-Content $file -Raw | ConvertFrom-Json } catch {} }
    if (-not $settings) { $settings = [PSCustomObject]@{} }
    $comfy = Join-Path $engineDir 'ComfyUI'
    # PascalCase, exactly as ComfyUISettings names them: System.Text.Json matches keys case-sensitively.
    $set = [ordered]@{
        ComfyUIFolderPath        = $comfy
        OutputFolderPath         = (Join-Path $comfy 'output')
        BaseUrl                  = 'http://127.0.0.1:8188'
        AutoRestartComfyUI       = $true
        ComfyUIRestartScriptPath = (Join-Path $engineDir 'run_flippix.bat')
    }
    foreach ($k in $set.Keys) { $settings | Add-Member -NotePropertyName $k -NotePropertyValue $set[$k] -Force }
    $settings | ConvertTo-Json -Depth 32 | Set-Content -Path $file -Encoding UTF8
    Write-Log 'FlipPix will start the engine itself.'
}

# FlipPix Models, copied into the install so its Start Menu entry outlives the Setup folder.
function Install-ModelsTool($appDir) {
    if (-not $StarterJson -or -not (Test-Path $ModelsPs1)) { return $null }
    $dst = Join-Path $appDir 'setup'
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item $ModelsPs1 (Join-Path $dst 'flippix-models.ps1') -Force
    Copy-Item $StarterJson (Join-Path $dst 'starter.json') -Force
    if (Test-Path $IconPath) { Copy-Item $IconPath (Join-Path $dst 'flippix.ico') -Force }
    $tool = Join-Path $dst 'flippix-models.ps1'
    if ($chkStart.Checked) {
        $exe = Join-Path $appDir $AppExe
        New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'FlipPix\FlipPix Models.lnk') 'powershell.exe' $dst $exe 1 `
            "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$tool`""
        Write-Log 'FlipPix Models shortcut created.'
    }
    return $tool
}

function Start-ModelsTool($ps1, $modelsDir) {
    $a = "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$ps1`""
    if ($modelsDir) { $a += " -ModelsDir `"$modelsDir`"" }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $a -WindowStyle Hidden
}

function Set-RemoteEnabled {
    # FlipPix.Remote's own settings file (RemoteConfig): turn the phone remote on, keep paired phones.
    $file = Join-Path $env:APPDATA 'FlipPix\remote.json'
    New-Item -ItemType Directory -Force -Path (Split-Path $file -Parent) | Out-Null
    $cfg = $null
    if (Test-Path $file) { try { $cfg = Get-Content $file -Raw | ConvertFrom-Json } catch { $cfg = $null } }
    if (-not $cfg) { $cfg = [PSCustomObject]@{ Port = 47800; Devices = @() } }
    $cfg | Add-Member -NotePropertyName 'Enabled' -NotePropertyValue $true -Force
    $cfg | ConvertTo-Json -Depth 8 | Set-Content -Path $file -Encoding UTF8
    Write-Log 'Phone remote turned on (FlipPix listens for the iPad when it starts).'
}

function Add-FirewallRule($exe) {
    # Lets the iPad reach FlipPix (TCP 47800 API, UDP 47801 discovery) on private networks. Needs
    # admin, so Windows shows one UAC prompt; declining is fine - Windows then asks the first time
    # FlipPix listens.
    $name = 'FlipPix phone remote'
    $cmd = "netsh advfirewall firewall delete rule name=`"$name`" >nul 2>&1 & " +
           "netsh advfirewall firewall add rule name=`"$name`" dir=in action=allow program=`"$exe`" enable=yes profile=private"
    try {
        $p = Start-Process -FilePath 'cmd.exe' -ArgumentList "/c $cmd" -Verb RunAs -WindowStyle Hidden -PassThru
        while (-not $p.HasExited) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 150 }
        Write-Log 'Firewall rule added so the iPad can connect.'
    } catch {
        Write-Log 'Firewall rule skipped. When Windows asks whether FlipPix may use the network, choose Allow on private networks.'
    }
}

function Set-CompanionConfig($root) {
    # Tells the companion where Setup put things (CompanionConfig in FlipPix.IosCompanion).
    $comfyDir = $null
    try { $comfyDir = (Get-Content (Join-Path $env:APPDATA 'FlipPix\settings.json') -Raw | ConvertFrom-Json).ComfyUIFolderPath } catch {}
    $cfg = [PSCustomObject]@{
        PortableRoot   = $(if ($comfyDir) { Split-Path $comfyDir -Parent } else { '' })
        LlmStartScript = (Join-Path $root 'LLM\start-llm.bat')
        FilterModel    = (Join-Path $root 'models\filter\nsfw_image_detection_uint8.onnx')
    }
    $file = Join-Path $env:APPDATA 'FlipPix\companion.json'
    New-Item -ItemType Directory -Force -Path (Split-Path $file -Parent) | Out-Null
    $cfg | ConvertTo-Json | Set-Content -Path $file -Encoding UTF8
    Write-Log "Companion settings written to $file"
}

function Set-Configuration($appDir, $root) {
    Enter-Phase 'configure'
    if ($Companion) {
        # The companion keeps its own pairing file and always listens; it only needs to know where things are.
        Set-Manual 0.1 'Recording where everything was installed...'
        Set-CompanionConfig $root
    } else {
        Set-Manual 0.1 'Turning on the phone remote...'
        Set-RemoteEnabled
    }
    Set-Manual 0.4 'Adding a firewall rule (Windows may ask for permission)...'
    $exe = Join-Path $appDir $AppExe
    Add-FirewallRule $exe
    Set-Manual 0.8 'Start-up shortcut...'
    $startupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) "$Product.lnk"
    if ($chkStartup.Checked) {
        New-Shortcut $startupLnk $exe $appDir $exe 7 $(if ($Companion) { '--tray' } else { '' })
        Write-Log "$Product starts (minimized) when you sign in to Windows."
    } else {
        Remove-Item -LiteralPath $startupLnk -ErrorAction SilentlyContinue
    }
    Set-Manual 1.0 ''
}

function Wait-Url($url, [int]$timeoutSec, $label) {
    # Polls until the URL answers 2xx; keeps the UI alive. Returns the parsed body or $null.
    $start = Get-Date
    while (((Get-Date) - $start).TotalSeconds -lt $timeoutSec) {
        if ($script:Cancelled) { throw 'cancelled' }
        try { return (Invoke-RestMethod -Uri $url -TimeoutSec 3) } catch {}
        $lblItem.Text = "$label ({0:N0}s)" -f ((Get-Date) - $start).TotalSeconds
        Update-Progress
        for ($k = 0; $k -lt 10; $k++) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 200 }
    }
    return $null
}

function Invoke-SelfTest($root, [bool]$StartComfy = $true) {
    Enter-Phase 'selftest'
    $script:TestRows = @()

    # Writing assistant: start it if needed, wait for the model to load, ask it something.
    $llmUrl = "http://127.0.0.1:$LlmPort"
    $startBat = Join-Path $root 'LLM\start-llm.bat'
    try { Invoke-RestMethod -Uri "$llmUrl/health" -TimeoutSec 3 | Out-Null } catch {
        Write-Log 'Starting the writing assistant...'
        Start-Process -FilePath $startBat -WorkingDirectory (Split-Path $startBat) -WindowStyle Minimized
    }
    $llmOk = $false
    if (Wait-Url "$llmUrl/health" 240 'Loading Qwen2.5-VL onto the GPU') {
        try {
            $body = @{ model = 'qwen2.5-vl-7b-instruct-q4_k_m'; max_tokens = 8
                       messages = @(@{ role = 'user'; content = 'Reply with the single word OK.' }) } | ConvertTo-Json -Depth 5
            $r = Invoke-RestMethod -Uri "$llmUrl/v1/chat/completions" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 120
            $llmOk = [bool]$r.choices[0].message.content
        } catch { Write-Log "Writing assistant test: $($_.Exception.Message)" }
    }
    $script:TestRows += ,@($(if ($llmOk) { 'ok' } else { 'fail' }),
        $(if ($llmOk) { 'The writing assistant (Qwen2.5-VL) answered.' } else { "The writing assistant didn't answer. Run start-llm.bat in the LLM folder to see why." }))
    Write-Log $script:TestRows[-1][1]

    # ComfyUI: start it the way FlipPix does (without opening a browser) and wait for the GPU.
    $comfyOk = $false; $gpuName = ''
    $settingsFile = Join-Path $env:APPDATA 'FlipPix\settings.json'
    $comfyDir = $null
    try { $comfyDir = (Get-Content $settingsFile -Raw | ConvertFrom-Json).ComfyUIFolderPath } catch {}
    try { Invoke-RestMethod -Uri 'http://127.0.0.1:8188/system_stats' -TimeoutSec 3 | Out-Null } catch {
        if ($StartComfy -and $comfyDir -and (Test-Path $comfyDir)) {
            $portable = Split-Path $comfyDir -Parent
            $py = Join-Path $portable 'python_embeded\python.exe'
            Write-Log 'Starting ComfyUI (the first start takes a few minutes)...'
            Start-Process -FilePath $py -ArgumentList '-s ComfyUI\main.py --windows-standalone-build --disable-auto-launch' `
                -WorkingDirectory $portable -WindowStyle Minimized
        }
    }
    $stats = Wait-Url 'http://127.0.0.1:8188/system_stats' $(if ($StartComfy) { 900 } else { 20 }) 'Starting ComfyUI and loading custom nodes'
    if ($stats) {
        $comfyOk = $true
        try { $gpuName = ($stats.devices[0].name -replace '^cuda:\d+\s*', '' -replace '\s*:\s*cudaMallocAsync$', '') } catch {}
    }
    $script:TestRows += ,@($(if ($comfyOk) { 'ok' } else { 'fail' }),
        $(if ($comfyOk) { "ComfyUI is running$(if ($gpuName) { " on $gpuName" })." } else { "ComfyUI didn't start. Open FlipPix; it will try again and show any error." }))
    Write-Log $script:TestRows[-1][1]
    Set-Manual 1.0 ''
}

function Stop-InstallProcesses($root) {
    # A ComfyUI left running from this folder keeps the node packs it loaded at its start: packages
    # installed under it now only arrive at the next start, and the companion leaves a ComfyUI that
    # already answers alone, so it never got one (RTXVideoSuperResolution stayed "missing" after a
    # re-run). Stop the companion and the servers running from this folder; the self-test below
    # starts ComfyUI again and the companion is relaunched from the finish page. The desktop app is
    # left alone (it may hold unsaved work).
    $names = @('FlipPix.IosCompanion.exe', 'python.exe', 'pythonw.exe', 'llama-server.exe')
    $prefix = $root.TrimEnd('\') + '\'
    $procs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $names -contains $_.Name -and $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    # The companion first: it stops what it started.
    foreach ($p in @($procs | Sort-Object { $_.Name -ne 'FlipPix.IosCompanion.exe' })) {
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            Write-Log "Stopped $($p.Name) (pid $($p.ProcessId)) so the update takes effect."
        } catch { <# already gone #> }
    }
    if ($procs.Count -gt 0) { Start-Sleep -Milliseconds 1500 }   # let the ports and file locks go
}

function Start-Install {
    $script:Busy = $true
    $script:Cancelled = $false
    $lstLog.Items.Clear()
    try {
        $plan = Get-Plan
        $root = [IO.Path]::GetFullPath($plan.Root)
        $appDir = Join-Path $root 'App'
        # Remembered so the next run offers this folder again (Get-DefaultRoot).
        try {
            New-Item -ItemType Directory -Force -Path (Split-Path $LastRootFile -Parent) | Out-Null
            Set-Content -Path $LastRootFile -Value $root -Encoding UTF8
        } catch {}
        Initialize-Phases $plan
        Write-Log "FlipPix Setup - $(Get-Date)"
        Write-Log "Folder: $root   Video models: $($plan.Video)   GPU: $(if ($Gpu) { "$($Gpu.Name) $($Gpu.VramMb) MB" } else { 'none' })"
        Write-Log "Log: $LogFile"

        Stop-InstallProcesses $root
        Install-App $appDir
        Install-FFmpeg $appDir

        $script:ComfyMode = $plan.Mode
        $script:EngineDir = Join-Path $root 'ComfyUI_FlipPix'
        switch ($plan.Mode) {
            'full' {
                $comfyArgs = @('-InstallDir', (Join-Path $root 'ComfyUI'), '-ModelsDir', (Join-Path $root 'models'),
                               '-DownloadModels', '-EnsureModels', '-Wizard')
                if ($Companion) {
                    $comfyArgs += @('-NodeListFile', 'flippix-custom-nodes-ios.txt', '-ModelListFile', 'flippix-models-ios.txt',
                                    '-ScanWorkflow', $CompanionWorkflows)
                } elseif (-not $plan.Video) { $comfyArgs += '-Minimal' }
                Invoke-Child $ComfyPs1 $comfyArgs 'comfyui'
            }
            'starter' { Install-Engine $script:EngineDir }
            default   { Write-Log 'Using the ComfyUI FlipPix already knows about.' }
        }
        if (-not $Companion) { $script:ModelsTool = Install-ModelsTool $appDir }

        $llmArgs = @('-InstallDir', (Join-Path $root 'LLM'), '-Port', "$LlmPort", '-Wizard')
        # The companion starts and watches the writing assistant itself.
        if ($Companion -or -not $chkStartup.Checked) { $llmArgs += '-NoStartup' }
        Invoke-Child $LlmPs1 $llmArgs 'writing-assistant'

        Set-Configuration $appDir $root
        Invoke-SelfTest $root ($plan.Mode -ne 'none')
        # The servers the test started belong to nobody: the companion would find them running and
        # leave them alone, so quitting it wouldn't stop them. It starts (and owns) its own.
        if ($Companion) { Stop-InstallProcesses $root }

        $pbAll.Value = 100
        Write-Log 'Setup complete.'
        $script:InstallDir = $appDir
        $script:Installed = $true
        $script:Busy = $false
        Show-Finish $root
        Show-Step 5
    } catch {
        $script:Busy = $false
        Stop-Child
        $msg = $_.Exception.Message
        Write-Log "ERROR: $msg"
        if ($script:Cancelled) { $form.Close(); return }
        [Windows.Forms.MessageBox]::Show(
            "$msg`r`n`r`nClick Install to try again - finished downloads are kept.`r`nThe full log is in $LogDir",
            "$Product Setup - Error", [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        Show-Step 3
    }
}

function Show-Finish($root) {
    if ($script:TestRows.Count -ge 1) { Set-CheckRow $rowTestLlm   $script:TestRows[0][0] $script:TestRows[0][1] }
    if ($script:TestRows.Count -ge 2) { Set-CheckRow $rowTestComfy $script:TestRows[1][0] $script:TestRows[1][1] }
    $dIpad.Text = "Connect your iPad:`r`n" +
        "  1. Put the iPad on the same Wi-Fi as this PC.`r`n" +
        "  2. Open FlipPix on the iPad and tap $($env:COMPUTERNAME).`r`n" +
        $(if ($Companion) {
            "  3. Type the 6-digit code shown in the FlipPix iOS Companion window on this PC."
        } else {
            "  3. On this PC, open FlipPix and click the phone button at the top of the Image Generator. " +
            "Type the 6-digit code it shows into the iPad."
        })
    Write-Log "Installed in $root"
    $offer = [bool]$script:ModelsTool -and $script:ComfyMode -ne 'full'
    $chkModels.Visible = $offer; $chkModels.Checked = $offer; $dHint.Visible = -not $offer
}

# ---------------------------------------------------------------------------
# button handlers
# ---------------------------------------------------------------------------
function Confirm-StopInstall {
    $ans = [Windows.Forms.MessageBox]::Show(
        "Stop Setup now?`r`n`r`nNothing is lost: run Setup again later and it carries on where it left off.",
        "$Product Setup", [Windows.Forms.MessageBoxButtons]::YesNo, [Windows.Forms.MessageBoxIcon]::Question)
    if ($ans -eq 'Yes') { $script:Cancelled = $true; Stop-Child; return $true }
    return $false
}

$btnNext.Add_Click({
    switch ($script:step) {
        0 { Show-Step 1 }
        1 { if ($rbAccept.Checked) { Show-Step 2 } }
        2 {
            if ([string]::IsNullOrWhiteSpace($txtDir.Text)) {
                [Windows.Forms.MessageBox]::Show('Please choose an install folder.', "$Product Setup",
                    [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
                return
            }
            Show-Step 3
        }
        3 {
            if (-not $script:ChecksOk) { return }
            Show-Step 4
            Start-Install
        }
        5 {
            if ($chkLaunch.Checked -and $script:Installed) {
                $exe = Join-Path $script:InstallDir $AppExe
                if (Test-Path $exe) { Start-Process -FilePath $exe -WorkingDirectory $script:InstallDir }
            }
            if ($chkModels.Visible -and $chkModels.Checked -and $script:ModelsTool) {
                $md = if ($script:ComfyMode -eq 'starter') { Join-Path $script:EngineDir 'ComfyUI\models' } else { '' }
                Start-ModelsTool $script:ModelsTool $md
            }
            $form.Close()
        }
    }
})
$btnBack.Add_Click({
    switch ($script:step) {
        1 { Show-Step 0 }
        2 { Show-Step 1 }
        3 { Show-Step 2 }
    }
})
$btnCancel.Add_Click({
    if ($script:Busy) { [void](Confirm-StopInstall); return }
    if ([Windows.Forms.MessageBox]::Show('Cancel FlipPix Setup?', "$Product Setup",
        [Windows.Forms.MessageBoxButtons]::YesNo, [Windows.Forms.MessageBoxIcon]::Question) -eq 'Yes') {
        $form.Close()
    }
})
$form.Add_FormClosing({
    param($s, $e)
    # The title-bar X during an install: same question as Cancel. The form stays open either way;
    # on Yes the install loop unwinds (its child is killed) and closes the form itself.
    if ($script:Busy) {
        $e.Cancel = $true
        if (-not $script:Cancelled) { [void](Confirm-StopInstall) }
    }
})

Show-Step 0
[void]$form.ShowDialog()
Stop-Child
$form.Dispose()
