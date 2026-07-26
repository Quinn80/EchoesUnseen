# ─────────────────────────────────────────────────────────────────────────────
# Echoes Unseen — beta publish script
#
# Produces ONE self-contained EchoesUnseen.exe (no .NET install needed on the
# tester's PC) and copies it to your Downloads folder, ready to drop into
# Dropbox for the website.
#
# Piper (the natural voice) is NOT bundled — the app downloads it automatically
# the first time a tester runs it, so the exe stays small-ish. Until Piper
# finishes downloading, the app talks with the built-in Windows voice.
#
# Usage:  right-click → Run with PowerShell,  or from a terminal:
#   powershell -ExecutionPolicy Bypass -File publish-beta.ps1
#   powershell -ExecutionPolicy Bypass -File publish-beta.ps1 -Version "1.2"
# ─────────────────────────────────────────────────────────────────────────────

param(
    [string]$Version = "1.7"
)

$ErrorActionPreference = 'Stop'
$root      = $PSScriptRoot
$project   = Join-Path $root 'EchoesUnseen\EchoesUnseen.csproj'
$publishDir = Join-Path $root 'EchoesUnseen\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
$downloads = Join-Path $env:USERPROFILE 'Downloads'
$outName   = "Echoes Unseen Beta $Version.exe"

Write-Host "Building the beta executable — this can take a couple of minutes..." -ForegroundColor Cyan

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false

$builtExe = Join-Path $publishDir 'EchoesUnseen.exe'
if (-not (Test-Path $builtExe)) {
    Write-Host "ERROR: expected exe not found at $builtExe" -ForegroundColor Red
    exit 1
}

$dest = Join-Path $downloads $outName
Copy-Item $builtExe $dest -Force

$sizeMb = [math]::Round((Get-Item $dest).Length / 1MB, 1)
Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Beta build: $dest  ($sizeMb MB)"
Write-Host "  Move this file into your Dropbox for the website."
