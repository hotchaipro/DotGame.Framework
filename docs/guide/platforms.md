# Platforms

All of a game's code is shared. Each platform has a small project holding its entry point, which
references that platform's package and builds the game's shared code and content
([Multiple platforms](multiple-platforms.md)). `dotnet new dotgame` makes them all:

```
MyGame/
  MyGame/           MyGame.shproj, MainGame.cs: the game, a shared project
  Content/          Its content
  MyGame.Desktop/   MyGame.Desktop.csproj, Program.cs
  MyGame.WebGL/     MyGame.WebGL.csproj,   Program.cs, wwwroot/index.html
  MyGame.Android/   MyGame.Android.csproj, MainActivity.cs
  MyGame.iOS/       MyGame.iOS.csproj,     Program.cs, AppDelegate.cs, SceneDelegate.cs, Info.plist
```

Each platform's project references its package, which brings the framework and the content pipeline:

```xml
<ItemGroup>
  <PackageReference Include="DotGame.Framework.Desktop" Version="$(DotGameVersion)" />
</ItemGroup>
```

| Platform | Package | Graphics | Project type |
|---|---|---|---|
| Windows, macOS, Linux | `DotGame.Framework.Desktop` | SDL3 GPU (Vulkan, Direct3D 12, Metal) | `Microsoft.NET.Sdk` |
| Web | `DotGame.Framework.WebGL` | WebGL 2 | `Microsoft.NET.Sdk.WebAssembly` |
| Android | `DotGame.Framework.Android` | OpenGL ES 3 | `Microsoft.NET.Sdk` with `net11.0-android` |
| iOS | `DotGame.Framework.iOS` | Metal | `Microsoft.NET.Sdk` with `net11.0-ios` |

The web and Android deliberately use WebGL 2 and OpenGL ES 3 rather than WebGPU and Vulkan, because
they run on far more devices in use today.

## Desktop

```csharp
internal static class DesktopProgram
{
    public static async Task Main()
    {
        var engine = new DesktopGameEngine(new()
        {
            Title = "My Game",
            WindowSize = WindowSize.Fit(9f / 16f, maxScreenPercent: 85),
        });

        await engine.RunAsync(new MainGame());
    }
}
```

[The options](../api/Desktop/DesktopGameEngine-Options.md) also cover how frames reach the screen and
multisampling.

### The window

Two settings say how the window opens, and they can't disagree. [`WindowSize`](../api/Desktop/WindowSize.md)
is how big it is when it's a window, and [`WindowMode`](../api/Desktop/WindowMode.md) is the state it's
in:

```csharp
// With neither set: 16:9, taking up to 85% of the screen
var standard = new DesktopGameEngine.Options { Title = "My Game" };

// A portrait game, as large as fits on the screen
var portrait = new DesktopGameEngine.Options
{
    Title = "My Game",
    WindowSize = WindowSize.Fit(9f / 16f),
};

// With limits: whichever is reached first decides
var limited = new DesktopGameEngine.Options
{
    Title = "My Game",
    WindowSize = WindowSize.Fit(16f / 9f, maxScreenPercent: 85, maxSize: new Vector2Int(1280, 720)),
};

// Opens full screen, and leaving it goes to a 16:9 window
var fullScreen = new DesktopGameEngine.Options
{
    Title = "My Game",
    WindowSize = WindowSize.Fit(16f / 9f),
    WindowMode = WindowMode.FullScreen,
};
```

Full screen covers the whole screen without changing the display's resolution. Players can resize the
window, so a game should handle any size.

The options decide how the window opens. After that, in Release builds, it reopens as the player left
it, until the game's window options change, such as in an update. `RememberWindow` turns that off,
Debug builds leave it off so runs are repeatable, and the `--reset-window` switch forgets the saved
window.

