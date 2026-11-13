<#
.SYNOPSIS
    Publishes OpenBird: renames the repository, pushes the cleaned code, turns on the homepage,
    and creates the GitHub release with the MSI attached.

.DESCRIPTION
    Run this on your PC, from the repository root, after:
      1. you copied the release kit into the repository (README.md, LICENSE, NOTICE, docs\, scripts\ ...),
      2. you rebuilt the MSI:   cd dotnet ; .\installer\build-msi.ps1
      3. you signed in to GitHub:  gh auth login
    The script stops at the first problem. It asks before anything that is hard to undo
    (making the repository public, replacing history).

    What it checks first:
      - the MSI exists and its Manufacturer is "TH0MSX" (not a personal name)
      - LICENSE is the real PolyForm text (it runs fetch-license.ps1 if needed)
      - tracked files contain no personal names, local user paths or API-key patterns

.PARAMETER ResetHistory
    Replaces the repository history with one clean commit. Recommended when older commits
    contained personal paths (old config files). This rewrites the main branch on GitHub.

.PARAMETER DryRun
    Runs all checks and prints what would happen. Changes nothing.

.EXAMPLE
    .\scripts\publish-release.ps1 -DryRun
    .\scripts\publish-release.ps1 -ResetHistory
#>
param(
    [string]$Owner = "TH0MSX",
    [string]$OldName = "PepaBird",
    [string]$Name = "OpenBird",
    [string]$Version = "3.1.0",
    [string]$Msi = "",
    [switch]$ResetHistory,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$Repo = "$Owner/$Name"
$Tag = "v$Version"
$Root = Split-Path $PSScriptRoot -Parent
Set-Location $Root

function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Ask($q) { $a = Read-Host "$q (type YES to continue)"; if ($a -ne "YES") { throw "Stopped by you." } }
function Run($cmd) {
    Write-Host "   > $cmd" -ForegroundColor DarkGray
    if (-not $DryRun) { Invoke-Expression $cmd; if ($LASTEXITCODE -ne 0) { throw "Command failed: $cmd" } }
}

Step "Tools"
foreach ($t in @("git", "gh")) { if (-not (Get-Command $t -ErrorAction SilentlyContinue)) { throw "$t is not installed." } }
& gh auth status
if ($LASTEXITCODE -ne 0) { throw "Not signed in. Run: gh auth login" }

Step "MSI"
if (-not $Msi) {
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
    $Msi = Join-Path $Root "dotnet\dist\OpenBird-$Version-$arch.msi"
}
if (-not (Test-Path $Msi)) { throw "MSI not found: $Msi. Build it with: cd dotnet ; .\installer\build-msi.ps1" }
$Msi = (Resolve-Path $Msi).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @($Msi, 0))
$view = $db.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $db, @("SELECT Value FROM Property WHERE Property='Manufacturer'"))
$view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null
$rec = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
$maker = $rec.GetType().InvokeMember("StringData", "GetProperty", $null, $rec, @(1))
Write-Host "   Manufacturer inside the MSI: $maker"
if ($maker -ne $Owner) { throw "The MSI says Manufacturer='$maker'. Rebuild it after the change to Manufacturer=""$Owner"" in dotnet\installer\OpenBird.wxs." }
$sha = (Get-FileHash $Msi -Algorithm SHA256).Hash.ToLower()
$size = [math]::Round((Get-Item $Msi).Length / 1MB, 1)
Write-Host "   $(Split-Path $Msi -Leaf)  $size MB  sha256 $sha"

Step "Licence"
$lic = Get-Content (Join-Path $Root "LICENSE") -Raw
if ($lic -match "PLACEHOLDER") { & (Join-Path $PSScriptRoot "fetch-license.ps1") }

