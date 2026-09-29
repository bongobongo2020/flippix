<#
.SYNOPSIS
    FlipPix Models: gets the model files the phone's features need into ComfyUI, from models the
    user already has or by downloading only what's missing.

.DESCRIPTION
    Reads the starter manifest (starter.json: features + models) and, for each feature ticked:
      * a file already in the models folder, at the right size, counts as installed;
      * "I already have models..." scans a folder by file name and size. A file found at its usual
        place under a models folder registers that folder in extra_model_paths.yaml (nothing copied);
        anything else is hard-linked when on the same drive, copied when not;
      * the rest downloads with curl.exe, resuming from a .part file, so Stop and a later Download
        (or a dropped connection) never starts a file over.

    Opened by FlipPix Setup when it finishes, and from the Start Menu (FlipPix > FlipPix Models).

.PARAMETER Check
    Print what each feature still needs and exit, without opening the window.

.PARAMETER Find
    With -Check: first use the models already in this folder, as "I already have models..." does
    (files that would need copying are listed, not copied).

.PARAMETER ModelsDir
    The ComfyUI models folder. Default: ComfyUIFolderPath\models from FlipPix's settings, else
    %USERPROFILE%\ComfyUI_FlipPix\ComfyUI\models (where Setup puts the starter).
#>
[CmdletBinding()]
param(
    [string]$ModelsDir = '',
    [string]$ManifestPath = '',
    [switch]$Check,
    [string]$Find = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------------------------------------------------------------------------
# manifest + folders
# ---------------------------------------------------------------------------
if (-not $ManifestPath) {
    foreach ($p in (Join-Path $ScriptDir 'starter.json'),
                   (Join-Path $ScriptDir '..\packaging\comfyui-starter\starter.json')) {
        if (Test-Path $p) { $ManifestPath = $p; break }
    }
}
if (-not $ManifestPath -or -not (Test-Path $ManifestPath)) {
    [Windows.Forms.MessageBox]::Show("starter.json was not found next to this script.", 'FlipPix Models') | Out-Null
    exit 1
}
$Manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json

function Get-DefaultModelsDir {
    $settingsFile = Join-Path $env:APPDATA 'FlipPix\settings.json'
    if (Test-Path $settingsFile) {
        try {
            $s = Get-Content $settingsFile -Raw | ConvertFrom-Json
            if ($s.ComfyUIFolderPath -and (Test-Path $s.ComfyUIFolderPath)) { return (Join-Path $s.ComfyUIFolderPath 'models') }
        } catch {}
    }
    return (Join-Path $env:USERPROFILE 'ComfyUI_FlipPix\ComfyUI\models')
}
if (-not $ModelsDir) { $ModelsDir = Get-DefaultModelsDir }
$script:ModelsDir = $ModelsDir

# ComfyUI registers the new folder names and the old ones for the same kinds of model.
$Aliases = @{ 'diffusion_models' = @('diffusion_models', 'unet'); 'text_encoders' = @('text_encoders', 'clip') }

function Get-Variants($path) {
    $cat, $rest = $path -split '/', 2
    $cats = if ($Aliases.ContainsKey($cat)) { $Aliases[$cat] } else { @($cat) }
    return @($cats | ForEach-Object { ($_ + '/' + $rest) -replace '/', '\' })
}

# The ComfyUI folder the models folder sits in, or $null when it isn't in one.
function Get-ComfyDir {
    $parent = Split-Path $script:ModelsDir -Parent
    if ($parent -and (Test-Path (Join-Path $parent 'main.py'))) { return $parent }
    return $null
}

function Get-YamlPath { $c = Get-ComfyDir; if ($c) { Join-Path $c 'extra_model_paths.yaml' } }

# Folders this window registered earlier: "flippix_found_N:" sections in extra_model_paths.yaml.
function Get-FoundRoots {
    $yaml = Get-YamlPath
    if (-not $yaml -or -not (Test-Path $yaml)) { return @() }
    $text = Get-Content $yaml -Raw
    return @([regex]::Matches($text, '(?m)^flippix_found_\d+:\s*\r?\n\s+base_path:\s*(.+?)\s*$') |
        ForEach-Object { $_.Groups[1].Value -replace '^["'']|["'']$', '' })
}

function Add-FoundRoot($root) {
    $yaml = Get-YamlPath
    if ((Get-FoundRoots) -contains $root) { return }
    $n = 1
    $text = if (Test-Path $yaml) { Get-Content $yaml -Raw } else { '' }
    while ($text -match "(?m)^flippix_found_$n\:") { $n++ }
    $block = @"

flippix_found_$($n):
    base_path: "$($root -replace '\\', '/')"
    checkpoints: checkpoints/
    diffusion_models: |
        diffusion_models/
        unet/
    text_encoders: |
        text_encoders/
        clip/
    vae: vae/
    loras: loras/
    latent_upscale_models: latent_upscale_models/
"@
    Add-Content -Path $yaml -Value $block -Encoding UTF8
    $script:NeedsRestart = $true
}

function Test-Installed($m) {
    foreach ($root in @($script:ModelsDir) + (Get-FoundRoots)) {
        foreach ($v in Get-Variants $m.path) {
            $f = Join-Path $root $v
            if ((Test-Path -LiteralPath $f) -and (Get-Item -LiteralPath $f).Length -eq [int64]$m.size) { return $true }
        }
    }
    return $false
}

function Get-Url($m) {
    if ($m.url) { return $m.url }
    $segs = ($m.path -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }
    return "https://huggingface.co/$($Manifest.mirror.repo)/resolve/main/" + ($segs -join '/')
}

function Format-GB($bytes) { '{0:N1} GB' -f ($bytes / 1GB) }

# ---------------------------------------------------------------------------
# reusing models the user already has
# ---------------------------------------------------------------------------

# Matches missing files in $src by name and exact size. Registers or hard-links what it can and
# returns what would have to be copied; the caller decides about that.
function Use-ExistingModels($src) {
    $byName = @{}
    Get-ChildItem -LiteralPath $src -Recurse -File -Include *.safetensors, *.gguf -ErrorAction SilentlyContinue | ForEach-Object {
        if (-not $byName.ContainsKey($_.Name)) { $byName[$_.Name] = @() }
        $byName[$_.Name] += $_
    }

    $found = 0; $copyQueue = @()
    foreach ($m in @($Manifest.models | Where-Object { -not (Test-Installed $_) })) {
        $leaf = ($m.path -split '/')[-1]
        $hit = @($byName[$leaf] | Where-Object { $_ -and $_.Length -eq [int64]$m.size }) | Select-Object -First 1
        if (-not $hit) { continue }
        $found++
        # At its usual place under some models folder? Then ComfyUI can read that whole folder.
        $root = $null
        foreach ($v in Get-Variants $m.path) {
            if ($hit.FullName.EndsWith('\' + $v, [StringComparison]::OrdinalIgnoreCase)) {
                $root = $hit.FullName.Substring(0, $hit.FullName.Length - $v.Length - 1); break
            }
        }
        $dest = Join-Path $script:ModelsDir ($m.path -replace '/', '\')
        if ($root -and (Get-ComfyDir)) {
            Add-FoundRoot $root
            Write-Log "  using $($hit.FullName)"
        } elseif ([IO.Path]::GetPathRoot($hit.FullName) -eq [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($script:ModelsDir))) {
            New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
            New-Item -ItemType HardLink -Path $dest -Target $hit.FullName | Out-Null
            Write-Log "  linked $($hit.Name)"
        } else {
            $copyQueue += [PSCustomObject]@{ Model = $m; Source = $hit; Dest = $dest }
        }
    }
    return [PSCustomObject]@{ Found = $found; CopyQueue = $copyQueue }
}

function Write-Log($msg) { Write-Host $msg }

if ($Check) {
    if ($Find) {
        $r = Use-ExistingModels $Find
        Write-Host "Found $($r.Found) file(s) in $Find"
        foreach ($c in $r.CopyQueue) { Write-Host "  would copy $($c.Source.FullName)" }
    }
    Write-Host "Models folder: $script:ModelsDir"
    foreach ($f in $Manifest.features) {
        $missing = @($Manifest.models | Where-Object { $_.features -contains $f.key } | Where-Object { -not (Test-Installed $_) })
        $bytes = ($missing | Measure-Object -Property size -Sum).Sum
        $state = if ($missing.Count -eq 0) { 'installed' } else { "$($missing.Count) file(s), $(Format-GB $bytes) to get" }
        Write-Host ("  {0,-14} {1}" -f $f.name, $state)
        foreach ($m in $missing) { Write-Host "      $($m.path)  <- $(Get-Url $m)" }
    }
    exit 0
}

# ---------------------------------------------------------------------------
# window (the same classic look as FlipPix Setup)
# ---------------------------------------------------------------------------
$clSilver = [Drawing.Color]::FromArgb(192,192,192)
$clWhite  = [Drawing.Color]::White
$fnt      = New-Object Drawing.Font('MS Sans Serif', 8.25)
$fntBold  = New-Object Drawing.Font('MS Sans Serif', 8.25, [Drawing.FontStyle]::Bold)

$form = New-Object Windows.Forms.Form
$form.Text = 'FlipPix Models'
$form.ClientSize = New-Object Drawing.Size(520, 470)
$form.FormBorderStyle = 'FixedDialog'; $form.MaximizeBox = $false
$form.StartPosition = 'CenterScreen'; $form.BackColor = $clSilver; $form.Font = $fnt
$icon = Join-Path $ScriptDir '..\flippix.ico'
if (-not (Test-Path $icon)) { $icon = Join-Path $ScriptDir 'flippix.ico' }
if (Test-Path $icon) { try { $form.Icon = New-Object Drawing.Icon($icon) } catch {} }

function New-Label($text, $x, $y, $w, $h) {
    $l = New-Object Windows.Forms.Label
    $l.Text = $text; $l.Location = New-Object Drawing.Point($x,$y); $l.Size = New-Object Drawing.Size($w,$h)
    return $l
}

$header = New-Object Windows.Forms.Panel
$header.Location = New-Object Drawing.Point(0,0); $header.Size = New-Object Drawing.Size(520,56); $header.BackColor = $clWhite
$hT = New-Label 'Models for your phone' 16 10 480 16; $hT.Font = $fntBold; $hT.BackColor = $clWhite
$hD = New-Label 'Tick what this PC should be able to make. Only files you do not already have are downloaded.' 30 30 480 20
$hD.BackColor = $clWhite
$header.Controls.AddRange(@($hT, $hD))
$form.Controls.Add($header)

$form.Controls.Add((New-Label 'Models folder:' 16 70 90 16))
$txtDir = New-Object Windows.Forms.TextBox
$txtDir.Location = New-Object Drawing.Point(16,88); $txtDir.Size = New-Object Drawing.Size(404,20); $txtDir.ReadOnly = $true
$btnDir = New-Object Windows.Forms.Button
$btnDir.Text = 'Change...'; $btnDir.Location = New-Object Drawing.Point(428,87); $btnDir.Size = New-Object Drawing.Size(76,23)
$form.Controls.AddRange(@($txtDir, $btnDir))

$grp = New-Object Windows.Forms.GroupBox
$grp.Text = 'What should this PC be able to make?'
$grp.Location = New-Object Drawing.Point(16,120); $grp.Size = New-Object Drawing.Size(488,140)
$form.Controls.Add($grp)

$script:Checks = @{}
$script:StateLabels = @{}
$y = 22
foreach ($f in $Manifest.features) {
    $c = New-Object Windows.Forms.CheckBox
    $c.Text = "$($f.name) - $($f.blurb)"; $c.Checked = $true
    $c.Location = New-Object Drawing.Point(12,$y); $c.Size = New-Object Drawing.Size(360,20)
    $s = New-Label '' 376 ($y + 3) 104 16
    $s.TextAlign = 'TopRight'
    $grp.Controls.AddRange(@($c, $s))
    $script:Checks[$f.key] = $c
    $script:StateLabels[$f.key] = $s
    $y += 27
}

$lblTotal = New-Label '' 16 268 488 16
$btnHave = New-Object Windows.Forms.Button
$btnHave.Text = 'I already have models...'; $btnHave.Location = New-Object Drawing.Point(16,290); $btnHave.Size = New-Object Drawing.Size(160,23)
$lblHave = New-Label 'Finds them by name in a folder you pick, and uses them instead of downloading.' 184 295 330 16
$form.Controls.AddRange(@($lblTotal, $btnHave, $lblHave))

$lblNow = New-Label 'Ready.' 16 324 488 16
$pb = New-Object Windows.Forms.ProgressBar
$pb.Location = New-Object Drawing.Point(16,342); $pb.Size = New-Object Drawing.Size(488,20); $pb.Maximum = 1000
$lst = New-Object Windows.Forms.ListBox
$lst.Location = New-Object Drawing.Point(16,368); $lst.Size = New-Object Drawing.Size(488,56); $lst.BackColor = $clWhite
$form.Controls.AddRange(@($lblNow, $pb, $lst))

$btnGo = New-Object Windows.Forms.Button
$btnGo.Text = 'Download'; $btnGo.Location = New-Object Drawing.Point(266,436); $btnGo.Size = New-Object Drawing.Size(76,23)
$btnStop = New-Object Windows.Forms.Button
$btnStop.Text = 'Stop'; $btnStop.Location = New-Object Drawing.Point(347,436); $btnStop.Size = New-Object Drawing.Size(76,23); $btnStop.Enabled = $false
$btnClose = New-Object Windows.Forms.Button
$btnClose.Text = 'Close'; $btnClose.Location = New-Object Drawing.Point(428,436); $btnClose.Size = New-Object Drawing.Size(76,23)
$form.Controls.AddRange(@($btnGo, $btnStop, $btnClose))

function Write-Log($msg) {
    [void]$lst.Items.Add($msg); $lst.TopIndex = $lst.Items.Count - 1
    [Windows.Forms.Application]::DoEvents()
}

# ---------------------------------------------------------------------------
# state
# ---------------------------------------------------------------------------
function Get-Wanted {
    $keys = @($script:Checks.Keys | Where-Object { $script:Checks[$_].Checked })
    # A file shared by two features is one download.
    return @($Manifest.models | Where-Object { $m = $_; @($m.features | Where-Object { $keys -contains $_ }).Count -gt 0 } |
        Where-Object { -not (Test-Installed $_) })
}

function Update-State {
    $txtDir.Text = $script:ModelsDir
    foreach ($f in $Manifest.features) {
        $missing = @($Manifest.models | Where-Object { $_.features -contains $f.key } | Where-Object { -not (Test-Installed $_) })
        $bytes = ($missing | Measure-Object -Property size -Sum).Sum
        $script:StateLabels[$f.key].Text = if ($missing.Count -eq 0) { 'installed' } else { (Format-GB $bytes) + ' to get' }
    }
    $wanted = Get-Wanted
    $need = [int64](($wanted | Measure-Object -Property size -Sum).Sum)
    $free = $null
    try {
        $probe = $script:ModelsDir
        while ($probe -and -not (Test-Path $probe)) { $probe = Split-Path $probe -Parent }
        if ($probe) { $free = (New-Object IO.DriveInfo ([IO.Path]::GetPathRoot((Resolve-Path $probe).Path))).AvailableFreeSpace }
    } catch {}
    $freeText = if ($null -ne $free) { " - free on that drive: $(Format-GB $free)" } else { '' }
    $lblTotal.Text = if ($wanted.Count -eq 0) { 'Everything ticked is installed.' } else { "To download: $(Format-GB $need) in $($wanted.Count) file(s)$freeText" }
    $lblTotal.ForeColor = if ($null -ne $free -and $need -gt $free) { [Drawing.Color]::DarkRed } else { [Drawing.Color]::Black }
    $btnGo.Enabled = $wanted.Count -gt 0 -and -not $script:Busy
}

# ---------------------------------------------------------------------------
# running one long job (curl or robocopy) while the window stays responsive
# ---------------------------------------------------------------------------
$script:Busy = $false
$script:Stop = $false
$script:NeedsRestart = $false

function Wait-Job2($proc, $watchFile, $total, $label, $doneBefore, $grand) {
    $null = $proc.Handle   # without this, ExitCode reads back empty once the process is gone
    $last = 0; $lastT = [DateTime]::UtcNow; $rate = 0
    while (-not $proc.HasExited) {
        if ($script:Stop) { try { $proc.Kill() } catch {}; break }
        Start-Sleep -Milliseconds 250
        $have = 0
        if (Test-Path -LiteralPath $watchFile) { $have = (Get-Item -LiteralPath $watchFile).Length }
        $now = [DateTime]::UtcNow
        $dt = ($now - $lastT).TotalSeconds
        if ($dt -ge 1) { $rate = 0.7 * $rate + 0.3 * (($have - $last) / $dt); $last = $have; $lastT = $now }
        $eta = if ($rate -gt 0) { ' - ' + [TimeSpan]::FromSeconds([int](($grand - $doneBefore - $have) / $rate)).ToString() + ' left' } else { '' }
        $lblNow.Text = "$label  $(Format-GB $have) of $(Format-GB $total)  ($('{0:N0}' -f ($rate / 1MB)) MB/s$eta)"
        if ($grand -gt 0) { $pb.Value = [Math]::Min(1000, [int](1000 * ($doneBefore + $have) / $grand)) }
        [Windows.Forms.Application]::DoEvents()
    }
    $proc.WaitForExit()
    return $proc.ExitCode
}

function Invoke-Download($m, $doneBefore, $grand) {
    $dest = Join-Path $script:ModelsDir ($m.path -replace '/', '\')
    New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
    $part = "$dest.part"
    $name = Split-Path $dest -Leaf
    if (-not ((Test-Path -LiteralPath $part) -and (Get-Item -LiteralPath $part).Length -eq [int64]$m.size)) {
        $cargs = @('-L', '--fail', '--silent', '--show-error', '--retry', '5', '--retry-delay', '5', '-C', '-', '-o', "`"$part`"")
        if ($env:HF_TOKEN -and -not $m.url) { $cargs += @('-H', "`"Authorization: Bearer $($env:HF_TOKEN)`"") }
        $cargs += "`"$(Get-Url $m)`""
        $p = Start-Process -FilePath 'curl.exe' -ArgumentList $cargs -PassThru -WindowStyle Hidden
        $code = Wait-Job2 $p $part ([int64]$m.size) "Downloading $name" $doneBefore $grand
        if ($script:Stop) { return $false }
        if ($code -ne 0) { Write-Log "  $name failed (curl exit $code). Download again to resume it."; return $false }
    }
    $got = (Get-Item -LiteralPath $part).Length
    if ($got -ne [int64]$m.size) {
        Write-Log "  $name is $got bytes, expected $($m.size): removed, it will download fresh next time."
        Remove-Item -LiteralPath $part -Force
        return $false
    }
    Move-Item -LiteralPath $part $dest -Force
    Write-Log "  $name"
    return $true
}

# ---------------------------------------------------------------------------
# "I already have models..."
# ---------------------------------------------------------------------------
function Find-Existing {
    $dlg = New-Object Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Pick the folder that holds your models (for example ComfyUI\models). Sub-folders are searched too.'
    if ($dlg.ShowDialog() -ne 'OK') { return }
    $src = $dlg.SelectedPath
    $script:Busy = $true; Update-State
    $lblNow.Text = "Searching $src ..."; [Windows.Forms.Application]::DoEvents()

    $r = Use-ExistingModels $src
    $found = $r.Found; $copyQueue = $r.CopyQueue

    $copyBytes = [int64](($copyQueue | ForEach-Object { $_.Source.Length } | Measure-Object -Sum).Sum)
    if ($copyQueue.Count -gt 0) {
        $ans = [Windows.Forms.MessageBox]::Show(
            "$($copyQueue.Count) file(s) ($(Format-GB $copyBytes)) are on another drive and not in a models folder layout ComfyUI can read directly. Copy them now?",
            'FlipPix Models', 'YesNo', 'Question')
        if ($ans -eq 'Yes') {
            $done = 0
            foreach ($c in $copyQueue) {
                if ($script:Stop) { break }
                New-Item -ItemType Directory -Force -Path (Split-Path $c.Dest -Parent) | Out-Null
                $rargs = @("`"$($c.Source.DirectoryName)`"", "`"$(Split-Path $c.Dest -Parent)`"", "`"$($c.Source.Name)`"", '/J', '/NJH', '/NJS', '/NP')
                $p = Start-Process -FilePath 'robocopy.exe' -ArgumentList $rargs -PassThru -WindowStyle Hidden
                $code = Wait-Job2 $p $c.Dest $c.Source.Length "Copying $($c.Source.Name)" $done $copyBytes
                if ($code -ge 8) { Write-Log "  copy failed: $($c.Source.Name) (robocopy $code)" } else { Write-Log "  copied $($c.Source.Name)" }
                $done += $c.Source.Length
            }
        }
    }

    $lblNow.Text = if ($found -eq 0) { "Nothing FlipPix needs was found in $src." } else { "Found $found file(s) in $src." }
    if ($script:NeedsRestart) { Write-Log '  ComfyUI reads new model folders when it starts: restart it (or FlipPix) once.' }
    $script:Busy = $false; $script:Stop = $false
    Update-State
}

# ---------------------------------------------------------------------------
# download
# ---------------------------------------------------------------------------
function Start-Downloads {
    $wanted = Get-Wanted
    if ($wanted.Count -eq 0) { return }
    $grand = [int64](($wanted | Measure-Object -Property size -Sum).Sum)
    $script:Busy = $true; $script:Stop = $false
    $btnGo.Enabled = $false; $btnStop.Enabled = $true; $btnHave.Enabled = $false; $btnDir.Enabled = $false
    foreach ($c in $script:Checks.Values) { $c.Enabled = $false }
    Write-Log "Downloading $($wanted.Count) file(s), $(Format-GB $grand)"
    $done = 0; $failed = 0
    foreach ($m in $wanted) {
        if ($script:Stop) { break }
        if (-not (Invoke-Download $m $done $grand)) { if (-not $script:Stop) { $failed++ } }
        $done += [int64]$m.size
    }
    $lblNow.Text = if ($script:Stop) { 'Stopped. Download again to carry on where it left off.' }
                   elseif ($failed) { "$failed file(s) did not finish. Download again to retry them." }
                   else { 'All done. Open FlipPix and pair your phone.' }
    if (-not $script:Stop -and -not $failed) { $pb.Value = 1000 }
    $script:Busy = $false; $script:Stop = $false
    $btnStop.Enabled = $false; $btnHave.Enabled = $true; $btnDir.Enabled = $true
    foreach ($c in $script:Checks.Values) { $c.Enabled = $true }
    Update-State
}

# ---------------------------------------------------------------------------
# events
# ---------------------------------------------------------------------------
foreach ($c in $script:Checks.Values) { $c.Add_CheckedChanged({ if (-not $script:Busy) { Update-State } }) }
$btnDir.Add_Click({
    $dlg = New-Object Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'The ComfyUI models folder to fill (it holds diffusion_models, vae, loras...)'
    $dlg.SelectedPath = $script:ModelsDir
    if ($dlg.ShowDialog() -eq 'OK') { $script:ModelsDir = $dlg.SelectedPath; Update-State }
})
$btnHave.Add_Click({ Find-Existing })
$btnGo.Add_Click({ Start-Downloads })
$btnStop.Add_Click({ $script:Stop = $true; $lblNow.Text = 'Stopping...' })
$btnClose.Add_Click({ $form.Close() })
$form.Add_FormClosing({
    param($s, $e)
    if ($script:Busy) {
        $ans = [Windows.Forms.MessageBox]::Show('Stop and close? What has downloaded so far is kept, and the next Download carries on from there.',
            'FlipPix Models', 'YesNo', 'Question')
        if ($ans -ne 'Yes') { $e.Cancel = $true; return }
        $script:Stop = $true   # the running job's wait loop kills curl or robocopy
    }
})

Update-State
[void]$form.ShowDialog()
$form.Dispose()
