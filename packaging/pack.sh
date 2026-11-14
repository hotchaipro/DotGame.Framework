#!/bin/bash
# pack.sh: the framework's packages, built from this repository in Release: DotGame.Framework, its
# content pipeline (DotGame.Framework.Build), a package per platform and DotGame.Templates.
#
#   VERSION=0.1.0-local.1 PLATFORMS="desktop android ios webgl" packaging/pack.sh
#
# Writes artifacts/packages/*.nupkg (or OUT) and checks their contents with packaging/check.sh. The build machine's tool package
# (DotGame.Framework.Build.Tools.<rid>) comes from packaging/pack-tools.sh. What this shares with a
# library's pack.sh is packaging/pack-lib.sh.
set -euo pipefail
PLATFORMS=${PLATFORMS:-desktop android ios webgl}
REPO=$(cd "$(dirname "$0")/.." && pwd)
FRAMEWORK=$REPO
source "$REPO/packaging/pack-lib.sh"
rm -f "$OUT"/DotGame.Framework.[0-9]*.nupkg "$OUT"/DotGame.Framework.Build.[0-9]*.nupkg "$OUT"/DotGame.Framework.{Desktop,WebGL,Android,iOS}.*.nupkg \
      "$OUT"/DotGame.Templates.*.nupkg

# ---- Build ----
build framework/DotGame.Framework.csproj
build analyzers/DotGame.Analyzers.csproj
build pipeline/tool/DotGame.Framework.ContentPipeline.BuildTool.csproj
build pipeline/tasks/DotGame.Framework.ContentPipeline.Tasks.csproj
for platform in $PLATFORMS; do
  case $platform in
    desktop) build platform/desktop/DotGame.Framework.Desktop.csproj ;;
    webgl) build platform/webgl/DotGame.Framework.WebGL.csproj ;;
    android) build platform/android/DotGame.Framework.Android.csproj ;;
    ios) build platform/ios/DotGame.Framework.iOS.csproj ;;
  esac
done

# ---- Stage and pack ----
N=$REPO/THIRD-PARTY-NOTICES.md
IN=$IMAGING_NOTICES
[ -f "$IN" ] || { echo "pack.sh: no DotGame.Imaging $IMAGING_VERSION notices at $IN"; exit 1; }
AN=$AUDIOCODECS_NOTICES
[ -f "$AN" ] || { echo "pack.sh: no DotGame.AudioCodecs $AUDIOCODECS_VERSION notices at $AN"; exit 1; }