Step "Scan for personal information"
$patterns = @("tomasz", "peplin", "tomas\\", "C:\\Users\\", "icloud", "AIza[0-9A-Za-z_\-]{30,}", "gsk_[0-9A-Za-z]{20,}", "Littlebird")
$files = & git ls-files --cached --others --exclude-standard | Where-Object { $_ -notmatch '\.(png|ico|bmp|db|msi|pdf)$' -and $_ -notmatch '^scripts/publish-release\.ps1$' -and $_ -notmatch '^PRIVACY-AND-LEGAL\.md$' }
$hits = @()
foreach ($f in $files) {
    if (-not (Test-Path $f)) { continue }
    foreach ($p in $patterns) {
        $m = Select-String -Path $f -Pattern $p -CaseSensitive:$false | Select-Object -First 2
        foreach ($x in $m) { $hits += "$f`:$($x.LineNumber): $($x.Line.Trim())" }
    }
}
if ($hits.Count -gt 0) { $hits | Select-Object -First 25 | ForEach-Object { Write-Host "   $_" -ForegroundColor Yellow }; throw "Personal information found in $($hits.Count) place(s). Fix them and run again." }
Write-Host "   Nothing found in $($files.Count) files." -ForegroundColor Green

Step "History"
$old = & git log --all --oneline -S"tomas" 2>$null
$oldCount = @($old).Count
Write-Host "   Commits that ever contained 'tomas': $oldCount"
if ($oldCount -gt 0 -and -not $ResetHistory) {
    Write-Host "   Older commits contain personal paths. Public history keeps them." -ForegroundColor Yellow
    Write-Host "   Re-run with -ResetHistory to publish one clean commit instead." -ForegroundColor Yellow
    Ask "Publish with the old history anyway"
}

if ($DryRun) { Write-Host "`nDry run finished. Nothing was changed." -ForegroundColor Green; return }

Step "Rename the repository"
cmd /c "gh repo view $Owner/$Name >nul 2>nul"
if ($LASTEXITCODE -eq 0) { Write-Host "   $Repo already exists." }
else {
    Run "gh repo rename $Name --repo $Owner/$OldName --yes"
    Run "git remote set-url origin https://github.com/$Repo.git"
}

Step "Commit and push"
$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($ResetHistory) {
    Ask "This replaces the history of '$branch' on GitHub with a single commit"
    Run "git checkout --orphan clean-release"
    Run "git add -A"
    Run "git commit -m 'OpenBird 3.1.0'"
    Run "git branch -M main"
    Run "git push --force origin main"
} else {
    Run "git add -A"
    & git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) { Run "git commit -m 'Public release 3.1.0: docs, homepage, licence, privacy notes'" }
    Run "git push origin $branch"
}

Step "Repository details"
Run "gh repo edit $Repo --description 'Local-first meeting recorder and screen memory for Windows 11. Personal project, noncommercial licence.' --homepage https://$($Owner.ToLower()).github.io/$Name/"
Run "gh repo edit $Repo --add-topic windows --add-topic windows-11 --add-topic winui3 --add-topic dotnet --add-topic meeting-recorder --add-topic whisper --add-topic local-first --add-topic privacy --add-topic arm64"

Step "Make the repository public"
Ask "Make $Repo public now (anyone can read it)"
Run "gh repo edit $Repo --visibility public --accept-visibility-change-consequences"

Step "Homepage (GitHub Pages from /docs)"
cmd /c "gh api repos/$Repo/pages -X POST -f source[branch]=main -f source[path]=/docs >nul 2>nul"
if ($LASTEXITCODE -ne 0) { Write-Host "   Pages may already be on, or needs a manual step: Settings > Pages > Deploy from branch main /docs" -ForegroundColor Yellow }

Step "Release $Tag"
$notes = (Get-Content (Join-Path $Root "RELEASE_NOTES_v$Version.md") -Raw).Replace("{{SHA256}}", $sha).Replace("{{MSI}}", (Split-Path $Msi -Leaf)).Replace("{{SIZE}}", "$size MB")
$notesFile = Join-Path $env:TEMP "openbird-release-notes.md"
[IO.File]::WriteAllText($notesFile, $notes, (New-Object Text.UTF8Encoding($false)))
Run "gh release create $Tag `"$Msi`" --repo $Repo --title 'OpenBird $Version' --notes-file `"$notesFile`" --latest"

Write-Host "`nDone." -ForegroundColor Green
Write-Host "  Release:  https://github.com/$Repo/releases/tag/$Tag"
Write-Host "  Homepage: https://$($Owner.ToLower()).github.io/$Name/  (can take a few minutes to appear)"
Write-Host "  One manual step: Settings > General > Social preview > upload docs\social-preview.png"

