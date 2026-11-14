#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Updates SDL3 submodules to the latest compatible release tags.

.DESCRIPTION
    Queries the git tags in each submodule and checks out the latest stable
    release that is semantically compatible with the SDL3-CS NuGet package.

    SDL3 compatibility model (from SDL docs/README-versions.md):
    - Stable releases have even minor AND even patch numbers (3.2.0, 3.4.2).
    - Newer patch versions within the same minor are backwards-compatible.
    - Newer minor versions are also backwards-compatible.
    - SDL3-CS 3.4.2 works with any native SDL3 >= 3.4.2 (stable).

    This script finds the latest stable release tag matching "release-3.*"
    where both minor and patch are even, and the version is >= the minimum
    compatible version.

.PARAMETER MinVersion
    Minimum compatible SDL version (default: 3.4.2, matching SDL3-CS NuGet).
    Only stable releases >= this version will be considered.

.PARAMETER DryRun
    Show what would be checked out without making changes.

.EXAMPLE
    ./update-submodules.ps1
    Updates both submodules to their latest compatible releases.

.EXAMPLE
    ./update-submodules.ps1 -DryRun
    Shows which tags would be selected without checking them out.
#>
param(
    [Parameter()]
    [string]$MinVersion = '3.4.2',

    [Parameter()]
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir = $PSScriptRoot
$SdlDir = Join-Path $ScriptDir 'src/SDL'

function Get-LatestCompatibleTag {
    param(
        [string]$RepoDir,
        [string]$MinVer
    )

    Push-Location $RepoDir
    try {
        # Fetch latest tags from remote
        git fetch --tags --quiet 2>&1 | Out-Null

        # Parse minimum version
        $minParts = $MinVer -split '\.'
        $minMajor = [int]$minParts[0]
        $minMinor = [int]$minParts[1]
        $minPatch = [int]$minParts[2]

        # Get all release tags, parse versions, filter to stable and compatible
        $tags = git tag -l 'release-3.*' |
            ForEach-Object {
                if ($_ -match '^release-(\d+)\.(\d+)\.(\d+)$') {
                    $major = [int]$Matches[1]
                    $minor = [int]$Matches[2]
                    $patch = [int]$Matches[3]
                    [PSCustomObject]@{
                        Tag   = $_
                        Major = $major
                        Minor = $minor
                        Patch = $patch
                    }
                }
            } |
            # Stable releases: even minor AND even patch
            Where-Object { ($_.Minor % 2 -eq 0) -and ($_.Patch % 2 -eq 0) } |
            # Must be >= minimum version
            Where-Object {
                ($_.Major -gt $minMajor) -or
                ($_.Major -eq $minMajor -and $_.Minor -gt $minMinor) -or
                ($_.Major -eq $minMajor -and $_.Minor -eq $minMinor -and $_.Patch -ge $minPatch)
            } |
            # Sort descending by version
            Sort-Object Major, Minor, Patch -Descending

        if ($tags) {
            return $tags[0].Tag
        }
        return $null
    }
    finally {
        Pop-Location
    }
}

function Update-Submodule {
    param(
        [string]$Name,
        [string]$RepoDir,
        [string]$MinVer
    )

    Write-Host "--- $Name ---" -ForegroundColor Yellow

    if (-not (Test-Path (Join-Path $RepoDir '.git'))) {
        Write-Error "$Name source not found at '$RepoDir'. Run 'git submodule update --init --recursive' first."
    }

    # Get current tag
    Push-Location $RepoDir
    $currentTag = git describe --tags --exact-match HEAD 2>$null
    if (-not $currentTag) {
        $currentTag = (git rev-parse --short HEAD)
    }
    Pop-Location

    $latestTag = Get-LatestCompatibleTag -RepoDir $RepoDir -MinVer $MinVer
    if (-not $latestTag) {
        Write-Warning "No compatible stable release found for $Name (>= $MinVer)"
        return
    }

    if ($currentTag -eq $latestTag) {
        Write-Host "  Already at latest: $currentTag" -ForegroundColor Green
        return
    }

    Write-Host "  Current: $currentTag"
    Write-Host "  Latest:  $latestTag" -ForegroundColor Cyan

    if ($DryRun) {
        Write-Host "  (dry run -- no changes made)" -ForegroundColor DarkGray
        return
    }

    Push-Location $RepoDir
    git checkout $latestTag --quiet
    Pop-Location

    Write-Host "  Updated to $latestTag" -ForegroundColor Green
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  SDL3 Submodule Update"                  -ForegroundColor Cyan
Write-Host "  Min compatible version: $MinVersion"    -ForegroundColor Cyan
if ($DryRun) {
    Write-Host "  Mode: DRY RUN"                      -ForegroundColor DarkGray
}
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Update-Submodule -Name 'SDL3' -RepoDir $SdlDir -MinVer $MinVersion

Write-Host ""
Write-Host "Submodule status:" -ForegroundColor Yellow
Push-Location (Join-Path $ScriptDir '../..')
git submodule status -- native/sdl3/src/SDL
Pop-Location

if (-not $DryRun) {
    Write-Host ""
    Write-Host "Remember to stage the updated submodule pointers:" -ForegroundColor Yellow
    Write-Host "  git add native/sdl3/src/SDL" -ForegroundColor DarkGray
}
