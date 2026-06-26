<#
.SYNOPSIS
    FlipPix setup wizard with a deliberately retro Windows 98 look.

.DESCRIPTION
    A self-contained, click-through installer for brand-new users:
      * Welcome / info pages
      * Choose install folder, desktop / Start Menu shortcuts
      * Optional: also install ComfyUI (chains to setup-comfyui-fresh.ps1)
      * Classic segmented progress bar while it deploys FlipPix
      * Finish page that can launch FlipPix

    It is built with WinForms but intentionally does NOT enable visual styles, so
    Windows renders the old 3-D gray controls and the segmented progress bar -
    i.e. the Windows 98 aesthetic - on any modern Windows.

    FlipPix binaries are taken from the repo's publish\ folder if present; otherwise
    the wizard builds them with `dotnet publish` (requires the .NET SDK). The app is
    published self-contained, so the END USER needs no .NET runtime to run FlipPix.

.NOTES
    Launched by Install-FlipPix.bat in the repo root (double-click).
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# NOTE: we deliberately do not call [Windows.Forms.Application]::EnableVisualStyles()
# so controls keep the classic Win9x 3-D look and the progress bar stays segmented.

# ---------------------------------------------------------------------------
# paths + state
# ---------------------------------------------------------------------------
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$IconPath  = Join-Path $RepoRoot 'flippix.ico'
$PublishDir = Join-Path $RepoRoot 'publish'
$ComfyBat  = Join-Path $RepoRoot 'Install-ComfyUI.bat'

$script:step = 0
$script:InstallDir   = Join-Path $env:LOCALAPPDATA 'Programs\FlipPix'
$script:DesktopSC    = $true
$script:StartMenuSC  = $true
$script:InstallComfy = $false
$script:LaunchOnExit = $true
$script:Installed    = $false

# ---------------------------------------------------------------------------
# Win98 palette + fonts
# ---------------------------------------------------------------------------
$clSilver  = [Drawing.Color]::FromArgb(192,192,192)   # classic ButtonFace
$clWhite   = [Drawing.Color]::White
$clNavy1   = [Drawing.Color]::FromArgb(0,0,128)        # banner gradient top
$clNavy2   = [Drawing.Color]::FromArgb(0,0,40)         # banner gradient bottom
$fnt       = New-Object Drawing.Font('MS Sans Serif', 8.25)
$fntBold   = New-Object Drawing.Font('MS Sans Serif', 8.25, [Drawing.FontStyle]::Bold)
$fntTitle  = New-Object Drawing.Font('MS Sans Serif', 14,   [Drawing.FontStyle]::Bold)

# ---------------------------------------------------------------------------
# form
# ---------------------------------------------------------------------------
$form = New-Object Windows.Forms.Form
$form.Text            = 'FlipPix Setup'
$form.ClientSize      = New-Object Drawing.Size(497, 360)
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox     = $false
$form.MinimizeBox     = $false
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
    $lblD.Location = New-Object Drawing.Point(32, 30); $lblD.Size = New-Object Drawing.Size(440, 26)
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

# ===========================================================================
# Page 0 - Welcome
# ===========================================================================
$pgWelcome = New-Object Windows.Forms.Panel
$pgWelcome.Location = New-Object Drawing.Point(0,0)
$pgWelcome.Size     = New-Object Drawing.Size(497,311)
$pgWelcome.Controls.Add((New-Banner))
$wTitle = New-Label 'Welcome to the FlipPix Setup Wizard' 180 24 300 40
$wTitle.Font = $fntBold
$wBody  = New-Label ("This will install FlipPix - an AI image & video studio that drives ComfyUI - on your computer.`r`n`r`nFlipPix is published self-contained, so you do not need to install the .NET runtime separately.`r`n`r`nIt is recommended that you close all other applications before continuing.`r`n`r`nClick Next to continue, or Cancel to exit Setup.") 180 70 300 230
$pgWelcome.Controls.AddRange(@($wTitle, $wBody))

# ===========================================================================
# Page 1 - Information / license
# ===========================================================================
$pgInfo = New-Object Windows.Forms.Panel
$pgInfo.Location = New-Object Drawing.Point(0,0)
$pgInfo.Size     = New-Object Drawing.Size(497,311)
$pgInfo.Controls.Add((New-Header 'Information' 'Please read the following before continuing.'))
$txtInfo = New-Object Windows.Forms.TextBox
$txtInfo.Multiline = $true; $txtInfo.ReadOnly = $true; $txtInfo.ScrollBars = 'Vertical'
$txtInfo.BackColor = $clWhite
$txtInfo.Location = New-Object Drawing.Point(18,72); $txtInfo.Size = New-Object Drawing.Size(461,195)
$txtInfo.Text = @"
FlipPix - AI image & video studio
=================================

