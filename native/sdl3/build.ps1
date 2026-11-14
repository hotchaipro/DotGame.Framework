#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds native SDL3 library from the git submodule.

.DESCRIPTION
    Builds SDL3 for the specified platform using CMake.
    Source code is read from the git submodule under native/sdl3/src/SDL/.
    Built binaries are copied to the native/sdl3/<rid>/ output directory.

.PARAMETER Platform
    Target platform to build for. If not specified, builds for the current
    host platform. Valid values:
        win-x64, win-arm64,
        linux-x64, linux-arm64,
        osx-x64, osx-arm64,
        android-arm64-v8a, android-x86_64,
        ios-arm64

.PARAMETER AndroidNdkRoot
    Path to the Android NDK root. Required for Android builds.
    Defaults to the ANDROID_NDK_ROOT environment variable.

.EXAMPLE
    ./native/sdl3/build.ps1
    Builds for the current host platform.

.EXAMPLE
    ./native/sdl3/build.ps1 -Platform android-arm64-v8a
    Cross-compiles for Android arm64.

.EXAMPLE
    ./native/sdl3/build.ps1 -Platform ios-arm64
    Cross-compiles for iOS arm64 (requires macOS with Xcode).
#>
param(
    [Parameter()]
    [ValidateSet(
        'win-x64', 'win-arm64',
        'linux-x64', 'linux-arm64',
        'osx-x64', 'osx-arm64',
        'android-arm64-v8a', 'android-x86_64',
        'ios-arm64')]
    [string]$Platform,

    [Parameter()]
    [string]$AndroidNdkRoot = $env:ANDROID_NDK_ROOT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve paths relative to this script
$ScriptDir = $PSScriptRoot
$SdlSrcDir = Join-Path $ScriptDir 'src/SDL'
$BuildBaseDir = Join-Path $ScriptDir 'build'

# Validate submodule exists
if (-not (Test-Path (Join-Path $SdlSrcDir 'CMakeLists.txt'))) {
    Write-Error "SDL source not found at '$SdlSrcDir'. Run 'git submodule update --init --recursive' first."
}

# Auto-detect host platform if not specified
if (-not $Platform) {
    if ($IsWindows -or ($env:OS -eq 'Windows_NT')) {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
        $Platform = if ($arch -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
    }
    elseif ($IsLinux) {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
        $Platform = if ($arch -eq 'Arm64') { 'linux-arm64' } else { 'linux-x64' }
    }
    elseif ($IsMacOS) {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
        $Platform = if ($arch -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' }
    }
    else {
        Write-Error "Could not detect host OS. Specify -Platform explicitly."
    }
    Write-Host "Auto-detected platform: $Platform"
}

$BuildDir = Join-Path $BuildBaseDir $Platform
$OutputDir = Join-Path $ScriptDir $Platform

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Building SDL3 native library"           -ForegroundColor Cyan
Write-Host "  Platform: $Platform"                    -ForegroundColor Cyan
Write-Host "  Output:   $OutputDir"                   -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Ensure output directory exists
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# Returns platform/toolchain-specific CMake arguments (architecture, NDK, etc.)
# These are passed on the cmake command line alongside the superbuild CMakeLists.txt.
function Get-PlatformArgs {
    param([string]$TargetPlatform)

    $args = @('-DCMAKE_BUILD_TYPE=Release')

    switch ($TargetPlatform) {
        'win-x64' {
            $args += @('-A', 'x64')
        }
        'win-arm64' {
            $args += @('-A', 'ARM64')
        }
        'linux-x64' {
            # Native build, no extra args needed
        }
        'linux-arm64' {
            $args += @(
                '-DCMAKE_SYSTEM_NAME=Linux'
                '-DCMAKE_SYSTEM_PROCESSOR=aarch64'
            )
        }
        'osx-x64' {
            $args += @('-DCMAKE_OSX_ARCHITECTURES=x86_64')
        }
        'osx-arm64' {
            $args += @('-DCMAKE_OSX_ARCHITECTURES=arm64')
        }
        'android-arm64-v8a' {
            if (-not $AndroidNdkRoot) { Write-Error "ANDROID_NDK_ROOT is required for Android builds." }
            $toolchain = Join-Path $AndroidNdkRoot 'build/cmake/android.toolchain.cmake'
            $args += @(
                "-DCMAKE_TOOLCHAIN_FILE=$toolchain"
                '-DANDROID_ABI=arm64-v8a'
                '-DANDROID_PLATFORM=android-24'
            )
        }
        'android-x86_64' {
            if (-not $AndroidNdkRoot) { Write-Error "ANDROID_NDK_ROOT is required for Android builds." }
            $toolchain = Join-Path $AndroidNdkRoot 'build/cmake/android.toolchain.cmake'
            $args += @(
                "-DCMAKE_TOOLCHAIN_FILE=$toolchain"
                '-DANDROID_ABI=x86_64'
                '-DANDROID_PLATFORM=android-24'
            )
        }
        'ios-arm64' {
            $args += @(
                '-DCMAKE_SYSTEM_NAME=iOS'
                '-DCMAKE_OSX_ARCHITECTURES=arm64'
                '-DCMAKE_OSX_DEPLOYMENT_TARGET=15.0'
            )
        }
    }

    return $args
}

# iOS uses static libraries; all other platforms use shared
$useStatic = $Platform -eq 'ios-arm64'

function Invoke-CMake {
    param([string[]]$Arguments)
    Write-Host "cmake $($Arguments -join ' ')" -ForegroundColor DarkGray
    & cmake @Arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Error "CMake failed with exit code $LASTEXITCODE"
    }
}

function Copy-NativeLib {
    param([string]$SearchDir, [string]$Pattern, [string]$DestDir)
    $files = Get-ChildItem -Path $SearchDir -Recurse -Filter $Pattern -File | Where-Object { $_.DirectoryName -notlike '*CMakeFiles*' }
    foreach ($f in $files) {
        Write-Host "  $($f.Name)" -ForegroundColor Green
        Copy-Item $f.FullName -Destination (Join-Path $DestDir $f.Name) -Force
    }
}

# --- Build SDL3 ---
Write-Host "--- Building SDL3 ---" -ForegroundColor Yellow

$sdlBuildDir = Join-Path $BuildDir 'SDL'

$sdlShared = if ($useStatic) { 'OFF' } else { 'ON' }
$sdlStatic = if ($useStatic) { 'ON' } else { 'OFF' }

# No build machine's paths in the binaries. SDL's release asserts and error
# messages name their source files (__FILE__), and debug info names the source
# and build folders, so both are written relative to the repository root:
# -ffile-prefix-map on Clang and GCC, /d1trimfile on MSVC (which keeps debug
# info in the PDB, not in the DLL). Android libraries keep their symbol table
# but not their debug info (-Wl,--strip-debug), which is most of their size.
# The flags go in CFLAGS, CXXFLAGS and LDFLAGS, which CMake adds to the
# toolchain's own flags when it configures. The NDK's (legacy) toolchain
# ignores those, so for Android they go on the command line instead, where it
# adds its own flags in front of them.
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $ScriptDir '../..'))
if ($Platform -like 'win-*') {
    $trimRoot = $RepoRoot.TrimEnd('\', '/') + '\'
    $compileFlags = "/d1trimfile:$trimRoot"
}
else {
    $compileFlags = "-ffile-prefix-map=$($RepoRoot.TrimEnd('/'))=."
}
$env:CFLAGS = $compileFlags
$env:CXXFLAGS = $compileFlags
$linkFlags = if ($Platform -like 'android-*') { '-Wl,--strip-debug' } else { '' }
$env:LDFLAGS = $linkFlags
$flagArgs = if ($Platform -like 'android-*') {
    @("-DCMAKE_C_FLAGS=$compileFlags", "-DCMAKE_CXX_FLAGS=$compileFlags", "-DCMAKE_SHARED_LINKER_FLAGS=$linkFlags")
} else { @() }

# The flags are read only when a build folder is first configured
if (Test-Path $BuildDir) { Remove-Item -Recurse -Force $BuildDir }

$platformArgs = Get-PlatformArgs -TargetPlatform $Platform
$cmakeArgs = @(
    '-S', $SdlSrcDir,
    '-B', $sdlBuildDir,
    "-DSDL_SHARED=$sdlShared",
    "-DSDL_STATIC=$sdlStatic",
    '-DSDL_TEST_LIBRARY=OFF',
    '-DSDL_TESTS=OFF',
    '-DSDL_INSTALL=OFF'
) + $platformArgs + $flagArgs
Invoke-CMake $cmakeArgs
Invoke-CMake @('--build', $sdlBuildDir, '--config', 'Release', '--parallel')

# --- Copy outputs ---
Write-Host "--- Copying outputs to $OutputDir ---" -ForegroundColor Yellow

switch -Wildcard ($Platform) {
    'win-*' {
        Copy-NativeLib -SearchDir $sdlBuildDir -Pattern 'SDL3.dll' -DestDir $OutputDir
    }
    'linux-*' {
        Copy-NativeLib -SearchDir $sdlBuildDir -Pattern 'libSDL3.so' -DestDir $OutputDir
    }
    'osx-*' {
        Copy-NativeLib -SearchDir $sdlBuildDir -Pattern 'libSDL3.dylib' -DestDir $OutputDir
    }
    'android-*' {
        Copy-NativeLib -SearchDir $sdlBuildDir -Pattern 'libSDL3.so' -DestDir $OutputDir
    }
    'ios-*' {
        Copy-NativeLib -SearchDir $sdlBuildDir -Pattern 'libSDL3.a' -DestDir $OutputDir
    }
}

Write-Host ""
Write-Host "Build complete for $Platform" -ForegroundColor Green
Write-Host "Output: $OutputDir" -ForegroundColor Green
Get-ChildItem $OutputDir -File | ForEach-Object { Write-Host "  $($_.Name) ($([math]::Round($_.Length / 1MB, 2)) MB)" }
