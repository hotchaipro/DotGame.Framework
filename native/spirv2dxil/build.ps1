#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds Mesa's spirv2dxil for the content pipeline's DXIL step.

.DESCRIPTION
    Clones Mesa at a pinned release, applies the patches in patches/, configures
    it with only spirv-to-dxil enabled (every driver, platform, LLVM and optional
    feature off) and copies the stripped tool to native/spirv2dxil/<rid>/.
    The binaries are checked in, so this is only needed when updating Mesa or
    the patches. The native-spirv2dxil GitHub workflow runs it on each build
    machine (win-x64, win-arm64, osx-arm64, linux-x64, linux-arm64).

    Each machine builds its own binary: no cross-compiling. Windows needs an
    MSVC developer environment for the machine's architecture (the workflow
    sets one up); macOS needs Xcode's command line tools; Linux needs gcc.
    Python 3.10 or later is needed for Mesa's generators and Meson; Meson, Ninja,
    Mako, PyYAML and packaging are installed into a private virtual environment
    under build/.

    The patches (both to be sent upstream):
      0001  dxil_internal.h includes <malloc.h>, which macOS doesn't have
      0002  Mesa builds only NIR's headers when spirv-to-dxil is the only part
            enabled, and spirv2dxil fails to link

.PARAMETER Python
    The Python 3.10+ interpreter to create the virtual environment with.
    Defaults to python3 (python on Windows).

.EXAMPLE
    ./native/spirv2dxil/build.ps1
#>

param(
    [string]$Python = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $true

# Mesa 26.2.6 (MIT): the release the DXIL from it was validated with
$MesaTag = 'mesa-26.2.6'
$MesaCommit = '8e87a503ebd91408c88596d1b9eeb5f4c0b2088d'

$Root = $PSScriptRoot
$BuildRoot = Join-Path $Root 'build'
$MesaDir = Join-Path $BuildRoot 'mesa'
$VenvDir = Join-Path $BuildRoot 'venv'

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$archName = if ($arch -eq 'Arm64') { 'arm64' } else { 'x64' }
if ($IsWindows) { $os = 'win' } elseif ($IsMacOS) { $os = 'osx' } else { $os = 'linux' }
$Rid = "$os-$archName"
if ($Rid -eq 'osx-x64') { throw 'Intel Macs are not a build machine: build on an Apple silicon Mac' }
$exeExt = if ($IsWindows) { '.exe' } else { '' }
Write-Host "spirv2dxil: building $MesaTag for $Rid"

New-Item -ItemType Directory -Force $BuildRoot | Out-Null

# Mesa, at the pinned commit, with the patches
if (-not (Test-Path (Join-Path $MesaDir '.git'))) {
    git clone --quiet --depth 1 --branch $MesaTag https://gitlab.freedesktop.org/mesa/mesa.git $MesaDir
}
$head = git -C $MesaDir rev-parse HEAD
if ($head -ne $MesaCommit) { throw "Mesa is at $head, expected $MesaCommit ($MesaTag)" }
git -C $MesaDir checkout --quiet -- .
foreach ($patch in Get-ChildItem (Join-Path $Root 'patches') -Filter '*.patch' | Sort-Object Name) {
    Write-Host "  applying $($patch.Name)"
    git -C $MesaDir apply --whitespace=nowarn $patch.FullName
}

# Meson and Mesa's generators, in a private virtual environment
if (-not $Python) { $Python = if ($IsWindows) { 'python' } else { 'python3' } }
$venvBin = Join-Path $VenvDir ($IsWindows ? 'Scripts' : 'bin')
if (-not (Test-Path (Join-Path $venvBin "python$exeExt"))) {
    & $Python -m venv $VenvDir
}
& (Join-Path $venvBin "python$exeExt") -m pip install --quiet --disable-pip-version-check meson ninja mako pyyaml packaging
$env:PATH = $venvBin + [System.IO.Path]::PathSeparator + $env:PATH

# Only spirv-to-dxil. Statically linked runtimes where the platform allows, so the
# tool runs with nothing installed: the static CRT on Windows, libgcc and
# libstdc++ on Linux (glibc stays dynamic, so build on the oldest supported
# distribution). macOS links only libSystem.
$options = @(
    '-Dbuildtype=release', '-Db_ndebug=true', '-Dspirv-to-dxil=true',
    '-Dgallium-drivers=', '-Dvulkan-drivers=', '-Dplatforms=', '-Dglx=disabled', '-Degl=disabled', '-Dgbm=disabled',
    '-Dopengl=false', '-Dgles1=disabled', '-Dgles2=disabled', '-Dshared-glapi=disabled', '-Dllvm=disabled',
    '-Dmicrosoft-clc=disabled', '-Dmesa-clc=auto', '-Dzstd=disabled', '-Dexpat=disabled', '-Dvalgrind=disabled',
    '-Dlibunwind=disabled', '-Dlmsensors=disabled', '-Dxmlconfig=disabled', '-Dvideo-codecs=', '-Dspirv-tools=disabled',
    '-Dandroid-libbacktrace=disabled', '-Dbuild-tests=false'
)
if ($IsWindows) {
    # MSVC writes source files' full paths (assertion messages) into the binary: trim the build
    # folder (Mesa's sources and the generated ones) from them, so it names no machine or repository
    $trim = "/d1trimfile:$((Resolve-Path $BuildRoot).Path)\"
    $options += @('-Db_vscrt=mt', "-Dc_args=$trim", "-Dcpp_args=$trim")
}
if ($IsLinux) { $options += @('-Dc_link_args=-static-libgcc', '-Dcpp_link_args=-static-libgcc -static-libstdc++') }
if ($IsMacOS) { $env:MACOSX_DEPLOYMENT_TARGET = '14.0' }

$buildDir = Join-Path $BuildRoot "out-$Rid"
if (Test-Path (Join-Path $buildDir 'build.ninja')) {
    meson setup --reconfigure $buildDir $MesaDir @options
} else {
    meson setup $buildDir $MesaDir @options
}
ninja -C $buildDir src/microsoft/spirv_to_dxil/spirv2dxil$exeExt

$built = Join-Path $buildDir "src/microsoft/spirv_to_dxil/spirv2dxil$exeExt"
$outDir = Join-Path $Root $Rid
New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir "spirv2dxil$exeExt"
Copy-Item $built $out -Force
if ($IsMacOS) { strip -x $out; codesign --force --sign - $out }
if ($IsLinux) { strip $out }

# The tool must start: with no file it asks for one (and exits 1)
$PSNativeCommandUseErrorActionPreference = $false
$reply = (& $out 2>&1) -join ' '
if ($reply -notmatch 'Specify a shader filename') { throw "spirv2dxil didn't start: $reply" }
Write-Host "spirv2dxil: $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"

# The smoke test's exit code would otherwise be the script's
exit 0
