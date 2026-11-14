#!/bin/bash
# test-packages.sh [platform...]: the framework's samples and template built from the packages alone.
#
#   VERSION=0.1.0-local.1 packaging/test-packages.sh desktop webgl android ios
#
# Each sample's head is copied, and its DotGame import is replaced by a reference to its platform's
# package, as a developer adds it from NuGet. It's built with a fresh NuGet packages folder, the
# content cache off, and no content tool on PATH. Nothing is built from this repository.
#
# The packages come from FEEDS, folders of .nupkg (by default artifacts/packages), and nuget.org.
#
# A library built on the framework runs this for its own samples, with DOTGAME_LIBRARY_DIR its root and
# DOTGAME_LIBRARY_PACKAGE its package, which the samples' heads reference beside the platform's. Its
# DOTGAME_LIBRARY_CHECKS, if set, is a script sourced here that may define library_head_checks (run
# after each head builds, with $platform, $head and $log) and library_checks (run at the end, with
# fresh, check and CLEAN_PATH), and the framework's own checks of the tools and template don't run.
set -uo pipefail
VERSION=${VERSION:-0.1.0-local.1}
PLATFORMS=${*:-desktop webgl android ios}
REPO=$(cd "$(dirname "$0")/.." && pwd)
FEEDS=${FEEDS:-$REPO/artifacts/packages}
LIBRARY_PACKAGE=""
if [ -n "${DOTGAME_LIBRARY_DIR:-}" ]; then
  LIB=$(cd "$DOTGAME_LIBRARY_DIR" && pwd)
  LIBRARY_PACKAGE=${DOTGAME_LIBRARY_PACKAGE:?test-packages.sh: DOTGAME_LIBRARY_DIR needs DOTGAME_LIBRARY_PACKAGE}
  # The layer's name: the package's last part (DotGame.Example: example)
  LAYER=$(echo "${LIBRARY_PACKAGE##*.}" | tr '[:upper:]' '[:lower:]'); SAMPLES=$LIB/sample
  # The library's folders, apart from its artifacts (where its packages may be) and its samples (copied)
  BUILT_HERE="$REPO/framework|$REPO/platform|$REPO/pipeline"
  for dir in "$LIB"/*/; do
    case $(basename "$dir") in artifacts|sample) ;; *) BUILT_HERE="$BUILT_HERE|$dir" ;; esac
  done
else
  LAYER=framework; SAMPLES=$REPO/sample; BUILT_HERE="$REPO/framework|$REPO/platform|$REPO/pipeline"
fi
# Outside the repository, so none of its Directory.Build files apply, and at a fixed place, so a failed
# run's files can be read afterwards. A linked folder is checked on its own, below.
T=$HOME/dotgame-package-test-$LAYER
rm -rf "$T" && mkdir -p "$T/bin"
export NUGET_PACKAGES=$T/nuget
export MSBUILDDISABLENODEREUSE=1
dotnet build-server shutdown > /dev/null 2>&1
DOTNET_DIR=$(dirname "$(readlink -f "$(command -v dotnet)")")
CLEAN_PATH="$T/bin:/usr/bin:/bin:/usr/sbin:/sbin:$DOTNET_DIR"
PY=$(command -v python3 || command -v python)
# Git Bash on Windows (CI): the system's PATH, which has no content tool, and Windows paths in files
file_path() { if command -v cygpath > /dev/null; then cygpath -m "$1"; else echo "$1"; fi; }
if command -v cygpath > /dev/null; then CLEAN_PATH=$PATH; fi
pass=0; fail=0
# content_published <project folder> <publish folder>: every file the content pipeline built for the
# project (its obj/ContentPipeline/Output) is in the publish folder at the same place, and there is one
content_published() {
  local out=$1/obj/ContentPipeline/Output f n=0
  [ -d "$out" ] || return 1
  while IFS= read -r f; do
    [ -f "$2/${f#"$out"/}" ] || { echo "    not published: ${f#"$out"/}"; return 1; }
    n=$((n+1))
  done < <(find "$out" -type f)
  [ $n -gt 0 ]
}
check() { if eval "$2"; then echo "PASS  $1"; pass=$((pass+1)); else echo "FAIL  $1"; fail=$((fail+1)); fi; }

# The samples, outside the repository, so nothing in it is found by a relative path
mkdir -p "$T/src/$LAYER"
cp -R "$SAMPLES/." "$T/src/$LAYER/"
sources=""; n=0; TEMPLATE=""
for feed in $FEEDS; do
  [ -d "$feed" ] || continue
  n=$((n+1)); sources="$sources    <add key=\"local$n\" value=\"$(file_path "$feed")\" />
"
  [ -n "$TEMPLATE" ] || TEMPLATE=$(ls "$feed"/DotGame.Templates.$VERSION.nupkg 2>/dev/null || true)
done
cat > "$T/src/nuget.config" <<XML
<configuration>
  <packageSources>
    <clear />
$sources    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

# The samples from packages, as a game has them. A head's DotGame import, in the head or in the samples'
# Directory.Build.targets, becomes its platform's package reference, and a library's samples reference
# the library's package too (their game code, a shared project, compiles into each head); and the
# samples' Directory.Build.props drops what's there only to build from source.
"$PY" - "$T/src" "$VERSION" "$LIBRARY_PACKAGE" <<'PY'
import glob, os, re, sys
root, version, library = sys.argv[1], sys.argv[2], sys.argv[3]
names = {"desktop": "Desktop", "webgl": "WebGL", "android": "Android", "ios": "iOS"}
for layer in glob.glob(f"{root}/*/"):
    targets = os.path.join(layer, "Directory.Build.targets")
    if os.path.exists(targets):
        os.remove(targets)
    props = os.path.join(layer, "Directory.Build.props")
    if os.path.exists(props):
        text = open(props, encoding="utf-8").read()
        open(props, "w", encoding="utf-8").write(re.sub(r'\s*<Import Project="\.\.[\\/]eng[\\/][^"]*" />', "", text))
