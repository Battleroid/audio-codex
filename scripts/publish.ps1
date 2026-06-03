# Produces a self-contained Windows build that needs NO .NET install to run.
# Output folder: <repo>\publish\  (run MarathonAudio.App.exe inside it; zip the folder to share)
#
#   scripts\publish.ps1                  # normal build (~200 MB); Parakeet downloads on first enable
#   scripts\publish.ps1 -WithParakeet    # also bundle the GPU Parakeet runtime + int8 model (~2 GB extra)
param([switch]$WithParakeet)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\App\MarathonAudio.App.csproj"
$out  = Join-Path $root "publish"

# --- Parakeet GPU runtime + model (sherpa-onnx + CUDA 12.x libs onnxruntime needs + int8 model) ---
$SherpaUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.2/sherpa-onnx-v1.13.2-cuda-12.x-cudnn-9.x-win-x64-cuda.tar.bz2"
$ModelUrl  = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2"
$CudaZips  = @(
  "https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.6.4.1-archive.zip",
  "https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.6.77-archive.zip",
  "https://developer.download.nvidia.com/compute/cuda/redist/libcufft/windows-x86_64/libcufft-windows-x86_64-11.3.0.4-archive.zip",
  "https://developer.download.nvidia.com/compute/cuda/redist/libcurand/windows-x86_64/libcurand-windows-x86_64-10.3.7.77-archive.zip",
  "https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.8.0.87_cuda12-archive.zip"
)

function Ensure-Parakeet($pk) {
  $bin = Join-Path $pk "bin"; $model = Join-Path $pk "model"
  # Each CUDA redist zip paired with a marker DLL it must produce, so an interrupted bundle can
  # resume by fetching only the libs still missing instead of trusting cublas alone.
  $cudaMarkers = @{
    "cublasLt64_12.dll" = $CudaZips[0]; "cudart64_12.dll" = $CudaZips[1]; "cufft64_11.dll" = $CudaZips[2]
    "curand64_10.dll"   = $CudaZips[3]; "cudnn64_9.dll"   = $CudaZips[4]
  }
  $missingCuda = @($cudaMarkers.Keys | Where-Object { -not (Test-Path (Join-Path $bin $_)) })
  $haveBin   = (Test-Path (Join-Path $bin "sherpa-onnx-offline.exe")) -and ($missingCuda.Count -eq 0)
  $haveModel = Test-Path (Join-Path $model "encoder.int8.onnx")
  if ($haveBin -and $haveModel) { Write-Host "Parakeet runtime + model already present (reusing)." -ForegroundColor Green; return }

  New-Item -ItemType Directory -Force -Path $bin, $model | Out-Null
  $tmp = Join-Path $env:TEMP ("pk_" + [guid]::NewGuid().ToString("N"))
  New-Item -ItemType Directory -Force -Path $tmp | Out-Null
  try {
    if (-not (Test-Path (Join-Path $bin "sherpa-onnx-offline.exe"))) {
      Write-Host "Downloading sherpa-onnx runtime..." -ForegroundColor Cyan
      $a = Join-Path $tmp "sherpa.tar.bz2"; curl.exe -sL -o $a $SherpaUrl
      tar -xf $a -C $tmp
      $ex = Get-ChildItem $tmp -Directory -Filter "sherpa-onnx-v*" | Select-Object -First 1
      Copy-Item (Join-Path $ex.FullName "bin\*") $bin -Force; Remove-Item $a -Force
    }
    # Re-check after the sherpa copy (it may already ship some CUDA libs), then fetch only what's missing.
    $missingCuda = @($cudaMarkers.Keys | Where-Object { -not (Test-Path (Join-Path $bin $_)) })
    foreach ($marker in $missingCuda) {
      $u = $cudaMarkers[$marker]
      Write-Host "Downloading $(Split-Path $u -Leaf) (for $marker)..." -ForegroundColor Cyan
      $z = Join-Path $tmp "lib.zip"; curl.exe -sL -o $z $u
      $ez = Join-Path $tmp "ez"; if (Test-Path $ez) { Remove-Item $ez -Recurse -Force }
      Expand-Archive $z -DestinationPath $ez -Force
      Get-ChildItem $ez -Recurse -Filter *.dll | Where-Object { $_.FullName -match '\\bin\\' } |
        ForEach-Object { Copy-Item $_.FullName $bin -Force }
      Remove-Item $z, $ez -Recurse -Force
    }
    if (-not (Test-Path (Join-Path $model "encoder.int8.onnx"))) {
      Write-Host "Downloading Parakeet int8 model..." -ForegroundColor Cyan
      $a = Join-Path $tmp "model.tar.bz2"; curl.exe -sL -o $a $ModelUrl
      tar -xf $a -C $tmp
      $ex = Get-ChildItem $tmp -Directory -Filter "sherpa-onnx-nemo-parakeet*" | Select-Object -First 1
      Copy-Item (Join-Path $ex.FullName "*.onnx") $model -Force
      Copy-Item (Join-Path $ex.FullName "tokens.txt") $model -Force
      Remove-Item $a -Force
    }
  } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

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

if ($WithParakeet) {
    $pkSrc = Join-Path $root "tools\parakeet"
    Ensure-Parakeet $pkSrc
    Write-Host "Bundling Parakeet runtime + model into the release..." -ForegroundColor Cyan
    $pkDst = Join-Path $out "tools\parakeet"
    New-Item -ItemType Directory -Force -Path $pkDst | Out-Null
    Copy-Item (Join-Path $pkSrc "*") $pkDst -Recurse -Force
}

Write-Host ""
Write-Host "Done. Run: $out\MarathonAudio.App.exe" -ForegroundColor Green
Write-Host "(Uses oo2core_9_win64.dll from your Marathon install; no .NET install needed.)"
if ($WithParakeet) { Write-Host "GPU Parakeet bundled (no in-app download needed)." -ForegroundColor Green }
