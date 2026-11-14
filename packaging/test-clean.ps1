# test-clean.ps1 <feed> <version> [platform...]: the guide's steps, on a machine with only the .NET SDK,
# from a folder of packages (a feed beside nuget.org, as nuget.org alone will be once they're published).
#
#   pwsh packaging/test-clean.ps1 artifacts/packages 0.1.0-preview.1 webgl android
#
# The publish workflow runs it in Microsoft's .NET SDK container images, which have nothing else: no JDK,
# no Android SDK, no Python, no compilers. First docs/guide/getting-started.md as written: a console
# project, DotGame.Framework.Desktop added, the guide's code and a picture, built. Then the template's
# game, its desktop head built and published, and each platform named here after installing what the
# guide says it needs (for Android, its workload, then the Android SDK and a JDK).
# Each step's log is in the work folder (TEMP, or DOTGAME_CLEAN_WORK); any failed step fails the script.
param(
    [Parameter(Mandatory)][string]$Feed,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(ValueFromRemainingArguments)][string[]]$Platforms = @()
)
$ErrorActionPreference = 'Stop'
$Feed = (Resolve-Path $Feed).Path
$Repo = Split-Path $PSScriptRoot -Parent
$Work = $env:DOTGAME_CLEAN_WORK
if (-not $Work) { $Work = Join-Path ([IO.Path]::GetTempPath()) "dotgame-clean-$PID" }
New-Item -ItemType Directory -Force $Work | Out-Null
$script:Failed = 0

function Step([string]$Name, [scriptblock]$Command) {
    $log = Join-Path $Work "$Name.log"
    & $Command *> $log
    if ($LASTEXITCODE -eq 0) {
        Write-Host "PASS  $Name"
    } else {
        Write-Host "FAIL  $Name (exit $LASTEXITCODE)"
        Get-Content $log | Select-String -Pattern ': error |error :|^error' | Select-Object -Unique -First 10 | ForEach-Object { Write-Host "        $($_.Line.Trim())" }
        Get-Content $log -Tail 4 | ForEach-Object { Write-Host "        $_" }
        $script:Failed++
    }
}

Write-Host "dotnet $(dotnet --version), on $([Runtime.InteropServices.RuntimeInformation]::OSDescription)"
foreach ($tool in 'java', 'python3', 'python', 'slangc', 'cmake', 'cl') {
    if (Get-Command $tool -ErrorAction SilentlyContinue) { Write-Host "note: $tool is on PATH" }
}

# The feed beside nuget.org, for everything under the work folder
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="packages" value="$Feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content (Join-Path $Work 'nuget.config')

# ---- Getting started: the desktop, with the .NET SDK alone ----
$guide = Get-Content (Join-Path $Repo 'docs/guide/getting-started.md') -Raw
$code = [regex]::Matches($guide, '(?s)```csharp\r?\n(.*?)```') | ForEach-Object { $_.Groups[1].Value }
$mainGame = $code | Where-Object { $_ -match 'class MainGame' } | Select-Object -First 1
$program = $code | Where-Object { $_ -match 'static class Program' } | Select-Object -First 1
if (-not $mainGame -or -not $program) { throw "getting-started.md: no MainGame or Program sample" }

Push-Location $Work
try {
    Step 'guide-new' { dotnet new console -n MyGame }
    Set-Location MyGame
    Step 'guide-add-package' { dotnet add package DotGame.Framework.Desktop --prerelease }
    $project = Get-Content MyGame.csproj -Raw
    if ($project -notmatch "DotGame.Framework.Desktop`" Version=`"$([regex]::Escape($Version))`"") {
        Write-Host "FAIL  guide-add-package: not version $Version"; $script:Failed++
    }
    $project.Replace('<OutputType>Exe</OutputType>', '<OutputType>WinExe</OutputType>') | Set-Content MyGame.csproj
    New-Item -ItemType Directory -Force Content/Textures | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $templates = [IO.Compression.ZipFile]::OpenRead((Join-Path $Feed "DotGame.Templates.$Version.nupkg"))
    try {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($templates.GetEntry('content/dotgame/Content/Textures/ball.png'), (Join-Path (Get-Location) 'Content/Textures/ball.png'), $true)
    } finally { $templates.Dispose() }
    Set-Content MainGame.cs $mainGame
    Set-Content Program.cs $program
    Step 'guide-build' { dotnet build }
    if (-not (Get-ChildItem bin -Recurse -Filter 'ball.*' | Where-Object { $_.FullName -match 'Content[\\/]Textures' })) {
        Write-Host "FAIL  guide-build: no built ball texture"; $script:Failed++
    }

    # ---- The template: every platform ----
    Set-Location $Work
    Step 'template-install' { dotnet new install "DotGame.Templates::$Version" --add-source $Feed }
    Step 'template-new' { dotnet new dotgame -n BallGame }
    Set-Location BallGame
    Step 'template-desktop' { dotnet build BallGame.Desktop }
    # Published as the guide says, for this machine: every file the pipeline built is beside the app
    $published = Join-Path $Work 'desktop'
    Step 'template-desktop-publish' { dotnet publish BallGame.Desktop -c Release --use-current-runtime -o $published }
    $built = (Resolve-Path 'BallGame.Desktop/obj/ContentPipeline/Output' -ErrorAction SilentlyContinue).Path
    $missing = @(if ($built) {
        Get-ChildItem $built -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($built, $_.FullName) } |
            Where-Object { -not (Test-Path (Join-Path $published $_)) }
    })
    if (-not $built -or $missing.Count) {
        Write-Host "FAIL  template-desktop-content: not published: $($missing -join ', ')"; $script:Failed++
    } else { Write-Host 'PASS  template-desktop-content' }
    foreach ($platform in $Platforms) {
        switch ($platform) {
            'webgl' {
                Step 'workload-wasm-tools' { dotnet workload install wasm-tools }
                Step 'template-webgl' { dotnet build BallGame.WebGL }
                Step 'template-webgl-publish' { dotnet publish BallGame.WebGL -o (Join-Path $Work 'web') }
            }
            'android' {
                Step 'workload-android' { dotnet workload install android }
                # As platforms.md says: the SDK and a JDK into folders of the work folder, then found
                # through ANDROID_HOME and JAVA_HOME. The installer needs USER, which containers don't set.
                if (-not $env:USER -and -not $env:USERNAME) { $env:USER = 'dotgame' }
                $sdk = Join-Path $Work 'android-sdk'; $jdk = Join-Path $Work 'jdk'
                Step 'android-dependencies' { dotnet build BallGame.Android -t:InstallAndroidDependencies "-p:AndroidSdkDirectory=$sdk" "-p:JavaSdkDirectory=$jdk" -p:AcceptAndroidSDKLicenses=True -p:AndroidManifestType=GoogleV2 }
                $env:ANDROID_HOME = $sdk; $env:JAVA_HOME = $jdk
                Step 'template-android' { dotnet build BallGame.Android }
            }
            'ios' {
                Step 'workload-ios' { dotnet workload install ios }
                Step 'template-ios' { dotnet build BallGame.iOS -p:RuntimeIdentifier=iossimulator-arm64 }
            }
            default { throw "unknown platform: $platform" }
        }
    }
} finally { Pop-Location }

Write-Host "logs: $Work"
if ($script:Failed) { Write-Host "$($script:Failed) failed"; exit 1 }
Write-Host 'all passed'
