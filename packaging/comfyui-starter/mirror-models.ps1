<#
.SYNOPSIS
    Uploads the starter's mirrored models (starter.json entries with "mirror": true) to the mirror
    Hugging Face repo, at the same models-relative path the FlipPix Models window downloads from.

.DESCRIPTION
    These are the files with no public copy that matches what the server runs: local conversions,
    a renamed upscaler, and three Civitai LoRAs. Check each LoRA's licence allows redistribution
    before running this against a public repo.

    Needs the Hugging Face CLI, logged in once:
        pip install -U "huggingface_hub[cli]"
        hf auth login

.PARAMETER ModelsRoot
    The models folder holding them. Default Z:\ (the ComfyUI server's model storage).

.PARAMETER Private
    Create the repo private if it doesn't exist yet (downloads then need a token).
#>
[CmdletBinding()]
param(
    [string]$ModelsRoot = 'Z:\',
    [switch]$Private
)

$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Manifest = Get-Content (Join-Path $Here 'starter.json') -Raw | ConvertFrom-Json
$Repo = $Manifest.mirror.repo

if (-not (Get-Command hf -ErrorAction SilentlyContinue)) {
    throw 'The hf CLI is not installed: pip install -U "huggingface_hub[cli]", then hf auth login.'
}

$files = @($Manifest.models | Where-Object { $_.mirror })
foreach ($m in $files) {
    $local = Join-Path $ModelsRoot ($m.path -replace '/', '\')
    if (-not (Test-Path -LiteralPath $local)) { throw "not found: $local" }
    $size = (Get-Item -LiteralPath $local).Length
    if ($size -ne $m.size) { throw "$local is $size bytes; starter.json says $($m.size). Update the manifest if the file changed on purpose." }
}

$visibility = if ($Private) { '--private' } else { @() }
& hf repo create $Repo --repo-type model --exist-ok @visibility
if ($LASTEXITCODE -ne 0) { throw "could not create $Repo" }

foreach ($m in $files) {
    $local = Join-Path $ModelsRoot ($m.path -replace '/', '\')
    Write-Host "==> $($m.path)" -ForegroundColor Cyan
    & hf upload $Repo $local $m.path --repo-type model --commit-message "mirror $($m.path)"
    if ($LASTEXITCODE -ne 0) { throw "upload failed: $($m.path)" }
}
Write-Host "`nMirrored $($files.Count) files to https://huggingface.co/$Repo" -ForegroundColor Green
