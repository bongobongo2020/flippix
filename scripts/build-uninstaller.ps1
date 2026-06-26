<#
.SYNOPSIS
    Compile scripts\uninstall-flippix.cs into Uninstall-FlipPix.exe.

.DESCRIPTION
    Uses the .NET Framework C# compiler (csc.exe) that ships with every modern
    Windows, so the resulting exe is tiny (a few KB), needs no .NET runtime
    install, and runs on any Windows 10/11 machine. The repo icon is embedded.

    make-release.ps1 calls this so each release package ships Uninstall-FlipPix.exe.

.PARAMETER OutPath
    Where to write the exe. Default: <repo>\publish\Uninstall-FlipPix.exe so the
    installer (which copies publish\) deploys it into the install folder.
#>
[CmdletBinding()]
param([string]$OutPath = '')

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$Src       = Join-Path $ScriptDir 'uninstall-flippix.cs'
$Icon      = Join-Path $RepoRoot 'flippix.ico'
if (-not $OutPath) { $OutPath = Join-Path $RepoRoot 'publish\Uninstall-FlipPix.exe' }

if (-not (Test-Path $Src)) { throw "Source not found: $Src" }

# Locate the .NET Framework csc.exe (64-bit preferred, then 32-bit).
$csc = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe (.NET Framework 4.x) not found. It ships with Windows; ensure .NET Framework is enabled." }

$outDir = Split-Path -Parent $OutPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    "/out:$OutPath",
    '/optimize+',
    '/reference:System.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll'
)
if (Test-Path $Icon) { $cscArgs += "/win32icon:$Icon" }
$cscArgs += $Src

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc.exe failed (exit $LASTEXITCODE)." }

$size = [math]::Round((Get-Item $OutPath).Length / 1KB, 1)
Write-Host "[ok] built $OutPath ($size KB)" -ForegroundColor Green
