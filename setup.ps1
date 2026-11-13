<#
.SYNOPSIS
    OpenBird setup: Python environment, dependencies, speech model, desktop shortcut.

.PARAMETER Python
    Optional path to an existing python.exe that already has the dependencies
    (for example an older virtual environment). When given, no new environment
    is created and nothing is installed into it.

.EXAMPLE
    .\setup.ps1
    .\setup.ps1 -Python "D:\envs\openbird\Scripts\python.exe"
#>
param(
    [string]$Python = "",
    [switch]$NoShortcut
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot

Write-Host "OpenBird setup" -ForegroundColor Cyan

# 1. Python
if ($Python) {
    if (-not (Test-Path $Python)) { throw "Python not found: $Python" }
    $PythonExe = (Resolve-Path $Python).Path
    Write-Host "[1/4] Using existing Python: $PythonExe"
} else {
    $VenvPython = Join-Path $Root ".venv\Scripts\python.exe"
    if (-not (Test-Path $VenvPython)) {
        Write-Host "[1/4] Creating virtual environment in .venv ..."
        if (Get-Command py -ErrorAction SilentlyContinue) {
            & py -3 -m venv (Join-Path $Root ".venv")
        } else {
            & python -m venv (Join-Path $Root ".venv")
        }
        if ($LASTEXITCODE -ne 0) { throw "Could not create the virtual environment. Is Python 3.10+ installed?" }
    } else {
        Write-Host "[1/4] Virtual environment already exists."
    }
    $PythonExe = $VenvPython

    Write-Host "[2/4] Installing dependencies ..."
    & $PythonExe -m pip install --upgrade pip
    & $PythonExe -m pip install -r (Join-Path $Root "requirements.txt")
    if ($LASTEXITCODE -ne 0) { throw "Dependency install failed. See the pip output above." }
}

$PythonW = Join-Path (Split-Path $PythonExe -Parent) "pythonw.exe"
if (-not (Test-Path $PythonW)) { $PythonW = $PythonExe }
# Start-OpenBird.bat reads this file to find the interpreter.
Set-Content -Path (Join-Path $Root ".openbird-python") -Value $PythonW -Encoding ASCII

# 3. Settings file and speech model
Write-Host "[3/4] Creating settings and checking the speech model ..."
& $PythonExe (Join-Path $Root "main.py") setup
if ($LASTEXITCODE -ne 0) { throw "Model setup failed. Re-run setup.ps1 when you are online." }

# 4. Desktop shortcut
if (-not $NoShortcut) {
    Write-Host "[4/4] Creating desktop shortcut ..."
    $Shell = New-Object -ComObject WScript.Shell
    $Lnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "OpenBird.lnk"
    $Shortcut = $Shell.CreateShortcut($Lnk)
    $Shortcut.TargetPath = $PythonW
    $Shortcut.Arguments = '"' + (Join-Path $Root "main.py") + '" gui'
    $Shortcut.WorkingDirectory = $Root
    $Icon = Join-Path $Root "assets\openbird_fluent.ico"
    if (Test-Path $Icon) { $Shortcut.IconLocation = "$Icon,0" }
    $Shortcut.Description = "OpenBird - local meeting recorder and screen memory"
    $Shortcut.Save()
}

Write-Host ""
& $PythonExe (Join-Path $Root "main.py") paths
Write-Host ""
Write-Host "Done. Start OpenBird from the desktop shortcut or Start-OpenBird.bat." -ForegroundColor Green
