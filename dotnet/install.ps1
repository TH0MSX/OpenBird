<#
.SYNOPSIS
    Builds OpenBird in Release mode and installs it for the current user.

.DESCRIPTION
    Builds a self-contained copy (no .NET or Windows App SDK install needed to run it)
    to %LOCALAPPDATA%\Programs\OpenBird and adds a Start menu shortcut.
    No administrator rights are needed. Your settings, notes and recordings are not touched.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Target "D:\Apps\OpenBird"
#>
param(
    [string]$Target = (Join-Path $env:LOCALAPPDATA "Programs\OpenBird")
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot

if (Get-Process -Name "OpenBird" -ErrorAction SilentlyContinue) {
    throw "OpenBird is running. Exit it from its notification-area icon first, so no recording is cut off, then run this again."
}

# Works in Windows PowerShell 5.1 and PowerShell 7. PROCESSOR_ARCHITEW6432 is set when this
# shell itself runs emulated, and then names the real processor.
$cpu = $env:PROCESSOR_ARCHITEW6432
if (-not $cpu) { $cpu = $env:PROCESSOR_ARCHITECTURE }
switch ($cpu) {
    "ARM64" { $rid = "win-arm64" }
    "AMD64" { $rid = "win-x64" }
    default { throw "Unsupported processor architecture: $cpu" }
}

Write-Host "Building OpenBird for $rid ..." -ForegroundColor Cyan
$project = Join-Path $Root "src\OpenBird.App"
& dotnet build $project -c Release -r $rid --self-contained true
if ($LASTEXITCODE -ne 0) { throw "Build failed. See the output above." }

# The build output is copied as it is. "dotnet publish" leaves out the app's compiled
# resources (OpenBird.pri, App.xbf) for this kind of app, and it then cannot start.
$built = Join-Path $project "bin\Release\net10.0-windows10.0.19041.0\$rid"
foreach ($required in @("OpenBird.exe", "OpenBird.pri", "App.xbf")) {
    if (-not (Test-Path (Join-Path $built $required))) { throw "Build output is incomplete: $required is missing in $built" }
}

Write-Host "Copying to $Target ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $Target -Force | Out-Null
Copy-Item -Path (Join-Path $built "*") -Destination $Target -Recurse -Force

$exe = Join-Path $Target "OpenBird.exe"
if (-not (Test-Path $exe)) { throw "Copy finished but $exe is missing." }

$startMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "OpenBird.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($startMenu)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $Target
$shortcut.Description = "OpenBird - local meeting recorder and screen memory"
$shortcut.Save()

Write-Host ""
Write-Host "Installed to $Target" -ForegroundColor Green
Write-Host "Start it from the Start menu: OpenBird"
Write-Host "To remove it: exit OpenBird, turn off 'Start when I sign in' in its Settings, then delete that folder and the Start menu shortcut."
