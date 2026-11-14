#!/bin/bash
# pack-tools.sh <rid> [fetched folder]: DotGame.Framework.Build.Tools.<rid>, from the tools that
# packaging/fetch-tools.py fetched for that build machine (default artifacts/tools/<rid>).
#
#   python3 packaging/fetch-tools.py osx-arm64 artifacts/tools/osx-arm64
#   VERSION=0.1.0-local.1 packaging/pack-tools.sh osx-arm64
#
# The package holds slangc (with the libraries it loads), spirv-cross, spirv2dxil and astcenc, each
# from the source pinned in packaging/tool-sources.json, with the notices made from those sources
# (packaging/tools/tool-notices.py). WebP needs no tool: the build tool encodes it (DotGame.Imaging).
set -euo pipefail
RID=${1:?usage: pack-tools.sh <rid> [fetched folder]}
VERSION=${VERSION:-0.1.0-local.1}
REPO=$(cd "$(dirname "$0")/.." && pwd)
FETCHED=${2:-$REPO/artifacts/tools/$RID}
OUT=$REPO/artifacts/packages
STAGE=$REPO/artifacts/stage-tools/$RID
ID=DotGame.Framework.Build.Tools.$RID
. "$REPO/packaging/repository.sh"
[ -d "$FETCHED/tools" ] || { echo "pack-tools.sh: nothing fetched in $FETCHED (packaging/fetch-tools.py $RID $FETCHED)"; exit 1; }
rm -rf "$STAGE" && mkdir -p "$STAGE/payload" "$OUT"
cp -R "$FETCHED/tools" "$STAGE/payload/tools"
tools="slangc, spirv-cross and spirv2dxil for shaders, astcenc for textures"
PY=$(command -v python3 || command -v python)  # Windows has only python
"$PY" "$REPO/packaging/tools/tool-notices.py" "$STAGE/payload/THIRD-PARTY-NOTICES.md" "$ID" "$FETCHED" "$STAGE/payload/tools"
description="Fetched by DotGame.Framework.Build on $RID build machines; don't reference it directly. DotGame's content tools for $RID build machines: $tools."
printf '# %s\n\n%s\n\nRestored by DotGame.Framework.Build on %s build machines only; nothing in it ships in a game.\n\nCopyright (c) 2026 Hot Chai Games LLC. The tools keep their own licenses: see THIRD-PARTY-NOTICES.md.\n' "$ID" "$description" "$RID" > "$STAGE/payload/README.md"
cat > "$STAGE/$ID.nuspec" <<XML
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$ID</id>
    <version>$VERSION</version>
    <authors>Hot Chai Games LLC</authors>
    <copyright>Copyright (c) 2026 Hot Chai Games LLC</copyright>
    <license type="expression">MIT</license>
    <readme>README.md</readme>
    $(repository_xml "$REPO")
    <description>$description</description>
    <developmentDependency>true</developmentDependency>
  </metadata>
  <files><file src="**" target="" /></files>
</package>
XML
cat > "$STAGE/Stub.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <NuspecFile>$ID.nuspec</NuspecFile>
    <!-- The payload itself, so the wildcard has no folder to strip (NuGet on Windows kept it) -->
    <NuspecBasePath>payload</NuspecBasePath>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NoBuild>true</NoBuild>
    <NoWarn>NU5100;NU5128</NoWarn>
  </PropertyGroup>
</Project>
XML
# Without the repository's Directory.Build files, which a stub inside it would otherwise import
ISOLATE="-p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false"
(cd "$STAGE" && { dotnet restore Stub.csproj -nologo -v q $ISOLATE && dotnet pack Stub.csproj --no-build -nologo -v q $ISOLATE -o "$OUT"; } > pack.log 2>&1 || { cat pack.log; exit 1; })
echo "== packed $ID $VERSION"
