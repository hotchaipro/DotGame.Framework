---
name: dotgame-framework
description: DotGame, a 2D game framework for .NET (desktop, web/WebGL, Android, iOS). Game lifecycle, the game thread and async rules, content loading and content items (TextureContent, SoundContent, MusicContent, ShaderContent, DataContent), launch screen, publishing, and build errors. Use when writing, building, or debugging a game that uses DotGame.Framework.
---

# DotGame framework

DotGame is a small, platform-neutral 2D framework: a `Game` class with an async lifecycle, `SpriteBatch` drawing, shaders, render textures, audio, input, and content loading, on one game thread. Each platform has a package (`DotGame.Framework.Desktop`, `.WebGL`, `.Android`, `.iOS`), and an MSBuild content pipeline builds assets for that platform during a normal `dotnet build`.

The framework doesn't draw text: it draws sprites, and text comes from a library on top of it.

Read the reference for an area before making non-trivial changes there:

| Area | Read |
|---|---|
| Startup order, a frame, pause/resume/stop, threads and `async`, time, content loaders | `references/lifecycle-and-threading.md` |
| Content keys, bundles, ownership, the content items and their settings, the content cache, shaders | `references/content.md` |
| Each platform's project and entry point, the web page and hosting, Android, iOS, launch screen, saving, trimming and AOT, feature switches | `references/platforms-and-publishing.md` |
| Build and run-time errors and their fixes, the analyzers | `references/build-errors.md` |

The repository's `docs/guide/*.md` and the API reference in `docs/api/` (a page per public type) are the full documentation.

## How a game is set up

The template is the quickest start:

```sh
dotnet new install DotGame.Templates
dotnet new dotgame -n MyGame          # --applicationId com.yourcompany.mygame for Android/iOS
dotnet run --project MyGame/MyGame.Desktop
```

It makes the game's code (`MyGame/MainGame.cs`), its content (`Content/`, beside it), and one small project per platform (a "head"). Each head references its platform package, imports the shared code (a shared project, `MyGame/MyGame.shproj`, whose `MyGame.projitems` lists its files, so Visual Studio adds a new file to it), and declares the content items it builds, so each platform's items have their own settings (and property pages in Visual Studio). `Directory.Build.props` sets `DotGameVersion`, the one version shared by every DotGame package.

```xml
<PackageReference Include="DotGame.Framework.Desktop" Version="$(DotGameVersion)" />

<Import Project="..\MyGame\MyGame.projitems" Label="Shared" />

<TextureContent Include="..\Content\Textures\*.png">
    <LinkBase>Content\Textures</LinkBase>
</TextureContent>
```

`LinkBase` names each item, as the .NET SDK's own items have it: `ball.png` is `Content\Textures\ball.png` (on the web, `wwwroot\Content\Textures`), loaded as `"Textures/ball"`. A file under a head's own `Content` folder needs no item at all: images, shaders, sounds and music there are content by default (`EnableDefaultContentItems=false` turns that off).

- A platform package brings the framework and its content pipeline (`DotGame.Framework.Build`), and the platform is the one whose package the project references.
- Shared game code can also be a class library referencing `DotGame.Framework` alone. A library builds no content: content items go in the heads.
- From source instead of packages, a head imports one include, which detects the platform (`DotGamePlatform`: browser-wasm or the WebAssembly SDK is `webgl`, `-android`/`-ios` target frameworks are `android`/`ios`, anything else `desktop`):

  ```xml
  <Import Project="$(DotGameFrameworkDir)packaging\build\DotGame.Framework.targets" />
  ```

- Prerequisites: the .NET 11 SDK. The web needs `dotnet workload install wasm-tools`, Android `android`, iOS `ios` and a Mac with Xcode. The content tools (astcenc, slangc, SPIRV-Cross, spirv2dxil) are restored from NuGet on the first build.

## A game

```csharp
using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Graphics;

internal sealed class MainGame : Game
{
    private Texture ball = null!;
    private Vector2 position;

    protected override async Task OnStart()
    {
        // Runs once, before the first frame: everything awaited here is ready for it
        this.ball = await this.Content.LoadAsync<Texture>("Textures/ball");
    }

    protected override void OnUpdate()
    {
        this.position.X += 200 * this.Time.DeltaSeconds;
    }

    protected override void OnDraw()
    {
        this.Graphics.Clear(Color.FromRgb(0x181C28));

        var spriteBatch = this.Graphics.SharedSpriteBatch;
        using var batch = spriteBatch.Begin();
        spriteBatch.Draw(this.ball, this.position, Color.White);
    }
}
```

## Rules that matter most

- **One game thread, no locks.** Game code and every framework API run on the game thread (the browser's only thread on the web), unless an API's docs say otherwise. `await` there resumes there. CPU-heavy work goes in `Task.Run` and must not touch framework or game state; from another thread, use `GameThread.InvokeAsync(...)`. There's no blocking wait for the game thread, by design.
- **Nothing updates or draws until `OnStart` completes.** The launch screen stays up until the first frame is on screen, so keep `OnStart` short and cap anything that can wait (network, sign-in). Start work the first frame doesn't need after `await this.FirstFrameDrawn`; on the web, anything started earlier delays the first frame even if nothing awaits it.
- **Save in `OnPause`.** An app can be ended without warning after it, and `OnStop` isn't guaranteed on phones. Don't await game time (`Time.DelayAsync`) in `OnPause`: game time has stopped.
- **Move by time.** Multiply by `Time.DeltaSeconds`. Reusable components take the elapsed time as a parameter rather than reading `Game.Time`.
- **Keep logic out of `OnDraw`.** A hidden game skips it.
- **Content is loaded by key**: its path under `Content/`, with no extension (`"Textures/ball"`). Data files are opened by path with the extension. Content loaded by key belongs to the content service: unload it, don't dispose it.
- **Scopes end with `using`.** `spriteBatch.Begin()`, `UseShader`, `UseRenderTarget`, `UseScissor` return scopes that must be disposed; a discarded one is build error DG0006.
- **Textures are premultiplied alpha**, from the pipeline and from `CreateTexture`.
- **The same code on every platform.** The differences that can't be hidden: the web has one thread, `File.*` writes on the web are lost on reload, the web plays audio only after the player's first gesture, and graphics devices can be lost on the web and Android (rebuild what you drew into render textures on `Graphics.DeviceReset`).
- **Trace with tags.** `Trace.Write(0x5a3c91e7, "...")` takes a random 32-bit number unique to the call site, so a log line identifies its source even in a trimmed build. Generate a new one per call site; never copy one.

## Platform-specific code

Keep it in the head project (entry points, store SDKs, platform options). In shared code, `#if __ANDROID__` and `#if __IOS__` work in those heads, and `OperatingSystem.IsBrowser()` tells the web apart at run time.

## Verifying changes

- Build every head you ship; each platform can fail on its own. `dotnet build MyGame.slnx` builds them all (Android and iOS take minutes; iOS needs a Mac).
- Only a Release publish trims and AOT compiles. Publish the web head (`dotnet publish MyGame/MyGame.WebGL -c Release -o publish`) and run a Release iOS build on a simulator or device early, before shipping.
- Startup, the launch screen hand-off, audio, and input can only be confirmed by running on the platform; say so when reporting.
