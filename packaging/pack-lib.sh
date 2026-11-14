# pack-lib.sh: what the framework's packaging/pack.sh shares with the pack.sh of a library built on the
# framework. Sourced, with REPO (the repository being packed) and FRAMEWORK (the framework's root, which
# is REPO for the framework) set.
#
# Sets P (the framework's packaging folder), REPOSITORY_XML (the repository's nuspec elements, from
# packaging/repository.sh), VERSION, OUT (where the packages go: OUT, or artifacts/packages), STAGE,
# TOOLS (the pinned tools for this machine, on PATH), IMAGING_VERSION and IMAGING_NOTICES,
# AUDIOCODECS_VERSION and AUDIOCODECS_NOTICES, and defines build, put, pack, dep, pin and tfm_of. PACKED lists the packages pack has written.
#
# Every assembly is built deterministically with no Source Link and its paths mapped to /_/, so no
# package names the machine or the folder it was built in.
P=$FRAMEWORK/packaging
. "$P/repository.sh"
REPOSITORY_XML=$(repository_xml "$REPO")
VERSION=${VERSION:-0.1.0-local.1}
export VERSION
OUT=${OUT:-$REPO/artifacts/packages}
STAGE=$REPO/artifacts/stage
rm -rf "$STAGE" && mkdir -p "$STAGE" "$OUT"
PACKED=()
# Both repositories' paths, when the framework is built from beside a library (a comma, escaped for -p)
PATH_MAP="$REPO=/_/"
[ "$FRAMEWORK" = "$REPO" ] || PATH_MAP="$PATH_MAP%2C$FRAMEWORK=/_framework/"
COMMON=(-c Release -nologo -v q
  "-p:Version=$VERSION" "-p:Company=Hot Chai Games LLC" "-p:Product=DotGame" "-p:Copyright=Copyright (c) 2026 Hot Chai Games LLC"
  -p:ContinuousIntegrationBuild=true -p:Deterministic=true "-p:PathMap=$PATH_MAP"
  -p:EnableSourceLink=false -p:EnableSourceControlManagerQueries=false -p:IncludeSourceRevisionInInformationalVersion=false
  -p:DebugType=embedded)
build() { echo "== build $1"; dotnet build "$REPO/$1" "${COMMON[@]}" "${@:2}"; }

# The pinned tools the tool packages carry (packaging/fetch-tools.py), first on PATH: shaders are
# compiled with what a game's build would use. Fetched once, into the framework's artifacts.
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) host=osx-arm64 ;; Linux-x86_64) host=linux-x64 ;; Linux-aarch64) host=linux-arm64 ;;
  *) echo "pack.sh: packs on macOS arm64 or Linux"; exit 1 ;;
esac
TOOLS=$FRAMEWORK/artifacts/tools/$host/tools
[ -x "$TOOLS/slangc" ] || python3 "$P/fetch-tools.py" $host "$FRAMEWORK/artifacts/tools/$host" > /dev/null
export PATH=$TOOLS:$PATH

# DotGame.Imaging's version, from the framework's content pipeline tool, which ships its DLL
IMAGING_VERSION=$(dotnet msbuild "$FRAMEWORK/pipeline/tool/DotGame.Framework.ContentPipeline.BuildTool.csproj" -getProperty:DotGameImagingVersion -p:Configuration=Release)
# Its notices, for the packages that ship its DLL: from its package, as restored (by the builds before a pack)
IMAGING_NOTICES=${NUGET_PACKAGES:-$HOME/.nuget/packages}/dotgame.imaging/$IMAGING_VERSION/THIRD-PARTY-NOTICES.md
# DotGame.AudioCodecs's version and notices, the same way: the tool ships its DLL too, and the desktop,
# Android and iOS packages depend on its package
AUDIOCODECS_VERSION=$(dotnet msbuild "$FRAMEWORK/pipeline/tool/DotGame.Framework.ContentPipeline.BuildTool.csproj" -getProperty:DotGameAudioCodecsVersion -p:Configuration=Release)
AUDIOCODECS_NOTICES=${NUGET_PACKAGES:-$HOME/.nuget/packages}/dotgame.audiocodecs/$AUDIOCODECS_VERSION/THIRD-PARTY-NOTICES.md

# A platform project's target framework with its platform version, which a package's lib folder needs
tfm_of() { dotnet msbuild "$REPO/$1" -getProperty:TargetFramework -getProperty:TargetPlatformVersion -p:Configuration=Release 2>/dev/null |
  python3 -c 'import json,sys; p=json.load(sys.stdin)["Properties"]; print(p["TargetFramework"] + p["TargetPlatformVersion"])'; }

# Before each pack: README_BODY, a file in packaging/readme or a path (none if empty), with README_VALUES'
# NAME=value placeholders; and NOTICES, the package's sections of notices files (packaging/tools/notices.py)
README_BODY=""; README_VALUES=(); NOTICES=()

# pack <id> <description> <dependencies xml> [package type]
pack() {
  local id=$1 description=$2 dependencies=$3 type=${4:-}
  local dir=$STAGE/$id
  local types=""
  [ -n "$type" ] && types="<packageTypes><packageType name=\"$type\" /></packageTypes>"
  python3 "$P/tools/readme.py" "$dir/payload/README.md" "$id" "$description" "$README_BODY" ${README_VALUES[@]+"${README_VALUES[@]}"}
  python3 "$P/tools/notices.py" "$dir/payload/THIRD-PARTY-NOTICES.md" "$id" ${NOTICES[@]+"${NOTICES[@]}"}
  README_BODY=""; README_VALUES=(); NOTICES=()
  cat > "$dir/$id.nuspec" <<XML
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$id</id>
    <version>$VERSION</version>
    <authors>Hot Chai Games LLC</authors>
    <copyright>Copyright (c) 2026 Hot Chai Games LLC</copyright>
    <license type="expression">MIT</license>
    <readme>README.md</readme>
    $REPOSITORY_XML
    <description>$description</description>
    <tags>game gamedev 2d framework dotgame</tags>
    $types
    <dependencies>$dependencies</dependencies>
  </metadata>
  <files>
    <file src="payload/**" target="" />
  </files>
</package>
XML
  cat > "$dir/Stub.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <NuspecFile>$id.nuspec</NuspecFile>
    <NuspecBasePath>.</NuspecBasePath>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NoBuild>true</NoBuild>
    <NoWarn>NU5100;NU5128;NU5129</NoWarn>
    <!-- The template's .template.config and .gitignore -->
    <NoDefaultExcludes>true</NoDefaultExcludes>
  </PropertyGroup>
</Project>
XML
  # Without the repository's Directory.Build files, which a stub inside it would otherwise import
  (cd "$dir" && dotnet restore Stub.csproj -nologo -v q -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false > restore.log 2>&1 || { cat restore.log; exit 1; }; dotnet pack Stub.csproj --no-build -nologo -v q -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false -o "$OUT" | grep -v "^$" || true; [ -f "$OUT/$id.$VERSION.nupkg" ] || { echo "pack failed: $id"; exit 1; })
  PACKED+=("$OUT/$id.$VERSION.nupkg")
  echo "== packed $id $VERSION"
}
stage() { mkdir -p "$STAGE/$1/payload/$2"; }
put() { stage "$1" "$2"; cp -R "${@:3}" "$STAGE/$1/payload/$2/"; }
dep() { echo "<group targetFramework=\"$1\">${*:2}</group>"; }
# pin <id> [version]: an exact dependency, by default on this version
pin() { echo "<dependency id=\"$1\" version=\"[${2:-$VERSION}]\" />"; }