for project in glob.glob(f"{root}/*/*/*.csproj"):
    parts = project.replace("\\", "/").split("/")
    layer, platform = parts[-3], parts[-2]
    if platform not in names:
        continue
    text = open(project, encoding="utf-8").read()
    packages = [f"DotGame.Framework.{names[platform]}"] + ([library] if library else [])
    references = "\n  <ItemGroup>\n" + "".join(f'    <PackageReference Include="{p}" Version="{version}" />\n' for p in packages) + "  </ItemGroup>"
    # The framework's import, or the library's (which brings the framework's)
    imports = "|".join(re.escape(name) for name in ["DotGame.Framework"] + ([library] if library else []))
    text, n = re.subn(r'\s*<Import Project="[^"]*(' + imports + r')\.targets" />', references, text)
    if n == 0:
        text = text.replace("</Project>", references.lstrip("\n") + "\n\n</Project>")
    open(project, "w", encoding="utf-8").write(text)
PY

[ -z "${DOTGAME_LIBRARY_CHECKS:-}" ] || source "$DOTGAME_LIBRARY_CHECKS"

check "no content tool on the clean PATH" '! PATH=$CLEAN_PATH command -v ffmpeg >/dev/null && ! PATH=$CLEAN_PATH command -v slangc >/dev/null && ! PATH=$CLEAN_PATH command -v astcenc >/dev/null'

