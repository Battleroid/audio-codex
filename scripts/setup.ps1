# Bootstraps a fresh Windows machine to build/run Marathon Audio Browser.
# Installs the .NET 8 SDK if missing, fetches vgmstream if missing, then publishes.
#
# NOTE: This tool is C#/.NET. It does NOT use Rust — no Rust toolchain is required.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Have-DotNet8 {
    try {
        $sdks = & dotnet --list-sdks 2>$null
        foreach ($l in $sdks) { if ($l -match '^(\d+)\.') { if ([int]$Matches[1] -ge 8) { return $true } } }
    } catch {}
    return $false
}

# 1) .NET 8 SDK
if (Have-DotNet8) {
    Write-Host "[ok] .NET 8+ SDK present." -ForegroundColor Green
} else {
    Write-Host "[..] Installing .NET 8 SDK via winget..." -ForegroundColor Cyan
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "winget not found. Install the .NET 8 SDK manually: https://dotnet.microsoft.com/download/dotnet/8.0"
    }
    winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements --disable-interactivity
    if (-not (Have-DotNet8)) { Write-Host "Open a new terminal so PATH refreshes, then re-run setup.ps1." -ForegroundColor Yellow; exit 1 }
}

# 2) vgmstream
$vgmExe = Join-Path $root "tools\vgmstream\vgmstream-cli.exe"
if (Test-Path $vgmExe) {
    Write-Host "[ok] vgmstream present." -ForegroundColor Green
} else {
    Write-Host "[..] Downloading vgmstream (win64)..." -ForegroundColor Cyan
    $zip = Join-Path $env:TEMP "vgmstream-win64.zip"
    $dst = Join-Path $root "tools\vgmstream"
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Invoke-WebRequest -Uri "https://github.com/vgmstream/vgmstream/releases/latest/download/vgmstream-win64.zip" -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $dst -Force
    Remove-Item $zip -Force
    Write-Host "[ok] vgmstream installed." -ForegroundColor Green
}

# 3) Build a self-contained release
Write-Host "[..] Building..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "publish.ps1")

Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
