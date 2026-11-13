<#
.SYNOPSIS
    Replaces LICENSE with the official PolyForm Noncommercial License 1.0.0 text.

.DESCRIPTION
    Downloads the licence from the official polyformproject/polyform-licenses repository (tag 1.0.0),
    checks that it looks complete, and writes it to LICENSE in the repository root.
    The text is not edited. The required copyright notice lives in the NOTICE file.

.EXAMPLE
    .\scripts\fetch-license.ps1
#>
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Root = Split-Path $PSScriptRoot -Parent
$url = "https://raw.githubusercontent.com/polyformproject/polyform-licenses/1.0.0/PolyForm-Noncommercial-1.0.0.md"

Write-Host "Downloading $url" -ForegroundColor Cyan
$text = (Invoke-WebRequest -Uri $url -UseBasicParsing).Content
if ($text -is [byte[]]) { $text = [Text.Encoding]::UTF8.GetString($text) }

$must = @("PolyForm Noncommercial License 1.0.0", "Acceptance", "Copyright License", "Distribution License",
          "Noncommercial Purposes", "Personal Uses", "Patent Defense", "No Liability", "Definitions")
foreach ($m in $must) {
    if ($text -notmatch [regex]::Escape($m)) { throw "The downloaded text does not look like the full licence (missing '$m'). LICENSE was not changed." }
}
if ($text.Length -lt 3500 -or $text.Length -gt 8000) { throw "The downloaded text has an unexpected size ($($text.Length) characters). LICENSE was not changed." }

[IO.File]::WriteAllText((Join-Path $Root "LICENSE"), $text, (New-Object Text.UTF8Encoding($false)))
Write-Host "LICENSE written ($($text.Length) characters)." -ForegroundColor Green
