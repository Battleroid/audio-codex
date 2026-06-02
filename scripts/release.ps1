# Build a self-contained release and (optionally) publish it as a GitHub release.
#
#   scripts\release.ps1 -Version v0.2.0                 # build + zip only
#   scripts\release.ps1 -Version v0.2.0 -Publish        # build + zip + create GitHub release
#   scripts\release.ps1 -Version v0.2.0 -Publish -Draft # ...as a draft
#
# Notes can be supplied with -Notes "text" or -NotesFile path\to\notes.md
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes,
    [string]$NotesFile,
    [switch]$Publish,
    [switch]$Draft,
    [switch]$WithParakeet
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# 1) sanity: decoder + (optional) transcription assets
$vgm = Join-Path $root "tools\vgmstream\vgmstream-cli.exe"
if (-not (Test-Path $vgm)) { throw "Missing $vgm - run scripts\setup.ps1 first." }
$whisper = Join-Path $root "tools\whisper\whisper-cli.exe"
$model   = Join-Path $root "tools\whisper\ggml-base.en.bin"
if (-not (Test-Path $whisper) -or -not (Test-Path $model)) {
    Write-Host "WARNING: tools\whisper\ is incomplete - the release will ship with transcription disabled." -ForegroundColor Yellow
}

# 2) build the self-contained app
if ($WithParakeet) { & (Join-Path $PSScriptRoot "publish.ps1") -WithParakeet }
else               { & (Join-Path $PSScriptRoot "publish.ps1") }

# 3) zip it
$zip = Join-Path $root ("audio-codex-{0}-win-x64.zip" -f $Version)
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $root "publish\*") -DestinationPath $zip
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Packaged $zip ($mb MB)" -ForegroundColor Green

# 4) optionally cut the GitHub release
if ($Publish) {
    $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
    if (-not (Test-Path $gh)) { throw "gh CLI not found; install it or run without -Publish." }

    $args = @("release", "create", $Version, $zip, "--title", "Audio Codex $Version")
    if ($NotesFile) { $args += @("--notes-file", $NotesFile) }
    elseif ($Notes) { $args += @("--notes", $Notes) }
    else            { $args += "--generate-notes" }
    if ($Draft) { $args += "--draft" }

    & $gh @args
    Write-Host "Release $Version created." -ForegroundColor Green
} else {
    Write-Host "Built $zip. Re-run with -Publish to create the GitHub release." -ForegroundColor Cyan
}
