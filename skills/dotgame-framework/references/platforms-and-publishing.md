# Platforms and publishing

## Contents

- The heads
- Desktop
- The web
- Android
- iOS
- Frame rates
- The launch screen
- Platform differences and saving games
- Trimming, AOT, and what the packages set
- Feature switches

## The heads

All game code is shared; each platform has a small project holding its entry point, which references its package and imports the game's shared `.props`.

| Platform | Package | Graphics | Project |
|---|---|---|---|
| Windows, macOS, Linux | `DotGame.Framework.Desktop` | SDL3 GPU (Vulkan, Direct3D 12, Metal) | `Microsoft.NET.Sdk`, `net11.0` |
| Web | `DotGame.Framework.WebGL` | WebGL 2 | `Microsoft.NET.Sdk.WebAssembly`, `browser-wasm` |
| Android | `DotGame.Framework.Android` | OpenGL ES 3 | `Microsoft.NET.Sdk`, `net11.0-android` |
| iOS | `DotGame.Framework.iOS` | Metal | `Microsoft.NET.Sdk`, `net11.0-ios` |

Without the template, start from `dotnet new console`, `wasmbrowser`, `android`, or `ios`, and add the platform package (`dotnet add package DotGame.Framework.Desktop --prerelease`).

## Desktop