What gets installed:
  * The FlipPix desktop application (self-contained, no .NET runtime required).
  * Bundled prompt templates and ComfyUI workflow files.
  * Optional desktop and Start Menu shortcuts.

What FlipPix needs to actually generate:
  * A running ComfyUI server (default http://127.0.0.1:8188) with the FlipPix
    custom nodes and model weights. If you don't have ComfyUI yet, tick the
    "Also install ComfyUI" box on the next page and Setup will launch the
    one-click ComfyUI installer for you.

Requirements:
  * Windows 10 or 11 (64-bit).
  * For image/video generation: an NVIDIA GPU (12 GB+ VRAM recommended) on the
    machine running ComfyUI.

This installer copies files into your user profile and does not require admin
rights. No files outside the chosen folder and the shortcut locations are
modified.

Please respect the licenses of ComfyUI, the custom nodes, and any models you
download.
"@.Replace("`n", "`r`n")
$pgInfo.Controls.Add($txtInfo)

# ===========================================================================
# Page 2 - Options (folder + shortcuts + ComfyUI)
# ===========================================================================
$pgOpts = New-Object Windows.Forms.Panel
$pgOpts.Location = New-Object Drawing.Point(0,0)
$pgOpts.Size     = New-Object Drawing.Size(497,311)
$pgOpts.Controls.Add((New-Header 'Choose options' 'Select where to install FlipPix and what extras to set up.'))

$pgOpts.Controls.Add((New-Label 'Install FlipPix to this folder:' 18 76 300 16))
$txtDir = New-Object Windows.Forms.TextBox
$txtDir.Location = New-Object Drawing.Point(18,94); $txtDir.Size = New-Object Drawing.Size(380,20)
$txtDir.Text = $script:InstallDir
$btnBrowse = New-Object Windows.Forms.Button
$btnBrowse.Text = 'Browse...'; $btnBrowse.Location = New-Object Drawing.Point(404,93)
$btnBrowse.Size = New-Object Drawing.Size(75,23)
$btnBrowse.Add_Click({
    $dlg = New-Object Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select the FlipPix install folder'
    $dlg.SelectedPath = $txtDir.Text
    if ($dlg.ShowDialog() -eq 'OK') { $txtDir.Text = (Join-Path $dlg.SelectedPath 'FlipPix') }
})

$chkDesktop = New-Object Windows.Forms.CheckBox
$chkDesktop.Text = 'Create a desktop shortcut'; $chkDesktop.Checked = $true
$chkDesktop.Location = New-Object Drawing.Point(18,132); $chkDesktop.Size = New-Object Drawing.Size(440,20)
$chkStart = New-Object Windows.Forms.CheckBox
$chkStart.Text = 'Create a Start Menu shortcut'; $chkStart.Checked = $true
$chkStart.Location = New-Object Drawing.Point(18,156); $chkStart.Size = New-Object Drawing.Size(440,20)

$grpComfy = New-Object Windows.Forms.GroupBox
$grpComfy.Text = 'ComfyUI (image/video engine)'
$grpComfy.Location = New-Object Drawing.Point(18,188); $grpComfy.Size = New-Object Drawing.Size(461,100)
$chkComfy = New-Object Windows.Forms.CheckBox
$chkComfy.Text = 'Also install ComfyUI (launches the one-click ComfyUI installer)'
$chkComfy.Location = New-Object Drawing.Point(12,22); $chkComfy.Size = New-Object Drawing.Size(440,20)
$lblComfy = New-Label "Provisions a fresh, self-contained ComfyUI and all FlipPix custom nodes.`r`nLarge download (~2 GB + optional models). Runs in its own window after FlipPix`r`nis installed. Leave unticked if you already have ComfyUI set up." 30 44 425 48
$grpComfy.Controls.AddRange(@($chkComfy, $lblComfy))

$pgOpts.Controls.AddRange(@($txtDir, $btnBrowse, $chkDesktop, $chkStart, $grpComfy))

# ===========================================================================
# Page 3 - Installing (progress)
# ===========================================================================
$pgRun = New-Object Windows.Forms.Panel
$pgRun.Location = New-Object Drawing.Point(0,0)
$pgRun.Size     = New-Object Drawing.Size(497,311)
$pgRun.Controls.Add((New-Header 'Installing' 'Please wait while FlipPix is installed on your computer.'))
$lblStatus = New-Label 'Preparing...' 18 80 461 16
$pb = New-Object Windows.Forms.ProgressBar
$pb.Location = New-Object Drawing.Point(18,100); $pb.Size = New-Object Drawing.Size(461,22)
$pb.Minimum = 0; $pb.Maximum = 100
$lstLog = New-Object Windows.Forms.ListBox
$lstLog.Location = New-Object Drawing.Point(18,134); $lstLog.Size = New-Object Drawing.Size(461,160)
$lstLog.BackColor = $clWhite
$pgRun.Controls.AddRange(@($lblStatus, $pb, $lstLog))

# ===========================================================================
# Page 4 - Finish
# ===========================================================================
$pgDone = New-Object Windows.Forms.Panel
$pgDone.Location = New-Object Drawing.Point(0,0)
$pgDone.Size     = New-Object Drawing.Size(497,311)
$pgDone.Controls.Add((New-Banner))
$dTitle = New-Label 'Completing the FlipPix Setup Wizard' 180 24 300 40
$dTitle.Font = $fntBold
$dBody  = New-Label 'Setup has finished installing FlipPix on your computer.' 180 74 300 40
$chkLaunch = New-Object Windows.Forms.CheckBox
$chkLaunch.Text = 'Launch FlipPix now'; $chkLaunch.Checked = $true
$chkLaunch.Location = New-Object Drawing.Point(180,120); $chkLaunch.Size = New-Object Drawing.Size(300,20)
$dHint = New-Label 'Click Finish to exit Setup.' 180 270 300 30
$pgDone.Controls.AddRange(@($dTitle, $dBody, $chkLaunch, $dHint))

$form.Controls.AddRange(@($pgWelcome, $pgInfo, $pgOpts, $pgRun, $pgDone))

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
$pages = @($pgWelcome, $pgInfo, $pgOpts, $pgRun, $pgDone)

function Show-Step($i) {
    $script:step = $i
    for ($k=0; $k -lt $pages.Count; $k++) { $pages[$k].Visible = ($k -eq $i) }
    switch ($i) {
        0 { $btnBack.Enabled=$false; $btnNext.Enabled=$true; $btnNext.Text='Next >'; $btnCancel.Visible=$true; $btnCancel.Enabled=$true }
        1 { $btnBack.Enabled=$true;  $btnNext.Enabled=$true; $btnNext.Text='Next >'; $btnCancel.Visible=$true; $btnCancel.Enabled=$true }
        2 { $btnBack.Enabled=$true;  $btnNext.Enabled=$true; $btnNext.Text='Install'; $btnCancel.Visible=$true; $btnCancel.Enabled=$true }
        3 { $btnBack.Enabled=$false; $btnNext.Enabled=$false; $btnNext.Text='Next >'; $btnCancel.Enabled=$false }
        4 { $btnBack.Enabled=$false; $btnNext.Enabled=$true; $btnNext.Text='Finish'; $btnCancel.Visible=$false }
    }
}

function Write-Log($msg) {
    [void]$lstLog.Items.Add($msg)
    $lstLog.TopIndex = $lstLog.Items.Count - 1
    [Windows.Forms.Application]::DoEvents()
}
function Set-Status($msg, $pct) {
    $lblStatus.Text = $msg
    if ($null -ne $pct) { $pb.Value = [Math]::Max(0, [Math]::Min(100, [int]$pct)) }
    [Windows.Forms.Application]::DoEvents()
}

# ---------------------------------------------------------------------------
# the actual install work
# ---------------------------------------------------------------------------
function Get-FlipPixSource {
    # Returns a folder that contains FlipPix.UI.exe, building it if necessary.
    if (Test-Path (Join-Path $PublishDir 'FlipPix.UI.exe')) {
        Write-Log "Using prebuilt binaries: $PublishDir"
        return $PublishDir
    }
    Write-Log 'No prebuilt binaries found - attempting to build from source...'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw "FlipPix is not built and the .NET SDK was not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download, then run Setup again (or run publish.bat first)."
    }
    Set-Status 'Building FlipPix from source (this can take a few minutes)...' 5
    Write-Log 'Running dotnet publish (self-contained, win-x64)...'
    $csproj = Join-Path $RepoRoot 'FlipPix.UI\FlipPix.UI.csproj'
    $out = Join-Path $env:TEMP ('flippix_publish_log_{0}.txt' -f $PID)
    $args = @('publish', $csproj, '-c','Release','-r','win-x64','--self-contained','true',
              '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-o', $PublishDir)
    $p = Start-Process -FilePath $dotnet.Source -ArgumentList $args -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    while (-not $p.HasExited) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 200 }
    if ($p.ExitCode -ne 0) {
        $tail = (Get-Content "$out.err" -ErrorAction SilentlyContinue | Select-Object -Last 5) -join '; '
        throw "dotnet publish failed (exit $($p.ExitCode)). $tail"
    }
    Write-Log 'Build complete.'
    return $PublishDir
}

function New-Shortcut($lnkPath, $target, $workdir, $icon) {
    $dir = Split-Path $lnkPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($lnkPath)
    $sc.TargetPath = $target
    $sc.WorkingDirectory = $workdir
    $sc.IconLocation = "$icon,0"
    $sc.Description = 'FlipPix - AI image & video studio'
    $sc.Save()
}

function Start-Install {
    try {
        $script:InstallDir   = $txtDir.Text.Trim()
        $script:DesktopSC    = $chkDesktop.Checked
        $script:StartMenuSC  = $chkStart.Checked
        $script:InstallComfy = $chkComfy.Checked

        Set-Status 'Locating FlipPix binaries...' 2
        $src = Get-FlipPixSource

        Set-Status 'Creating install folder...' 10
        New-Item -ItemType Directory -Force -Path $script:InstallDir | Out-Null
        Write-Log "Install folder: $script:InstallDir"

        Set-Status 'Copying files...' 12
        $files = Get-ChildItem -Path $src -Recurse -File
        $total = [Math]::Max(1, $files.Count); $i = 0
        foreach ($f in $files) {
            $rel  = $f.FullName.Substring($src.Length).TrimStart('\')
            $dest = Join-Path $script:InstallDir $rel
            $dd   = Split-Path $dest -Parent
            if (-not (Test-Path $dd)) { New-Item -ItemType Directory -Force -Path $dd | Out-Null }
            Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
            $i++
            $pct = 12 + [int](($i / $total) * 73)
            if (($i % 10) -eq 0 -or $i -eq $total) { Set-Status ("Copying files... ({0}/{1})" -f $i, $total) $pct }
        }
        Write-Log "Copied $total files."

        $exe = Join-Path $script:InstallDir 'FlipPix.UI.exe'
        if ($script:DesktopSC) {
            Set-Status 'Creating desktop shortcut...' 88
            New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'FlipPix.lnk') $exe $script:InstallDir $exe
            Write-Log 'Desktop shortcut created.'
        }
        if ($script:StartMenuSC) {
            Set-Status 'Creating Start Menu shortcut...' 92
            $sm = Join-Path ([Environment]::GetFolderPath('Programs')) 'FlipPix\FlipPix.lnk'
            New-Shortcut $sm $exe $script:InstallDir $exe
            Write-Log 'Start Menu shortcut created.'
            $uninst = Join-Path $script:InstallDir 'Uninstall-FlipPix.exe'
            if (Test-Path $uninst) {
                $smU = Join-Path ([Environment]::GetFolderPath('Programs')) 'FlipPix\Uninstall FlipPix.lnk'
                New-Shortcut $smU $uninst $script:InstallDir $uninst
                Write-Log 'Uninstall shortcut created.'
            }
        }

        if ($script:InstallComfy) {
            Set-Status 'Launching the ComfyUI installer in a separate window...' 96
            if (Test-Path $ComfyBat) {
                Start-Process -FilePath $ComfyBat -WorkingDirectory $RepoRoot
                Write-Log 'ComfyUI installer launched (continues in its own window).'
            } else {
                Write-Log "WARNING: $ComfyBat not found - skipped ComfyUI install."
            }
        }

        Set-Status 'Done.' 100
        Write-Log 'FlipPix installation complete.'
        $script:Installed = $true
        Start-Sleep -Milliseconds 400
        Show-Step 4
    } catch {
        Write-Log "ERROR: $($_.Exception.Message)"
        [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'FlipPix Setup - Error',
            [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        # let the user go back and retry / cancel
        $btnBack.Enabled = $true; $btnCancel.Enabled = $true
        Show-Step 2
    }
}

# ---------------------------------------------------------------------------
# button handlers
# ---------------------------------------------------------------------------
$btnNext.Add_Click({
    switch ($script:step) {
        0 { Show-Step 1 }
        1 { Show-Step 2 }
        2 {
            if ([string]::IsNullOrWhiteSpace($txtDir.Text)) {
                [Windows.Forms.MessageBox]::Show('Please choose an install folder.', 'FlipPix Setup',
                    [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
                return
            }
            Show-Step 3
            Start-Install
        }
        4 {
            if ($chkLaunch.Checked -and $script:Installed) {
                $exe = Join-Path $script:InstallDir 'FlipPix.UI.exe'
                if (Test-Path $exe) { Start-Process -FilePath $exe -WorkingDirectory $script:InstallDir }
            }
            $form.Close()
        }
    }
})
$btnBack.Add_Click({
    switch ($script:step) {
        1 { Show-Step 0 }
        2 { Show-Step 1 }
    }
})
$btnCancel.Add_Click({
    if ([Windows.Forms.MessageBox]::Show('Cancel FlipPix Setup?', 'FlipPix Setup',
        [Windows.Forms.MessageBoxButtons]::YesNo, [Windows.Forms.MessageBoxIcon]::Question) -eq 'Yes') {
        $form.Close()
    }
})

Show-Step 0
[void]$form.ShowDialog()
$form.Dispose()
