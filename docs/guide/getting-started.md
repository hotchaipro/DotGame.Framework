# Getting started

This guide takes you from nothing to a game that draws a sprite and moves it, in one project for
the desktop, the quickest platform to run. Everything in it carries over to the web, Android and iOS:
[Multiple platforms](multiple-platforms.md) shares the same game between all four.

## What you need

- The [.NET 11 SDK](https://dotnet.microsoft.com/download). That's all for the desktop: the tools
  that build your textures, sounds and shaders come with DotGame's packages.

## Create your game

A game is an ordinary .NET project with DotGame's package for its platform:

```sh
dotnet new console -n MyGame
cd MyGame
dotnet add package DotGame.Framework.Desktop --prerelease
```

The package brings the framework, the desktop platform and the content pipeline. In `MyGame.csproj`,
change `<OutputType>Exe</OutputType>` to `WinExe`, so that Windows opens no console beside the game.

### The content

Make a `Content` folder in the project and put a picture in it, at `Content/Textures/ball.png`. That's
all: the content pipeline builds the pictures, sounds, music and shaders it finds under `Content` into
each platform's formats as part of the build. A sound is a WAV in `Content/Sound`; music, a WAV or FLAC
in `Content/Music`.

```
MyGame/
  MyGame.csproj
  Program.cs
  MainGame.cs
  Content/
    Textures/ball.png
```

Each file is an MSBuild item, such as `TextureContent`. Declare one yourself to give it settings, or to
build files from elsewhere; a file you declare is no longer added for you:

```xml
<ItemGroup>
  <TextureContent Include="Content\Textures\ball.png" Preload="true" />
</ItemGroup>
```

[Content](content.md) and [the content pipeline](content-pipeline.md) have the rest.

### The game

A game derives from [`Game`](../api/Framework/Game.md) and overrides what it needs. `MainGame.cs`:

```csharp
using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;

namespace MyGame;

internal sealed class MainGame : Game
{
    private Texture _ball = null!;
    private Vector2 _position = new(32, 32);
    private Vector2 _velocity = new(180, 120);

    protected override async Task OnStart()
    {
        // Runs once, before the first frame. Content is loaded by its path under Content, with no extension.
        this._ball = await this.Content.LoadAsync<Texture>("Textures/ball");
    }

    protected override void OnUpdate()
    {
        // Runs once a frame. Move by time, so the speed is the same at any frame rate.
        this._position += this._velocity * this.Time.DeltaSeconds;

        // Bounce off the edges
        var bounds = new Vector2(this.ClientBounds.Width, this.ClientBounds.Height) - this._ball.Size;
        if ((this._position.X < 0 && this._velocity.X < 0) || (this._position.X > bounds.X && this._velocity.X > 0))
        {
            this._velocity.X = -this._velocity.X;
        }

        if ((this._position.Y < 0 && this._velocity.Y < 0) || (this._position.Y > bounds.Y && this._velocity.Y > 0))
        {
            this._velocity.Y = -this._velocity.Y;
        }
    }

    protected override void OnPointerEvent(PointerEvent e)
    {
        // Touch, mouse and pen alike
        if (e.Kind == PointerEventKind.Began)
        {
            this._position = e.Position - ((Vector2)this._ball.Size / 2);
        }
    }

    protected override void OnDraw()
    {
        this.Graphics.Clear(Color.FromRgb(0x181C28));

        var spriteBatch = this.Graphics.SharedSpriteBatch;
        using var batch = spriteBatch.Begin();
        spriteBatch.Draw(this._ball, this._position, Color.White);
    }
}
```

Pointer events cover touch, mouse and pen, so the same code works on every platform.

### The entry point

`Program.cs` is the entry point, and the only code that's about the platform. It starts the desktop
engine with the game:

```csharp
using System.Threading.Tasks;
using DotGame.Framework.Desktop;

namespace MyGame;

internal static class Program
{
    public static async Task Main()
    {
        var engine = new DesktopGameEngine(new() { Title = "MyGame" });
        await engine.RunAsync(new MainGame());
    }
}
```

## Run it

```sh
dotnet run
```

A window opens, with a ball bouncing around it. Click anywhere to move the ball there.

## Every platform at once

DotGame's template makes the same game with a project for each platform, sharing its code and content:

```sh
dotnet new install DotGame.Templates
dotnet new dotgame -n MyGame
dotnet run --project MyGame/MyGame.Desktop
```

The web, Android and iOS each need their workload (`dotnet workload install wasm-tools`, `android` or
`ios`). On Linux the web also needs Python 3 ([Platforms](platforms.md#the-web)), Android needs the
Android SDK and a JDK, which one command installs ([Platforms](platforms.md#android)), and iOS builds
on a Mac with Xcode.
[Multiple platforms](multiple-platforms.md) explains how the template's projects share the game.

## Where to go next

- [The game loop](game-loop.md) explains what runs when, which is worth reading early.
- [Graphics](graphics.md), [Input](input.md) and [Audio](audio.md) cover the everyday parts.
- [Multiple platforms](multiple-platforms.md) shares a game between the desktop, the web, Android and
  iOS, and [Platforms](platforms.md) has each platform's entry point, options and publishing.

## Is it a good fit?

DotGame suits a 2D game in C# that should start quickly in a browser, written in code rather
than an editor. It's a framework, not an engine: it has no scene editor, physics or 3D rendering, and
you bring your own architecture.

If you've used MonoGame or XNA, much will feel familiar: you derive from `Game`, override update and
draw methods, and draw with a `SpriteBatch`. The main differences are that lifecycle and loading are
`async`, content is built from MSBuild items in your project, and the web is a first-class target.