```csharp
internal static class Program
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

- `WindowSize` is the window's size when windowed (default 16:9, up to 85% of the screen); `WindowMode` is `Windowed`, `Maximized`, or `FullScreen` (borderless, no resolution change). Players can resize, so handle any size.
- Release builds reopen the window as the player left it; `RememberWindow = false` turns that off, Debug leaves it off, and `--reset-window` forgets it.
- `engine.WindowMode = WindowMode.FullScreen` changes it while running; it's desktop-only, so keep the engine in the desktop head and pass the setting down.
- Other options: `PresentMode` (`VSync`, `Mailbox`, `Immediate`), `GraphicsDebug`.
- Publish: `dotnet publish MyGame/MyGame.Desktop -c Release -r win-x64` (or `osx-arm64`, `linux-x64`, ...).
- A running game locks its executable, so a rebuild's copy fails (MSB3021/MSB3027). Close it, or build to another folder with `-o`.

## The web

A plain .NET WebAssembly app with a small JavaScript host; no Blazor.

```csharp
[SupportedOSPlatform("browser")]
internal static partial class Program
{
    public static async Task Main()
    {
        var engine = new WebGLGameEngine();
        await engine.RunAsync(new MainGame());
    }
}
```

- The template's web project sets `OverrideHtmlAssetPlaceholders=true`, and for Release `RunAOTCompilation`, `WasmStripILAfterAOT`, `WasmEnableSIMD`, and `WasmEnableExceptionHandling`. Copy those if you make the project yourself.
- Content goes under `wwwroot\Content\`: keep it in `wwwroot`, or name shared content there with a `LinkBase` (or a `Link`) starting `wwwroot\`. Only processed files are published.
- **Write code that WebAssembly AOT compiles.** Mono's AOT compiles a generic instance over a value type only where it sees that instance used directly; code reached only through an interface runs in the interpreter, and the first interpreted call also starts the interpreter (about 40 ms). Don't enumerate arrays or lists of structs through `IEnumerable<T>`, `IList<T>` or a read-only collection (take a `T[]`, `List<T>` or `ReadOnlySpan<T>` and index or `foreach` it), don't enumerate a `ConcurrentDictionary` whose values are structs (`ToArray()` it), and marshal JavaScript callbacks with reference-type parameters (`Action<string, string>`, not `Action<int, string>`). To check a build, log Mono's AOT lookups (`MONO_LOG_MASK=aot`): every `AOT NOT FOUND` method runs in the interpreter.
- **Keep blocking browser calls off the startup path.** `new AudioContext()` and the first synchronous GPU query (`getParameter`, `getProgramParameter`, `getShaderParameter`) can stop the main thread for hundreds of milliseconds while the browser starts a service. Make them only where needed, never just for a log line, and don't block the main thread while the WebAssembly downloads: the browser feeds its streaming compile from that thread.
- **The page needs almost nothing.** The build adds the viewport and web app tags, styles, import map, module preloads, launch screen, and canvas. A page, if there is one, holds only what's about the game (`<title>`, description, icons). A tag the page has wins over the generated one; its own `<canvas>` replaces the generated one (set `WebGLGameEngine.Options.CanvasElementId` to match). Without an `index.html`, the title comes from `GameTitle` or the assembly name. `GenerateWebPage=false` turns generation off.
- **Fast startup.** Mark what the first frame needs with `Preload="true"`; while developing on `localhost`, the console lists anything loaded before the first frame that isn't marked, and warns about anything marked that the first frame didn't load. Don't preload what only some players need. Start other work after `FirstFrameDrawn`.
- Publish: `dotnet publish MyGame/MyGame.WebGL -c Release -o publish`. Every content file and framework script gets a content-hash fingerprint in its name, so content can be served with immutable cache headers.
- **Hosting.** A publish writes a Brotli `.br` copy beside each file the page fetches, and the page fetches the copies. The host must serve `*.br` files as they are with `Content-Encoding: br`. A host that isn't set up makes the game stop with an error naming the file, on purpose.

  | Host | Setup |
  |---|---|
  | Netlify | Nothing: the publish adds the rule to `_headers` |
  | nginx | `location ~ \.br$ { add_header Content-Encoding br; types { } default_type application/octet-stream; }` |
  | Apache | `<FilesMatch "\.br$">` with `Header set Content-Encoding br` and `ForceType application/octet-stream` |
  | Cloudflare Workers | A Worker must serve the `.br` requests (static assets re-compress them); see `docs/guide/platforms.md` |
  | No header control (itch.io, GitHub Pages) | `<DotGameWebDecompressionFallback>true</DotGameWebDecompressionFallback>`: gzip copies the page decodes itself (slower) |

  `<CompressionEnabled>false</CompressionEnabled>` fetches every file as is. `dotnet run` writes no copies, so only published builds need the host set up.
- Audio waits for the player's first gesture: music started earlier plays after it; a sound played before it is dropped.
- Web games can be installed as progressive web apps.

## Android

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

- `ConfigurationChanges = AndroidGameEngine.ConfigurationChanges` stops rotation and similar changes from restarting the activity.
- The theme is the generated `DotGame.LaunchScreen` (or one derived from it). `LaunchScreenAndroidThemeParent` sets what it derives from (default `@android:style/Theme`; the template uses `@android:style/Theme.DeviceDefault.NoActionBar`).
- The template's Release settings: `AndroidStripNativeLibraries=true`. Not `RunAOTCompilation`: Android runs on CoreCLR since .NET 11, which refuses it (XA1044). Version with `ApplicationVersion` (an integer) and `ApplicationDisplayVersion`.
- `AndroidGameEngine.Options.ExitProcessOnFinish` is the one engine option.

## iOS

iOS builds need a Mac with Xcode. The game runs in a window a scene delegate makes:

```csharp
[Export("scene:willConnectToSession:options:")]
public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
{
    if (scene is not UIWindowScene windowScene)
    {
        return;
    }

    this.Window = new UIWindow(windowScene);
    var options = new iOSGameEngine.Options { AudioCategory = AudioCategory.Ambient };
    new iOSGameEngine(this.Window, options).Run(new MainGame());
}
```

- `AudioCategory` (`Ambient`, `SoloAmbient`, `Playback`) decides how audio mixes with other apps and whether the silent switch mutes it. Other options: `AutoHidesHomeIndicator`, `DeferredSystemGestureEdges`.
- The app's `Info.plist` `UILaunchStoryboardName` is `LaunchScreen`; the build generates that storyboard.
- `Info.plist` declares the scene in `UIApplicationSceneManifest` (one `UIWindowSceneSessionRoleApplication` configuration whose `UISceneDelegateClassName` is the `[Register]` name of the scene delegate), as the template's does: built with the iOS 27 SDK, an app without it fails with MT7199.
- iOS is fully ahead-of-time compiled: code that generates code at run time doesn't work. Run a Release build on a simulator or device early, since Debug runs on the interpreter and hides such failures.
- Lossy sounds and music become AAC with macOS's encoder, so iOS content builds only on a Mac (or use `LossyFormat="Opus"`).
- A ProMotion iPhone needs `CADisableMinimumFrameDurationOnPhone` in `Info.plist` to go above 60 fps.

## Frame rates

`this.PreferredFrameRate = 60;` asks for a rate in shared code; `null` returns to the display's own. Desktop and web draw at most that rate; Android (11 and later) and iOS ask the system, which has the last word.

## The launch screen

One item describes it, and the build makes each platform's form (Android theme and splash, iOS storyboard, desktop textures, web markup and manifest colors):

```xml
<LaunchScreen Include="Content\LaunchScreen\logo.png" Background="#181c28" LogoSize="140" />
```

- `Background` is required. `LogoSize` is the logo's longer side in dp (points on iOS, CSS pixels on the web; default 160). `Branding` adds an optional mark near the bottom, in a 200 x 80 dp area; `BrandingWidth` narrows it.
- One `LaunchScreen` item per game. The Android build warns `LSC0002` when the logo doesn't fit the circle Android 12's splash screen shows, with the largest `LogoSize` that fits; other platforms show the whole logo, so their heads can set a larger `LogoSize`.
- It fades out once the first frame is on screen; games write no launch code. Clear the first frames to the same color to continue the look.

## Platform differences and saving games

| | Desktop | Web | Android | iOS |
|---|---|---|---|---|
| Threads | Many | One | Many | Many |
| Device can be lost | No | Yes | Yes | No |
| `File.*` lasts | Yes | No | Yes | Yes |
| Audio needs a gesture first | No | Yes | No | No |

- Save with `System.IO` under `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` on desktop, Android, and iOS.
- On the web, `File.*` writes to memory and is gone on reload, with no error. Save through the browser (local storage or the origin-private file system over JavaScript interop).
- Lost device: textures and shaders loaded through content are restored, but render-texture contents are gone; redraw them on `Graphics.DeviceReset`.

## Trimming, AOT, and what the packages set

- The framework is marked AOT-compatible. Web Release builds use AOT; iOS is always AOT. The template's `Directory.Build.props` trims Release builds (`PublishTrimmed`, `TrimMode=full`, plus `EventSourceSupport`, `UseSystemResourceKeys`, `MetadataUpdaterSupport`, `HttpActivityPropagationSupport` off) and sets `InvariantGlobalization` and `InvariantTimezone`.
- Only a Release publish runs the trimmer. Treat trim and AOT warnings (IL2xxx, IL3xxx) as real: the code they point at can fail only in a published build. Avoid reflection and run-time code generation in game code; use source generators (System.Text.Json's, for example) instead.
- The packages don't impose a code policy on games: no banned APIs, and warnings aren't made errors. What they do set: the platform (`DotGamePlatform`), the feature switches below, `PublishReferencesDocumentationFiles=false`, and on iOS `DynamicCodeSupport=false` with the `managed-static` registrar (without it, an iOS Release build crashes at launch with `ExecutionEngineException`). Don't override those two.
- The `DotGame.Framework` package includes analyzers that report errors; see `build-errors.md`.

## Feature switches

Runtime switches, set as MSBuild properties, that the trimmer treats as constants (code behind a switch that's off is removed from trimmed builds):

| Property | Turns on | Default |
|---|---|---|
| `DotGameDebug` | The framework's self-checks, fatal audio failures, a debugger break on fatal errors, a desktop window that doesn't remember its place | `true` in Debug |
| `DotGameTraceInput` | Traces of input events | As `DotGameDebug` |
| `DotGameTraceAudio` | Traces of sounds and music | As `DotGameDebug` |

To chase an audio problem in Release: `dotnet build -c Release -p:DotGameTraceAudio=true`.
