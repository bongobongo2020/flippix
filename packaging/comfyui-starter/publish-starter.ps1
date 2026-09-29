<#
.SYNOPSIS
    Uploads the built starter bundle (build-starter.ps1) and its .sha256 to the Hugging Face repo
    that FlipPix Setup downloads it from (starter.json: bundle.repo / bundle.file).

    Needs the Hugging Face CLI, logged in once:
        pip install -U "huggingface_hub[cli]"
        hf auth login
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $Here '..\..')
$Manifest = Get-Content (Join-Path $Here 'starter.json') -Raw | ConvertFrom-Json
$Bundle = Join-Path $RepoRoot ('release\comfyui-starter\' + $Manifest.bundle.file)

if (-not (Test-Path "$Bundle.sha256")) { throw "Build it first: packaging\comfyui-starter\build-starter.ps1 ($Bundle not found)" }
if (-not (Get-Command hf -ErrorAction SilentlyContinue)) {
    throw 'The hf CLI is not installed: pip install -U "huggingface_hub[cli]", then hf auth login.'
}

& hf repo create $Manifest.bundle.repo --repo-type model --exist-ok
foreach ($f in $Bundle, "$Bundle.sha256") {
    & hf upload $Manifest.bundle.repo $f (Split-Path $f -Leaf) --repo-type model --commit-message "starter $($Manifest.version)"
    if ($LASTEXITCODE -ne 0) { throw "upload failed: $f" }
}
Write-Host "`nPublished: $($Manifest.bundle.url)" -ForegroundColor Green