# DotGame.Framework: the core assembly, its analyzers, and the framework's include
F=framework/bin/Release/net11.0
put DotGame.Framework lib/net11.0 "$REPO/$F/DotGame.Framework.dll" "$REPO/$F/DotGame.Framework.xml"
put DotGame.Framework analyzers/dotnet/cs "$REPO/analyzers/bin/Release/netstandard2.0/DotGame.Analyzers.dll"
put DotGame.Framework build "$REPO"/packaging/build/*.targets
put DotGame.Framework buildTransitive "$P"/buildTransitive/DotGame.Framework/*
README_BODY=framework.md
pack DotGame.Framework "The core of DotGame: the game lifecycle, graphics, audio, input, content and time." "$(dep net11.0)"

# DotGame.Framework.Build: the content pipeline's targets, tasks and build tool (no lib, nothing in a game)
B=DotGame.Framework.Build
put $B pipeline "$REPO"/pipeline/*.targets "$REPO"/pipeline/*.props "$REPO"/pipeline/*.xaml
put $B pipeline/build "$REPO"/pipeline/build/DotGame.Framework.ContentPipeline.targets
# The web builds compile it into the runtime (WebAssembly.LegacyExceptions.targets)
put $B pipeline/native "$REPO"/pipeline/native/wasm-legacy-eh.cpp
put $B pipeline/tasks "$REPO/pipeline/tasks/bin/Release/netstandard2.0/DotGame.Framework.ContentPipeline.Tasks.dll"
put $B pipeline/tool "$REPO/pipeline/tool/ContentVersions.props"
T=$REPO/pipeline/tool/bin/Release/net11.0
put $B pipeline/tool "$T"/DotGame.Framework.ContentPipeline.BuildTool.dll "$T"/DotGame.Framework.ContentPipeline.BuildTool.deps.json "$T"/DotGame.Framework.ContentPipeline.BuildTool.runtimeconfig.json "$T"/DotGame.Imaging.dll "$T"/DotGame.AudioCodecs.dll
put $B buildTransitive "$P"/buildTransitive/$B/*
sed -i.bak "s/@VERSION@/$VERSION/" "$STAGE/$B/payload/buildTransitive/$B.props" && rm "$STAGE/$B/payload/buildTransitive/$B.props.bak"
NOTICES=("$AN#libopus" "$AN#libopusenc" "$IN#libjpeg-turbo" "$IN#libwebp" "$N#DirectX Shader Compiler")
pack $B "Added by DotGame.Framework.Desktop, .WebGL, .Android and .iOS; don't reference it directly. DotGame's content pipeline: turns a game's textures, shaders, sounds and music into each platform's formats at build time. Nothing in it ships in a game." "$(dep net11.0)"

# The platforms
for platform in $PLATFORMS; do
  case $platform in
    desktop)
      id=DotGame.Framework.Desktop; L=platform/desktop/bin/Release/net11.0
      put $id lib/net11.0 "$REPO/$L/$id.dll" "$REPO/$L/$id.xml"
      for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do put $id runtimes/$rid/native "$REPO"/native/sdl3/$rid/*; done
      put $id buildTransitive "$P"/buildTransitive/$id/*
      README_BODY=platform.md; README_VALUES=("PLATFORM=desktop" "SNIPPET=var engine = new DesktopGameEngine(new() { Title = \"My Game\" });
await engine.RunAsync(new MyGame());")
      NOTICES=("$N#SDL 3" "$N#HIDAPI" "$N#yuv2rgb")
      # SDL3-CS at the version the platform was built against (eng/Versions.props): an older one fails to load
      sdl3cs=$(dotnet msbuild "$REPO/platform/desktop/$id.csproj" -getProperty:Sdl3CsVersion -p:Configuration=Release)
      pack $id "DotGame for Windows, macOS and Linux, on SDL 3: a game's desktop head references this package." \
        "$(dep net11.0 "$(pin DotGame.Framework)$(pin DotGame.Framework.Build)<dependency id=\"DotGame.AudioCodecs\" version=\"[$AUDIOCODECS_VERSION]\" /><dependency id=\"SDL3-CS\" version=\"$sdl3cs\" exclude=\"Compile\" />")" ;;
    android)
      id=DotGame.Framework.Android; L=$REPO/platform/android/bin/Release/net11.0-android; tfm=$(tfm_of platform/android/$id.csproj)
      put $id lib/$tfm "$L/$id.dll" "$L/$id.xml"
      put $id buildTransitive "$P"/buildTransitive/$id/*
      README_BODY=platform.md; README_VALUES=("PLATFORM=Android" "SNIPPET=// In the main activity's OnCreate
var hostView = new FrameLayout(this);
this.SetContentView(hostView);
new AndroidGameEngine(this, hostView).Run(new MyGame());")
      pack $id "DotGame for Android: a game's Android head references this package." "$(dep $tfm "$(pin DotGame.Framework)$(pin DotGame.Framework.Build)<dependency id=\"DotGame.AudioCodecs\" version=\"[$AUDIOCODECS_VERSION]\" />")" ;;
    ios)
      id=DotGame.Framework.iOS; L=$REPO/platform/ios/bin/Release/net11.0-ios; tfm=$(tfm_of platform/ios/$id.csproj)
      put $id lib/$tfm "$L/$id.dll" "$L/$id.xml"
      [ -d "$L/$id.resources" ] && put $id lib/$tfm "$L/$id.resources"
      put $id buildTransitive "$P"/buildTransitive/$id/*
      README_BODY=platform.md; README_VALUES=("PLATFORM=iOS" "SNIPPET=// In the scene delegate's WillConnect
this.Window = new UIWindow(windowScene);
new iOSGameEngine(this.Window).Run(new MyGame());")
      pack $id "DotGame for iOS: a game's iOS head references this package." "$(dep $tfm "$(pin DotGame.Framework)$(pin DotGame.Framework.Build)<dependency id=\"DotGame.AudioCodecs\" version=\"[$AUDIOCODECS_VERSION]\" />")" ;;
    webgl)
      id=DotGame.Framework.WebGL; L=platform/webgl/bin/Release/net11.0
      put $id lib/net11.0 "$REPO/$L/$id.dll" "$REPO/$L/$id.xml"
      put $id web/_dotgame "$REPO"/platform/webgl/wwwroot/_dotgame/*
      put $id buildTransitive "$P"/buildTransitive/$id/*
      README_BODY=platform.md; README_VALUES=("PLATFORM=web (a Microsoft.NET.Sdk.WebAssembly project for browser-wasm)" "SNIPPET=await new WebGLGameEngine().RunAsync(new MyGame());")
      NOTICES=("$N#.NET wasmbrowser")
      pack $id "DotGame for the web, on WebGL 2 and WebAssembly: a game's web head references this package." "$(dep net11.0 "$(pin DotGame.Framework)$(pin DotGame.Framework.Build)")" ;;
  esac
done

# DotGame.Templates: dotnet new dotgame, a game whose projects reference this version's packages
TP=DotGame.Templates
put $TP content "$P/templates/dotgame"
sed -i.bak "s/@VERSION@/$VERSION/" "$STAGE/$TP/payload/content/dotgame/Directory.Build.props" && rm "$STAGE/$TP/payload/content/dotgame/Directory.Build.props.bak"
README_BODY=templates.md
pack $TP "dotnet new dotgame: a DotGame game, with its shared code and a project for each platform (desktop, web, Android and iOS)." "" Template

ls -la "${PACKED[@]}"
"$P/check.sh" --contents "$P/contents.txt" "${PACKED[@]}"
