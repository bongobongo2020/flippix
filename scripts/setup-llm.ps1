<#
.SYNOPSIS
    Installs FlipPix's writing assistant: llama-server (llama.cpp, CUDA) running Qwen2.5-VL 7B.

.DESCRIPTION
    FlipPix's Analyze / prompt-writing features talk to an OpenAI-compatible llama-server
    (LMStudioSettings in FlipPix settings). This sets one up on this PC:

      1. Downloads a pinned llama.cpp Windows CUDA 12.4 build (+ its CUDA runtime DLLs).
      2. Downloads Qwen2.5-VL-7B-Instruct Q4_K_M + its Q8_0 vision projector (mmproj), ~5.5 GB.
      3. Writes start-llm.bat (llama-server on 127.0.0.1:<Port>, all layers on the GPU).
      4. Optionally puts a minimized start-up shortcut in the user's Startup folder.
      5. Points FlipPix at it (LMStudioSettings.BaseUrl / SelectedModel / Servers), keeping any
         previous server in ServerHistory so it can be switched back to in FlipPix.

    Re-running is safe: finished downloads are skipped and partial ones resume.

.PARAMETER InstallDir
    Folder for the server, model and start script. Default: %LOCALAPPDATA%\FlipPix\LLM

.PARAMETER Port
    llama-server port. Default 8080.

.PARAMETER NoStartup
    Don't add the Startup-folder shortcut.

.PARAMETER Wizard
    Print '##FLIPPIX|...' progress markers for the setup wizard (see setup-common.ps1).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\setup-llm.ps1 -InstallDir D:\FlipPix\LLM
#>

[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'FlipPix\LLM'),
    [int]$Port = 8080,
    [switch]$NoStartup,
    [switch]$Wizard
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'setup-common.ps1')

# Pinned so every install gets the same, known-good server. CUDA 12.4 rather than 13.x because it
# runs on older drivers (>= 551); bump both values together after testing a newer build.
$LlamaBuild = 'b11321'
$LlamaCuda  = '12.4'
$LlamaBase  = "https://github.com/ggml-org/llama.cpp/releases/download/$LlamaBuild"
$Downloads = @(
    @{ Name = "llama-$LlamaBuild-bin-win-cuda-$LlamaCuda-x64.zip"; Bytes = 263400928; Label = 'llama-server (CUDA)' },
    @{ Name = "cudart-llama-bin-win-cuda-$LlamaCuda-x64.zip";      Bytes = 391443627; Label = 'CUDA runtime' }
)

$HfBase     = 'https://huggingface.co/ggml-org/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main'
$ModelFile  = 'Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf'
$MmprojFile = 'mmproj-Qwen2.5-VL-7B-Instruct-Q8_0.gguf'
$Models = @(
    @{ Name = $ModelFile;  Bytes = 4683072032; Label = 'Qwen2.5-VL 7B (Q4_K_M)' },
    @{ Name = $MmprojFile; Bytes = 853119712;  Label = 'Qwen2.5-VL vision projector' }
)
# The model id llama-server reports and FlipPix sends; matches the example in LMStudioSettings.
$ModelAlias = 'qwen2.5-vl-7b-instruct-q4_k_m'

$BinDir   = Join-Path $InstallDir 'bin'
$ModelDir = Join-Path $InstallDir 'models'
$DlDir    = Join-Path $InstallDir 'downloads'
New-Item -ItemType Directory -Force -Path $BinDir, $ModelDir, $DlDir | Out-Null

Write-Host 'FlipPix - writing assistant installer (llama-server + Qwen2.5-VL 7B)' -ForegroundColor Magenta

# ---------------------------------------------------------------------------
# 1. llama-server
# ---------------------------------------------------------------------------
Write-Marker 'phase' @('llm-server')
Write-Step "Installing llama-server $LlamaBuild (CUDA $LlamaCuda) -> $BinDir"
$stamp = Join-Path $BinDir 'flippix-build.txt'
if ((Test-Path (Join-Path $BinDir 'llama-server.exe')) -and (Test-Path $stamp) -and
    ((Get-Content $stamp -Raw).Trim() -eq "$LlamaBuild-cuda-$LlamaCuda")) {
    Write-Ok 'llama-server already installed'
} else {
    foreach ($d in $Downloads) {
        $zip = Join-Path $DlDir $d.Name
        Get-File "$LlamaBase/$($d.Name)" $zip $d.Bytes $d.Label
        Write-Host "  extracting $($d.Name)"
        Expand-Archive -Path $zip -DestinationPath $BinDir -Force
    }
    if (-not (Test-Path (Join-Path $BinDir 'llama-server.exe'))) {
        throw "llama-server.exe not found in $BinDir after extracting the llama.cpp build."
    }
    Set-Content -Path $stamp -Value "$LlamaBuild-cuda-$LlamaCuda" -Encoding ASCII
    Remove-Item -Path $DlDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok 'llama-server installed'
}