To change the mode while the game runs, as a settings menu would, set the engine's
[`WindowMode`](../api/Desktop/DesktopGameEngine.md#windowmode):

<!-- no-check -->
```csharp
engine.WindowMode = WindowMode.FullScreen;
```

The change starts by the next frame and can take a second, as macOS animates it. Reading `WindowMode`
says what the window is now, so it changes when that's done, and the game also sees it as a change of
size. It follows the player's own changes too. It's only on the desktop engine, since the other
platforms have no window to change, so a game shared across platforms keeps the engine in its desktop
project and passes the setting down.

### Publishing

To publish:

```sh
dotnet publish MyGame/MyGame.Desktop -c Release -r win-x64
```

## The web

A plain .NET WebAssembly app with a small JavaScript host, with no Blazor runtime to download. The
project uses the `Microsoft.NET.Sdk.WebAssembly` SDK and the `browser-wasm` runtime identifier, and the
[framework sample's project](../../sample/webgl/DotGame.Framework.Sample.WebGL.csproj) has the
settings to copy, including AOT and trimming for Release.

The web needs the `wasm-tools` workload (`dotnet workload install wasm-tools`). Every build links the
.NET runtime with Emscripten, so that older browsers run it (Safari before 18.4, Chrome before 137), and on Linux Emscripten needs
Python 3. Most distributions have it, but container images such as Microsoft's .NET SDK images often
don't: install `python3` with the system's package manager. The workload brings its own Python on
Windows and macOS.

<!-- no-check -->
```csharp
[SupportedOSPlatform("browser")]
partial class Program
{
    public static async Task Main()
    {
        var engine = new WebGLGameEngine(new());
        await engine.RunAsync(new MainGame());
    }
}
```

A web project's content goes in `wwwroot`, so that it's served. Content shared with other projects is
named there with `LinkBase` ([Multiple platforms](multiple-platforms.md#linkbase)):

```xml
<TextureContent Include="..\Content\Textures\*.png">
  <LinkBase>wwwroot\Content\Textures</LinkBase>
</TextureContent>
```

Content kept in the web project itself goes in `wwwroot\Content`. Only the processed file is published,
not the source.

### The page

A web game's page needs almost nothing in it. The build adds the viewport and web app tags, the
styles, the import map, the module preloads, the launch screen and the canvas. A game needn't have an
`index.html` at all, in which case the title comes from `GameTitle`, or else the assembly name. A page,
if it has one, only holds what's about the game:

```html
<!DOCTYPE html>
<html>
<head>
    <title>My Game</title>
    <meta name="description" content="What it's about">
    <link rel="icon" href="favicon.ico">
</head>
<body></body>
</html>
```

A tag the page has wins over the generated one, and its own `<canvas>` replaces the generated one (set
`CanvasElementId` to match). `GenerateWebPage=false` turns all of it off.

### Fast startup

The framework is built around starting quickly in a browser. Each texture, sound and music track is
its own file in a format the browser decodes natively, so asset data never has to be copied into
WebAssembly memory and decoded by .NET code.

- **Preload what the first frame needs.** Add `Preload="true"` to those items, to start downloading
  them with the page. While you develop on `localhost`, the console lists anything loaded before the
  first frame that isn't marked, and warns about anything marked that the first frame didn't load:
  its download only slowed the start.
- **Start other work after the first frame**, with
  [`FirstFrameDrawn`](game-loop.md#startup).
- A package's JavaScript modules join the preloads as `WebStartupModule` items. A module that must
  run before the game starts, such as a crash reporter that should hear about a browser that can't
  run the game, is a `WebPageModule` item: the page runs it before `main.js`, and its `Config`
  metadata is the script's `data-config` attribute.
- The runtime runs on Safari 16.4, Chrome 91 and Firefox 89 and later: it needs WebGL 2 and
  WebAssembly SIMD, and its exceptions use WebAssembly exception handling as first shipped (.NET 11
  links the standard's final form, which needs Safari 18.4, Chrome 137 or Firefox 131;
  `DotGameWebLegacyExceptions=false` keeps it). A browser without what the runtime needs is told
  so, and asked to update, rather than shown a crash. The crash reporter gets it as a warning.

### Publishing

```sh
dotnet publish MyGame/MyGame.WebGL -c Release -o publish
```

Every content file and framework script is published with a content hash in its file name, and loaders
request the right name automatically. So you can serve content with long-lived, immutable cache headers
or from a CDN, and players only download files that changed in a new release.

### Hosting

A publish compresses every file the page fetches that isn't compressed already (content, your game's own
data files, and the runtime's WebAssembly) with Brotli at its highest quality, as a `.br` copy beside the
file, and the page fetches the copies. The host must send them as they are, with the header
`Content-Encoding: br`, and the browser decodes them as they arrive. Your file types don't matter to the
host, so there are no MIME types to register, and every file is as small as Brotli makes it, whether or not
the host compresses anything itself.

A host that isn't set up sends a `.br` copy without the header, and the game stops with an error saying
so, naming the file. That's on purpose: a game that only loads slowly would look like a slow framework.
A copy that arrives in another encoding, or decoded, is fine: a CDN (Cloudflare does) sends a browser or
proxy that doesn't accept Brotli the file itself, plain or as gzip, and the page takes it as it is (a
WebAssembly file with no encoding is checked for WebAssembly's magic number). Set the host up:

| Host | Setup |
|---|---|
| Netlify | Nothing: the publish adds the rule to the game's `_headers` file (or writes one): `/*.br` with `Content-Encoding: br`. |
| Cloudflare Workers | A Worker must send the copies: Cloudflare's static assets compress a `.br` again whatever `_headers` says (even with `Cache-Control: no-transform`), so the browser gets it still compressed. Run the Worker first for the folders the page fetches copies from (`run_worker_first = ["/_framework/*", "/Content/*"]`), and for a request ending in `.br` return the asset with `Content-Encoding: br`, the original file's `Content-Type` and `encodeBody: 'manual'`. Each of those requests is a Worker request. Upload the copies: a `.assetsignore` mustn't leave out `*.br`. |
| nginx | `location ~ \.br$ { add_header Content-Encoding br; types { } default_type application/octet-stream; }` |
| Apache | `<FilesMatch "\.br$">` with `Header set Content-Encoding br` and `ForceType application/octet-stream` (mod_headers). |
| Any other | Send each `*.br` file with `Content-Encoding: br`. |

A host that can't send headers (itch.io, GitHub Pages, a bucket without per-file metadata) needs the
fallback: set `<DotGameWebDecompressionFallback>true</DotGameWebDecompressionFallback>` in the web project.
The publish then writes gzip copies (`.gz`) instead, and the page decodes them itself as they arrive, natively
and while the WebAssembly compiles. It's slower than Brotli from a host set up for it (gzip is larger), and the
page logs a warning saying so. To fetch every file as it is and leave compression to the host, set
`<CompressionEnabled>false</CompressionEnabled>`; files of types the host doesn't know then go out
uncompressed.

A build (`dotnet run`) writes no copies and fetches every file as it is, so only a published build needs the
host set up.

A web game can be installed as a progressive web app, with a web app manifest, and run full screen.

Only a Release publish runs the trimmer, so publish and try it before you ship.

A Release build serves the platform's scripts (`_dotgame/*.js`) without their comments and indentation,
which are most of their download, and links the runtime without native debug information
(`WasmNativeDebugSymbols` is `false`), so `dotnet.native.js` is minified too. Each script keeps its lines, so
the line numbers and function names in a stack trace from a player still point at the source.

## Android

Android needs the `android` workload, the Android SDK and a JDK. Visual Studio on Windows installs all
three with its .NET Multi-platform App UI development workload, and builds find them there, so you can
skip the rest of this. Otherwise, one build target of the workload's installs the SDK and Microsoft's
build of OpenJDK, into the folders you name; with Android Studio's SDK folder, it adds what's missing
there:

```sh
dotnet workload install android
dotnet build MyGame/MyGame.Android -t:InstallAndroidDependencies -p:AndroidSdkDirectory=<sdk folder> -p:JavaSdkDirectory=<jdk folder> -p:AcceptAndroidSDKLicenses=True -p:AndroidManifestType=GoogleV2
```

`AcceptAndroidSDKLicenses` accepts the Android SDK's licenses for you, so read them first if you
haven't. `AndroidManifestType=GoogleV2` installs from Google's list of SDK packages, since the default
list doesn't have the command-line tools that .NET for Android 37.2 asks for. Then set `ANDROID_HOME` to
the SDK's folder and `JAVA_HOME` to the JDK's, so that every build finds them. Android Studio's own SDK
folder is `~/Library/Android/sdk` on macOS, `%LOCALAPPDATA%\Android\Sdk` on Windows and
`~/Android/Sdk` on Linux.

The entry point is an `Activity`. Set `ConfigurationChanges` so that turning the device doesn't restart
the game, and use the launch screen's theme:

<!-- no-check -->
```csharp
[Activity(
    Label = "My Game",
    MainLauncher = true,
    ConfigurationChanges = AndroidGameEngine.ConfigurationChanges,
    Theme = "@style/DotGame.LaunchScreen")]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var hostView = new FrameLayout(this);
        this.SetContentView(hostView);

        new AndroidGameEngine(this, hostView).Run(new MainGame());
    }
}
```

A game runs once in a process, and the process lives as long as the game. Finishing the activity
ends the process once the game has stopped. When Android destroys the activity without ending the
process and makes another in its place (a configuration change the activity doesn't handle, such as
the language, or the system reclaiming it, as Don't keep activities does), the app starts again in a
new process from inside the new activity's `base.OnCreate`, before it makes a game. So anything set
up once in `OnCreate` after `base.OnCreate` runs once in a process, as on the other platforms.

## iOS

iOS needs a Mac to build. The game runs in a window that a scene delegate makes. The
[sample's `Program.cs`](../../sample/ios/Program.cs) has the application and scene delegates to copy,
and the part that's yours is:

<!-- no-check -->
```csharp
this.Window = new UIWindow(windowScene);

var options = new iOSGameEngine.Options
{
    AudioCategory = AudioCategory.Ambient,
};

new iOSGameEngine(this.Window, options).Run(new MainGame());
```

[`AudioCategory`](../api/iOS/AudioCategory.md) chooses how the game's audio mixes with other
apps' and whether it respects the silent switch.

The iOS build is fully ahead-of-time compiled, so code that depends on generating code at run time
doesn't work there. Try a Release build on a simulator or a device early.

## Frame rates

A game can say what frame rate it would like, in shared code, on every platform, and change it at any
time. A casual game can ask for 60 on a fast display, for the battery, or run its menus slower than
play:

<!-- no-check -->
```csharp
this.PreferredFrameRate = 60;

// Back to the display's own rate, the default
this.PreferredFrameRate = null;
```

[`PreferredFrameRate`](../api/Framework/Game.md#preferredframerate) is a preference. The desktop and
the web draw at most that many frames a second, unevenly if it doesn't divide into the display's rate,
such as 60 on a 144 Hz display. Android (from Android 11) and iOS ask the system, which has the last word,
and on a ProMotion iPhone, going above 60 also needs `CADisableMinimumFrameDurationOnPhone` in the app's
`Info.plist`.

## The launch screen

From launch until the first frame, every platform shows the same launch screen: your background color,
your logo in the middle, and optionally a publisher mark at the bottom. You describe it once, and the
build makes each platform's form of it:

```xml
<ItemGroup>
  <LaunchScreen Include="Content\LaunchScreen\logo.png" Background="#181c28" LogoSize="140" />
</ItemGroup>
```

It fades out once the first frame is on screen, so you write no launch code. To continue the look into
your first scene, clear to the same color.

## Platform differences to know

The framework behaves the same on every platform wherever it can. What can't be the same:

| | Desktop | Web | Android | iOS |
|---|---|---|---|---|
| Threads | Many | **One** | Many | Many |
| Device can be lost | No | Yes | Yes | No |
| `File.*` lasts | Yes | **No** | Yes | Yes |
| Audio needs a player's gesture first | No | Yes | No | No |

- **One thread on the web:** see [threads and async](game-loop.md#threads-and-async).
- **Lost devices:** see [render textures](graphics.md#render-textures).

## Saving games

The framework adds nothing over .NET here. Use `System.IO` with
`Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` on desktop, Android and iOS.

```csharp
private async Task SaveProgress(string json)
{
    var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    await File.WriteAllTextAsync(Path.Combine(folder, "progress.json"), json);
}
```

**The web is different.** In a browser, `File.*` writes to memory, and what it wrote is gone when the
page reloads, with no error. Save through the browser instead, with local storage or the origin-private
file system over JavaScript interop.

Save in [`OnPause`](game-loop.md#pausing-resuming-and-stopping), as an app can be ended without warning
after that.

## Trimming and AOT

The framework is marked AOT-compatible, and web and iOS release builds use AOT. Avoid reflection and
anything that generates code at run time in game code, and treat the build's trim and AOT warnings as
real: code they point at can fail only in a published build.

## See also

- [`DesktopGameEngine`](../api/Desktop/DesktopGameEngine.md),
  [`WebGLGameEngine`](../api/Web/WebGLGameEngine.md),
  [`AndroidGameEngine`](../api/Android/AndroidGameEngine.md),
  [`iOSGameEngine`](../api/iOS/iOSGameEngine.md)
- The sample's projects in [`sample/`](../../sample/README.md)
