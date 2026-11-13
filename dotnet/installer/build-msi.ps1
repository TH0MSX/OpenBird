<#
.SYNOPSIS
    Builds OpenBird and packs it into a Windows Installer (.msi) package.

.DESCRIPTION
    Run this on the Windows PC (it builds for that PC's own processor, ARM64 or x64).
    The result is dist\OpenBird-<version>-<arch>.msi. Double-click it to install:
      - installs to %LOCALAPPDATA%\Programs\OpenBird, no administrator rights needed
      - Start menu shortcut, optional desktop shortcut
      - setup option "Start OpenBird when I sign in to Windows"
      - "Open OpenBird now" on the last page
      - appears in Settings > Apps, and uninstalls cleanly
    Notes, recordings and settings (%LOCALAPPDATA%\OpenBird, Documents\OpenBird) are never touched.
    The packaging tool is WiX Toolset 5 (free, MS-RL licence). It is installed next to the project
    as a local dotnet tool; nothing is installed system-wide.

.EXAMPLE
    .\installer\build-msi.ps1
    .\installer\build-msi.ps1 -SkipBuild      # reuse the last Release build
#>
param(
    [string]$Version = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$Installer = $PSScriptRoot
$Root = Split-Path $Installer -Parent
$WixExtVersion = "5.0.2"

$cpu = $env:PROCESSOR_ARCHITEW6432
if (-not $cpu) { $cpu = $env:PROCESSOR_ARCHITECTURE }
switch ($cpu) {
    "ARM64" { $rid = "win-arm64"; $arch = "arm64" }
    "AMD64" { $rid = "win-x64";   $arch = "x64" }
    default { throw "Unsupported processor architecture: $cpu" }
}

$project = Join-Path $Root "src\OpenBird.App"
if (-not $Version) {
    $csproj = [xml](Get-Content (Join-Path $project "OpenBird.App.csproj"))
    $Version = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Version '$Version' must look like 3.1.0" }

if (Get-Process -Name "OpenBird" -ErrorAction SilentlyContinue) {
    throw "OpenBird is running. Exit it from its notification-area icon first, so no recording is cut off, then run this again."
}

if (-not $SkipBuild) {
    Write-Host "Building OpenBird $Version for $rid ..." -ForegroundColor Cyan
    & dotnet build $project -c Release -r $rid --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "Build failed. See the output above." }
}

# The build output is packed as it is ("dotnet publish" leaves out OpenBird.pri and App.xbf for this kind of app).
$built = Join-Path $project "bin\Release\net10.0-windows10.0.19041.0\$rid"
foreach ($required in @("OpenBird.exe", "OpenBird.pri", "App.xbf", "Assets\openbird.ico", "Assets\openbird-recording.ico")) {
    if (-not (Test-Path (Join-Path $built $required))) { throw "Build output is incomplete: $required is missing in $built" }
}

Write-Host "Preparing the packaging tool (WiX $WixExtVersion) ..." -ForegroundColor Cyan
Push-Location $Root
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "Could not restore the WiX tool. Check your internet connection." }
    $have = & dotnet wix extension list -g 2>&1 | Out-String
    foreach ($ext in @("WixToolset.UI.wixext", "WixToolset.Util.wixext")) {
        if ($have -notmatch [regex]::Escape("$ext $WixExtVersion")) {
            & dotnet wix extension add -g "$ext/$WixExtVersion"
            if ($LASTEXITCODE -ne 0) { throw "Could not add the WiX extension $ext." }
        }
    }

    $dist = Join-Path $Root "dist"
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $msi = Join-Path $dist "OpenBird-$Version-$arch.msi"

    Write-Host "Building $msi ..." -ForegroundColor Cyan
    & dotnet wix build (Join-Path $Installer "OpenBird.wxs") `
        -arch $arch `
        -d "Version=$Version" -d "PayloadDir=$built" -d "BuildArch=$arch" -d "InstallerDir=$Installer" `
        -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext `
        -o $msi
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed. See the output above." }
}
finally { Pop-Location }

$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $msi ($size MB)" -ForegroundColor Green
Write-Host "Double-click it to install. If you installed with install.ps1 before, delete the old folder first:"
Write-Host "  Remove-Item `"$env:LOCALAPPDATA\Programs\OpenBird`" -Recurse -Force"
