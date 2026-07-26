# Echoes Unseen - Piper Voice Engine Installer
# Downloads piper.exe and the Lessac voice model.
#
# How to run:
#   Right-click this file and choose "Run with PowerShell"
#
# If Windows blocks scripts, open admin PowerShell once and run:
#   Set-ExecutionPolicy -Scope CurrentUser RemoteSigned

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "Echoes Unseen - Voice Engine Installer" -ForegroundColor Magenta
Write-Host "----------------------------------------"
Write-Host ""

# Locate the project folder relative to this script
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$piperDir = Join-Path $scriptDir "EchoesUnseen\Resources\Piper"
$voicesDir = Join-Path $piperDir "voices"

Write-Host "Target folder: $piperDir"
New-Item -ItemType Directory -Force -Path $piperDir | Out-Null
New-Item -ItemType Directory -Force -Path $voicesDir | Out-Null

# Piper binary
$piperExe = Join-Path $piperDir "piper.exe"
$piperZipUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip"
$piperZipPath = Join-Path $env:TEMP "piper_windows_amd64.zip"

if (Test-Path $piperExe) {
    Write-Host "OK: piper.exe already present. Skipping." -ForegroundColor Green
}
else {
    Write-Host ""
    Write-Host "Step 1 of 2: Downloading Piper TTS engine (~25 MB)..." -ForegroundColor Cyan

    try {
        Invoke-WebRequest -Uri $piperZipUrl -OutFile $piperZipPath -UseBasicParsing
    }
    catch {
        Write-Host "ERROR: Download failed." -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        Write-Host ""
        Write-Host "Manual fallback:"
        Write-Host "  1. Go to https://github.com/rhasspy/piper/releases"
        Write-Host "  2. Download piper_windows_amd64.zip"
        Write-Host "  3. Extract its contents into:"
        Write-Host "     $piperDir"
        Read-Host "Press Enter to exit"
        exit 1
    }

    Write-Host "      Extracting..."
    $extractDir = Join-Path $env:TEMP "piper_extract"
    if (Test-Path $extractDir) {
        Remove-Item -Recurse -Force $extractDir
    }
    Expand-Archive -Path $piperZipPath -DestinationPath $extractDir -Force

    $innerDir = Join-Path $extractDir "piper"
    if (Test-Path $innerDir) {
        Get-ChildItem -Path $innerDir | Copy-Item -Destination $piperDir -Recurse -Force
    }
    else {
        Get-ChildItem -Path $extractDir | Copy-Item -Destination $piperDir -Recurse -Force
    }

    Remove-Item $piperZipPath -Force -ErrorAction SilentlyContinue
    Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue

    if (Test-Path $piperExe) {
        Write-Host "      OK: Piper installed." -ForegroundColor Green
    }
    else {
        Write-Host "ERROR: piper.exe not found after extraction." -ForegroundColor Red
        Read-Host "Press Enter to exit"
        exit 1
    }
}

# Voice model
$voiceOnnx = Join-Path $voicesDir "en_US-lessac-high.onnx"
$voiceJson = Join-Path $voicesDir "en_US-lessac-high.onnx.json"

$voiceOnnxUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/high/en_US-lessac-high.onnx"
$voiceJsonUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/high/en_US-lessac-high.onnx.json"

if ((Test-Path $voiceOnnx) -and (Test-Path $voiceJson)) {
    Write-Host "OK: Lessac voice already present. Skipping." -ForegroundColor Green
}
else {
    Write-Host ""
    Write-Host "Step 2 of 2: Downloading Lessac voice model (~60 MB)..." -ForegroundColor Cyan

    try {
        if (-not (Test-Path $voiceOnnx)) {
            Invoke-WebRequest -Uri $voiceOnnxUrl -OutFile $voiceOnnx -UseBasicParsing
        }
        if (-not (Test-Path $voiceJson)) {
            Invoke-WebRequest -Uri $voiceJsonUrl -OutFile $voiceJson -UseBasicParsing
        }
        Write-Host "      OK: Voice model installed." -ForegroundColor Green
    }
    catch {
        Write-Host "ERROR: Voice download failed." -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        Read-Host "Press Enter to exit"
        exit 1
    }
}

# Done
Write-Host ""
Write-Host "----------------------------------------"
Write-Host "Setup complete!" -ForegroundColor Green
Write-Host "----------------------------------------"
Write-Host ""
Write-Host "Next: Open EchoesUnseen.sln in Visual Studio and press F5."
Write-Host ""
Read-Host "Press Enter to close this window"
