<#
.SYNOPSIS
    Helpers shared by the FlipPix setup scripts (dot-sourced, not run directly).

.DESCRIPTION
    * Console logging (Write-Step / Write-Ok / Write-Warn2 / Write-Err2).
    * Write-Marker: machine-readable '##FLIPPIX|kind|field|...' lines. They are only printed when
      the calling script was started with -Wizard, i.e. by the setup wizard
      (flippix-installer.ps1), which reads the child's stdout and drives its progress bars from
      them. Kinds:
          phase|<key>                          a new install phase starts
          count|<i>|<n>|<label>                item i of n inside the phase
          download|<file.part>|<bytes>|<label> a download starts; the wizard watches the .part grow
    * Get-File: resumable download. Data goes to '<file>.part' and is renamed when complete, so an
      interrupted download is resumed on the next run instead of being mistaken for a finished one.
    * Read-ModelManifest / ConvertTo-Bytes: the 'path | ~size | url' manifest format.

    Callers set $Wizard (a [switch] param) before calling these; PowerShell resolves it from the
    dot-sourcing script's scope at call time.
#>

# GitHub and Hugging Face refuse TLS 1.0/1.1, which Windows PowerShell 5.1 may still default to.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# The wizard reads our redirected stdout as UTF-8, so paths with non-ASCII user names survive in
# the markers. No BOM, or it would prefix the first line.
if ($Wizard) { [Console]::OutputEncoding = New-Object Text.UTF8Encoding $false }

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  [ok] $m" -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "  [!] $m"  -ForegroundColor Yellow }
function Write-Err2($m) { Write-Host "  [x] $m"  -ForegroundColor Red }

function Write-Marker([string]$Kind, [object[]]$Fields = @()) {
    if (-not $Wizard) { return }
    Write-Host ('##FLIPPIX|' + ((@($Kind) + @($Fields | ForEach-Object { "$_" -replace '\|', '/' })) -join '|'))
}

function ConvertTo-Bytes([string]$Size) {
    # '~4.2 GB' / '~168 MB' -> bytes (approximate; only used for progress and disk estimates).
    if ($Size -match '([\d.]+)\s*([KMGT]?B)') {
        $n = [double]::Parse($matches[1], [Globalization.CultureInfo]::InvariantCulture)
        switch ($matches[2].ToUpper()) {
            'TB' { return [long]($n * 1TB) }
            'GB' { return [long]($n * 1GB) }
            'MB' { return [long]($n * 1MB) }
            'KB' { return [long]($n * 1KB) }
            default { return [long]$n }
        }
    }
    return [long]0
}

function Read-ModelManifest($file) {
    # Each entry: Path (relative to the models folder) | Size | Url.
    @(
        if (Test-Path $file) {
            Get-Content $file | ForEach-Object {
                $line = $_.Trim()
                if ($line -eq '' -or $line.StartsWith('#')) { return }
                $parts = $line -split '\|', 3
                if ($parts.Count -ne 3) { return }
                @{ Path = $parts[0].Trim() -replace '/', '\'; Size = $parts[1].Trim(); Url = $parts[2].Trim()
                   Bytes = (ConvertTo-Bytes $parts[1]) }
            }
        }
    )
}

function Get-File($Url, $OutFile, [long]$ExpectedBytes = 0, [string]$Label = '') {
    if (Test-Path $OutFile) {
        $sizeMB = [math]::Round((Get-Item $OutFile).Length / 1MB, 1)
        Write-Ok "already downloaded ($sizeMB MB): $(Split-Path $OutFile -Leaf)"
        return
    }
    if (-not $Label) { $Label = Split-Path $OutFile -Leaf }
    $part = "$OutFile.part"
    Write-Marker 'download' @($part, $ExpectedBytes, $Label)
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        $curlArgs = @('-L', '--fail', '--retry', '3', '-C', '-', '-o', $part, $Url)
        # Under the wizard nobody sees curl's meter; it would only bloat the redirected log.
        if ($Wizard) { $curlArgs = @('-sS') + $curlArgs }
        & $curl.Source @curlArgs
        if ($LASTEXITCODE -ne 0) { throw "curl failed downloading $Url (exit $LASTEXITCODE)" }
    } else {
        Invoke-WebRequest -Uri $Url -OutFile $part -UseBasicParsing
    }
    Move-Item -LiteralPath $part -Destination $OutFile -Force
}
