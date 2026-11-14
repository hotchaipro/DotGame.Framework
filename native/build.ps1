#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the framework's native libraries.

.DESCRIPTION
    Runs the build script of each native component under native/ (currently
    SDL3, native/sdl3/build.ps1). Built binaries are written to
    native/<component>/<rid>/ and are checked in, so this is only needed when
    updating a native dependency. The native-sdl3 GitHub workflow runs this
    script on each OS when the native sources change.

.PARAMETER Platform
    Target platform(s). Defaults to the current host platform. Valid values:
        win-x64, win-arm64,
        linux-x64, linux-arm64,
        osx-x64, osx-arm64,
        android-arm64-v8a, android-x86_64,
        ios-arm64

.PARAMETER AndroidNdkRoot
    Path to the Android NDK root. Required for Android builds.
    Defaults to the ANDROID_NDK_ROOT environment variable.

.EXAMPLE
    ./native/build.ps1
    Builds for the current host platform.

.EXAMPLE
    ./native/build.ps1 -Platform android-arm64-v8a, android-x86_64
#>
param(
    [Parameter()]
    [ValidateSet(
        'win-x64', 'win-arm64',
        'linux-x64', 'linux-arm64',
        'osx-x64', 'osx-arm64',
        'android-arm64-v8a', 'android-x86_64',
        'ios-arm64')]
    [string[]]$Platform,

    [Parameter()]
    [string]$AndroidNdkRoot = $env:ANDROID_NDK_ROOT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sdl3 = Join-Path $PSScriptRoot 'sdl3/build.ps1'

if (-not $Platform) {
    & $sdl3 -AndroidNdkRoot $AndroidNdkRoot
}
else {
    foreach ($rid in $Platform) {
        & $sdl3 -Platform $rid -AndroidNdkRoot $AndroidNdkRoot
    }
}