# ---------------------------------------------------------------------------
# 2. model + vision projector
# ---------------------------------------------------------------------------
Write-Marker 'phase' @('llm-model')
Write-Step "Downloading Qwen2.5-VL 7B -> $ModelDir"
$n = 0
foreach ($m in $Models) {
    $n++
    Write-Marker 'count' @($n, $Models.Count, $m.Label)
    Get-File "$HfBase/$($m.Name)" (Join-Path $ModelDir $m.Name) $m.Bytes $m.Label
}
Write-Ok 'model ready'

# ---------------------------------------------------------------------------
# 3. start script + start-up shortcut
# ---------------------------------------------------------------------------
Write-Marker 'phase' @('llm-config')
Write-Step 'Writing start-llm.bat'
# Bound to 127.0.0.1: only FlipPix on this PC talks to it (the iPad goes through FlipPix).
# -ngl 99 puts every layer on the GPU (~6 GB of VRAM with the projector and an 8k context).
$bat = @"
@echo off
REM FlipPix writing assistant - Qwen2.5-VL 7B on llama-server. Written by scripts\setup-llm.ps1.
title FlipPix writing assistant (llama-server :$Port)
cd /d "%~dp0"
"bin\llama-server.exe" -m "models\$ModelFile" --mmproj "models\$MmprojFile" --alias $ModelAlias --host 127.0.0.1 --port $Port -ngl 99 -c 8192
"@
$StartBat = Join-Path $InstallDir 'start-llm.bat'
Set-Content -Path $StartBat -Value $bat -Encoding ASCII
Write-Ok $StartBat

$startupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'FlipPix writing assistant.lnk'
if ($NoStartup) {
    Remove-Item -LiteralPath $startupLnk -ErrorAction SilentlyContinue
} else {
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($startupLnk)
    $sc.TargetPath = $StartBat
    $sc.WorkingDirectory = $InstallDir
    $sc.WindowStyle = 7   # minimized
    $sc.Description = 'FlipPix writing assistant (llama-server)'
    $sc.Save()
    Write-Ok 'starts minimized when you sign in to Windows'
}

# ---------------------------------------------------------------------------
# 4. point FlipPix at it
# ---------------------------------------------------------------------------
Write-Step 'Pointing FlipPix at this writing assistant'
$url  = "http://127.0.0.1:$Port"
$dir  = Join-Path $env:APPDATA 'FlipPix'
$file = Join-Path $dir 'settings.json'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$settings = $null
if (Test-Path $file) {
    try { $settings = Get-Content $file -Raw | ConvertFrom-Json } catch { $settings = $null }
}
if (-not $settings) { $settings = [PSCustomObject]@{} }
$lm = $settings.LMStudioSettings
if (-not $lm) { $lm = [PSCustomObject]@{} }

# Keep the previous server reachable from FlipPix's saved-servers list.
$history = @($lm.ServerHistory | Where-Object { $_ })
$old = "$($lm.BaseUrl)".TrimEnd('/')
if ($old -and $old -ne $url -and $history -notcontains $old) { $history = @($old) + $history }
$servers = @($lm.Servers | Where-Object { $_ -and "$($_.BaseUrl)".TrimEnd('/') -ne $url })
foreach ($sv in $servers) { $sv | Add-Member -NotePropertyName 'IsDefault' -NotePropertyValue $false -Force }
$servers += [PSCustomObject]@{
    Name = 'This PC'; BaseUrl = $url; Model = $ModelAlias; ModelName = 'Qwen2.5-VL 7B'; IsDefault = $true
}

$lm | Add-Member -NotePropertyName 'BaseUrl'       -NotePropertyValue $url        -Force
$lm | Add-Member -NotePropertyName 'SelectedModel' -NotePropertyValue $ModelAlias -Force
$lm | Add-Member -NotePropertyName 'ServerHistory' -NotePropertyValue $history    -Force
$lm | Add-Member -NotePropertyName 'Servers'       -NotePropertyValue $servers    -Force
$settings | Add-Member -NotePropertyName 'LMStudioSettings' -NotePropertyValue $lm -Force
$settings | ConvertTo-Json -Depth 32 | Set-Content -Path $file -Encoding UTF8
Write-Ok "FlipPix uses $url ($ModelAlias)"

Write-Host "`nWriting assistant ready. Start it any time with:" -ForegroundColor Magenta
Write-Host "  $StartBat"
