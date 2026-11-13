<#
.SYNOPSIS
    Builds a clean, portable copy of OpenBird in dist\OpenBird-<version>.
    The copy contains source only: run setup.ps1 inside it on the target PC.
#>
param(
    [string]$Version = "2.5.0"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Target = Join-Path $Root "dist\OpenBird-$Version"

if (Test-Path $Target) { Remove-Item -Path $Target -Recurse -Force }
New-Item -ItemType Directory -Path $Target -Force | Out-Null

Copy-Item -Path (Join-Path $Root "openbird") -Destination (Join-Path $Target "openbird") -Recurse
Get-ChildItem -Path $Target -Filter "__pycache__" -Recurse -Directory | Remove-Item -Recurse -Force
if (Test-Path (Join-Path $Root "assets")) {
    Copy-Item -Path (Join-Path $Root "assets") -Destination (Join-Path $Target "assets") -Recurse
}
foreach ($f in @("main.py", "requirements.txt", "setup.ps1", "Start-OpenBird.bat", "config.example.json", "README.md")) {
    Copy-Item -Path (Join-Path $Root $f) -Destination $Target
}

$files = Get-ChildItem -Path $Target -Recurse -File
$sizeMb = ($files | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("Packaged {0} files ({1:N2} MB) into {2}" -f $files.Count, $sizeMb, $Target) -ForegroundColor Green