for platform in $PLATFORMS; do
  for layer in $LAYER; do
    head=$T/src/$layer/$platform
    project=$(ls "$head"/*.csproj)
    log=$T/$layer-$platform.log
    case $platform in
      ios) args=(-r iossimulator-arm64) ;;
      *) args=() ;;
    esac
    PATH=$CLEAN_PATH dotnet build "$project" -nologo -v n -p:ContentCacheEnabled=false ${args[@]+"${args[@]}"} > "$log" 2>&1
    status=$?
    check "$layer sample, $platform: builds from packages" '[ $status -eq 0 ]'
    if [ $status -ne 0 ]; then grep -E "error [A-Z]+[0-9]*:" "$log" | sed "s|$T/||g" | sort -u | head -5 | cut -c1-600; continue; fi
    check "$layer sample, $platform: built nothing from the repositories" '! grep -qE "$BUILT_HERE" "$log"'
    if [ $platform = webgl ]; then
      pub=$T/publish-$layer
      PATH=$CLEAN_PATH dotnet publish "$project" -nologo -v q -c Debug -p:ContentCacheEnabled=false -o "$pub" > "$T/$layer-webgl-publish.log" 2>&1
      check "$layer sample, webgl: publishes" '[ $? -eq 0 ]'
      check "$layer sample, webgl: the platform's scripts are published, fingerprinted" 'ls "$pub"/wwwroot/_dotgame/main.*.js >/dev/null 2>&1 && [ "$(ls "$pub"/wwwroot/_dotgame/*.js | wc -l)" -ge 10 ]'
      check "$layer sample, webgl: the page loads main.js by its fingerprinted name" 'grep -q "_dotgame/main\.[a-z0-9]*\.js" "$pub/wwwroot/index.html"'
      check "$layer sample, webgl: the content is published" 'find "$pub/wwwroot" -path "*Content*" -type f | grep -q .'
      check "$layer sample, webgl: the framework is published once" '[ "$(ls "$pub/wwwroot/_framework" | grep -cE "^DotGame\.Framework\.[a-z0-9]{10}\.wasm$")" -eq 1 ] && [ "$(ls "$pub/wwwroot/_framework" | grep -cE "^DotGame\.Framework\.WebGL\.[a-z0-9]{10}\.wasm$")" -eq 1 ]'
    fi
    if [ $platform = desktop ]; then
      pub=$T/publish-$layer-desktop
      PATH=$CLEAN_PATH dotnet publish "$project" -nologo -v q -c Release --use-current-runtime -p:ContentCacheEnabled=false -o "$pub" > "$T/$layer-desktop-publish.log" 2>&1
      check "$layer sample, desktop: publishes" '[ $? -eq 0 ]'
      check "$layer sample, desktop: the content is published" 'content_published "$head" "$pub"'
    fi
    if declare -F library_head_checks > /dev/null; then library_head_checks; fi
    check "$layer sample, $platform: shaders compiled by the packages' slangc" '! grep -q -- "--slangc \"slangc\"" "$log" && { ! grep -q -- "--slangc" "$log" || grep -q "dotgame.framework.build.tools.[a-z0-9-]*/[^/]*/tools/slangc" "$log"; }'
  done
done

# A library with no platform (a game's shared code), which references the framework's package, or the
# library's, builds
library=${LIBRARY_PACKAGE:-DotGame.Framework}
lib=$T/src/shared-code; mkdir -p "$lib"
cat > "$lib/Library.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="$library" Version="$VERSION" /></ItemGroup>
</Project>
XML
echo 'public static class Shared { public static DotGame.Framework.Color Tint => DotGame.Framework.Color.Black; }' > "$lib/Shared.cs"
PATH=$CLEAN_PATH dotnet build "$lib/Library.csproj" -nologo -v q > "$T/library.log" 2>&1
check "a library with no platform builds from $library's package" '[ $? -eq 0 ]'
grep -E "error" "$T/library.log" | head -3 | cut -c1-600

check "a library with no platform restores no content tools" '! grep -qi "dotgame.framework.build.tools" "$lib/obj/project.assets.json" && ! grep -q "Restoring DotGame" "$T/library.log"'
echo "Downloaded: $(ls "$NUGET_PACKAGES" | grep dotgame | tr '\n' ' ')"

# How the tools arrive, each from an empty packages folder, on fresh copies of the samples
fresh() { rm -rf "$T/$1" && cp -R "$T/src" "$T/$1" && find "$T/$1" -type d \( -name obj -o -name bin \) -prune -exec rm -rf {} +; }
tools_in() { ls -d "$1"/dotgame.framework.build.tools.*/*/tools >/dev/null 2>&1; }

# How the framework's packages bring the tools: the framework's run only
if [ -z "$LIBRARY_PACKAGE" ]; then

# Restore alone (CI's restore step): NuGet leaves packages' build files out of restore, so it never
# fetches the tools; the build after it does, with --no-restore too
fresh restore; export NUGET_PACKAGES=$T/nuget-restore
project=$(ls "$T/restore/framework/desktop"/*.csproj)
PATH=$CLEAN_PATH dotnet restore "$project" -nologo > "$T/restore-1.log" 2>&1
check "restore: succeeds, without the tools" '[ $? -eq 0 ] && ! tools_in "$NUGET_PACKAGES"'
PATH=$CLEAN_PATH dotnet build "$project" --no-restore -nologo -v n -p:ContentCacheEnabled=false > "$T/restore-build.log" 2>&1
check "build --no-restore after it: restores the tools itself and builds" '[ $? -eq 0 ] && grep -q "Restoring DotGame" "$T/restore-build.log" && tools_in "$NUGET_PACKAGES"'

# CI fetching everything before going offline: restore, then dotnet package download puts the tools
# in the packages folder (it writes NuGet's own layout), so the build fetches nothing
fresh download; export NUGET_PACKAGES=$T/nuget-download
project=$(ls "$T/download/framework/desktop"/*.csproj)
rid=$(ls "$T/nuget"/ | sed -n 's/^dotgame\.framework\.build\.tools\.//p' | head -1)
PATH=$CLEAN_PATH dotnet restore "$project" -nologo > "$T/download-1.log" 2>&1 &&
  (cd "$T/src" && PATH=$CLEAN_PATH dotnet package download "DotGame.Framework.Build.Tools.$rid@$VERSION" --prerelease -o "$NUGET_PACKAGES") >> "$T/download-1.log" 2>&1
check "restore and dotnet package download: the tools are in the packages folder" '[ $? -eq 0 ] && tools_in "$NUGET_PACKAGES"'
PATH=$CLEAN_PATH dotnet build "$project" --no-restore -nologo -v n -p:ContentCacheEnabled=false > "$T/download-build.log" 2>&1
check "build --no-restore after them: builds, and restores nothing itself" '[ $? -eq 0 ] && ! grep -q "Restoring DotGame" "$T/download-build.log"'

# A first build: one restore, then the build restores the tools itself, once
fresh first; export NUGET_PACKAGES=$T/nuget-first
project=$(ls "$T/first/framework/desktop"/*.csproj)
PATH=$CLEAN_PATH dotnet build "$project" -nologo -v n -p:ContentCacheEnabled=false > "$T/first-1.log" 2>&1
check "first build: restores the tools itself and builds" '[ $? -eq 0 ] && grep -q "Restoring DotGame" "$T/first-1.log" && tools_in "$NUGET_PACKAGES"'
PATH=$CLEAN_PATH dotnet build "$project" -nologo -v n -p:ContentCacheEnabled=false > "$T/first-2.log" 2>&1
check "second build: restores nothing itself" '[ $? -eq 0 ] && ! grep -q "Restoring DotGame" "$T/first-2.log"'

# Two heads' first builds at once share the packages folder
fresh parallel; export NUGET_PACKAGES=$T/nuget-parallel
cp -R "$T/parallel/framework/desktop" "$T/parallel/framework/desktop-2"
a=$(ls "$T/parallel/framework/desktop"/*.csproj); b=$(ls "$T/parallel/framework/desktop-2"/*.csproj)
PATH=$CLEAN_PATH dotnet build "$a" -nologo -v n -p:ContentCacheEnabled=false > "$T/parallel-a.log" 2>&1 & pa=$!
PATH=$CLEAN_PATH dotnet build "$b" -nologo -v n -p:ContentCacheEnabled=false > "$T/parallel-b.log" 2>&1 & pb=$!
wait $pa; sa=$?; wait $pb; sb=$?
check "two first builds at once: both build" '[ $sa -eq 0 ] && [ $sb -eq 0 ] && tools_in "$NUGET_PACKAGES"'
for f in parallel-a parallel-b first-1 restore-build; do grep -E "error [A-Z]+[0-9]*:" "$T/$f.log" | sed "s|$T/||g" | sort -u | head -3 | cut -c1-600; done
fi

# A web head reached through a link (macOS's temporary folder is /var/folders, which is /private/var/folders),
# built and published by its full path: the SDK's asset paths resolve the link, MSBuild's don't
if [[ " $PLATFORMS " == *" webgl "* ]]; then
  linked=$(mktemp -d); trap 'rm -rf "$linked"' EXIT
  cp -R "$T/src" "$linked/" && find "$linked" -type d \( -name obj -o -name bin \) -prune -exec rm -rf {} +
  export NUGET_PACKAGES=$T/nuget
  for layer in $LAYER; do
    project=$(ls "$linked/src/$layer/webgl"/*.csproj)
    (cd "$HOME" && PATH=$CLEAN_PATH dotnet build "$project" -nologo -v q -p:ContentCacheEnabled=false &&
      PATH=$CLEAN_PATH dotnet publish "$project" -nologo -v q -c Debug -p:ContentCacheEnabled=false -o "$linked/publish-$layer") > "$T/linked-$layer.log" 2>&1
    check "$layer sample, webgl: builds and publishes from a linked folder" '[ $? -eq 0 ] && grep -q "_dotgame/main\.[a-z0-9]*\.js" "$linked/publish-$layer/wwwroot/index.html"'
    grep -E "error" "$T/linked-$layer.log" | sort -u | head -2 | cut -c1-300
  done
fi

# The library's own checks
if declare -F library_checks > /dev/null; then library_checks; fi

# The template, as the getting-started guide uses it: installed (into a hive of its own, not this
# machine's), dotnet new dotgame, and each platform's project built from the packages
if [ -z "$LIBRARY_PACKAGE" ]; then
tpl=$T/template; hive=$T/template-hive
mkdir -p "$tpl" && cp "$T/src/nuget.config" "$tpl/"
export NUGET_PACKAGES=$T/nuget
dotnet new install "$(file_path "${TEMPLATE:-DotGame.Templates.$VERSION.nupkg}")" --debug:custom-hive "$hive" > "$T/template-install.log" 2>&1
check "template: installs" '[ $? -eq 0 ]'
(cd "$tpl" && dotnet new dotgame -n BallGame --debug:custom-hive "$hive") > "$T/template-new.log" 2>&1
check "template: dotnet new dotgame makes a game on this version" '[ $? -eq 0 ] && [ -f "$tpl/BallGame/BallGame.slnx" ] && grep -q "<DotGameVersion>$VERSION</DotGameVersion>" "$tpl/BallGame/Directory.Build.props"'
check "template: the application ID comes from the name" 'grep -q "<ApplicationId>com.example.ballgame</ApplicationId>" "$tpl/BallGame/BallGame.Android/BallGame.Android.csproj" && grep -q "namespace BallGame;" "$tpl/BallGame/BallGame/MainGame.cs"'
for platform in $PLATFORMS; do
  case $platform in
    desktop) head=Desktop; args=() ;; webgl) head=WebGL; args=() ;; android) head=Android; args=() ;;
    ios) head=iOS; args=(-r iossimulator-arm64) ;;
  esac
  project=$tpl/BallGame/BallGame.$head/BallGame.$head.csproj
  PATH=$CLEAN_PATH dotnet build "$project" -nologo -v n -p:ContentCacheEnabled=false ${args[@]+"${args[@]}"} > "$T/template-$platform.log" 2>&1
  status=$?
  check "template, $platform: builds from packages, with no compiler or DotGame warning" '[ $status -eq 0 ] && ! grep -q "warning DG\|warning CS" "$T/template-$platform.log"'
  grep -E "(error|warning) [A-Z]+[0-9]*:" "$T/template-$platform.log" | sed "s|$T/||g" | sort -u | head -3 | cut -c1-600
  if [ $platform = desktop ] && [ $status -eq 0 ]; then
    check "template, desktop: the ball is built" 'find "$tpl/BallGame/BallGame.Desktop/bin" -path "*Content/Textures/ball.*" | grep -q .'
    # Published as the guide says, for this machine's platform: the build's content is beside the app
    pub=$T/template-desktop-publish
    PATH=$CLEAN_PATH dotnet publish "$project" -nologo -v q -c Release --use-current-runtime -p:ContentCacheEnabled=false -o "$pub" > "$T/template-desktop-publish.log" 2>&1
    check "template, desktop: publishes" '[ $? -eq 0 ]'
    grep -E "error [A-Z]+[0-9]*:" "$T/template-desktop-publish.log" | sed "s|$T/||g" | sort -u | head -3 | cut -c1-600
    check "template, desktop: the content and launch screen are published" 'content_published "$tpl/BallGame/BallGame.Desktop" "$pub"'
  fi
done
fi
echo "$pass passed, $fail failed"
[ $fail -eq 0 ]
