# Produces a self-contained Windows build that needs NO .NET install to run.
# Output folder: <repo>\publish\  (run MarathonAudio.App.exe inside it; zip the folder to share)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\App\MarathonAudio.App.csproj"
$out  = Join-Path $root "publish"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Publishing self-contained build (win-x64)..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Belt-and-suspenders: ensure the bundled decoder is present alongside the exe.
$toolsSrc = Join-Path $root "tools\vgmstream"
$toolsDst = Join-Path $out  "tools\vgmstream"
if (-not (Test-Path (Join-Path $toolsDst "vgmstream-cli.exe"))) {
    New-Item -ItemType Directory -Force -Path $toolsDst | Out-Null
    Copy-Item (Join-Path $toolsSrc "*") $toolsDst -Recurse -Force
}

Write-Host ""
Write-Host "Done. Run: $out\MarathonAudio.App.exe" -ForegroundColor Green
Write-Host "(Uses oo2core_9_win64.dll from your Marathon install; no .NET install needed.)"
