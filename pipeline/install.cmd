@echo off
call :install KhronosGroup.VulkanSDK 1.4.363.0 || exit /b 1

rem astcenc (Arm's ASTC encoder, for Android and iOS textures): a pinned, checksum-verified
rem release in the per-user tools folder, which is added to the user PATH
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop';" ^
  "$version = '5.7.0';" ^
  "if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { $asset = 'windows-arm64'; $sha = '5b18285fda1eeb884fff5d1846c5669bafd38552c55c6a08d5bd8f6b8589c081'; $exe = 'astcenc-neon.exe' } else { $asset = 'windows-x64'; $sha = 'cbde7c78adcc90d0d90ecf10f32d03f3340fbe1720cb6d6c892147d625e74d2d'; $exe = 'astcenc-avx2.exe' };" ^
  "$bin = Join-Path $env:LOCALAPPDATA 'DotGame\ContentPipelineTools\bin';" ^
  "if (Get-Command $exe -ErrorAction SilentlyContinue) { Write-Host \"${exe}: already installed\"; exit 0 };" ^
  "$zip = Join-Path $env:TEMP \"astcenc-$version.zip\";" ^
  "Invoke-WebRequest \"https://github.com/ARM-software/astc-encoder/releases/download/$version/astcenc-$version-$asset.zip\" -OutFile $zip;" ^
  "if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $sha) { throw \"checksum mismatch for $zip\" };" ^
  "$extract = Join-Path $env:TEMP \"astcenc-$version\";" ^
  "Expand-Archive $zip $extract -Force;" ^
  "New-Item -ItemType Directory -Force $bin | Out-Null;" ^
  "Copy-Item (Join-Path $extract 'bin\*') $bin -Force;" ^
  "$path = [Environment]::GetEnvironmentVariable('Path', 'User');" ^
  "if (($path -split ';') -notcontains $bin) { [Environment]::SetEnvironmentVariable('Path', ($path.TrimEnd(';') + ';' + $bin), 'User'); Write-Host \"Added $bin to the user PATH: open a new terminal\" };" ^
  "Write-Host \"${exe}: installed ($bin)\""
if errorlevel 1 exit /b 1

echo.
echo Open a new terminal for the tools to be on PATH. Then check them with:
echo   slangc -v ^& astcenc -version
exit /b 0

:install
winget install --id %1 --version %2 --exact --silent --accept-package-agreements --accept-source-agreements
rem winget's code for a package that's already installed
if errorlevel -1978335189 if not errorlevel -1978335188 exit /b 0
if errorlevel 1 (
  echo Failed to install %1 %2. 1>&2
  exit /b 1
)
exit /b 0
