# DotGame documentation

DotGame is a cross-platform 2D game framework for .NET. You write a game once in C#, and it
runs on Windows, macOS, Linux, the web, Android and iOS.

New here? Start with [Getting started](guide/getting-started.md). It takes you from an empty folder
to a game drawing a sprite.

## Guides

The guides explain how the parts work and how to use them, with code you can copy.

| Guide | What it covers |
|---|---|
| [Getting started](guide/getting-started.md) | Install the tools and write your first game, for the desktop |
| [Multiple platforms](guide/multiple-platforms.md) | One game for the desktop, the web, Android and iOS: a project for each, sharing the code and content |
| [The game loop](guide/game-loop.md) | Startup, what runs in a frame, pausing and stopping, and threads and `async` |
| [Graphics](guide/graphics.md) | Sprites, colors, transforms, blending, triangles, render textures and clipping |
| [Shaders](guide/shaders.md) | Sprite effects written in Slang, with parameters and per-sprite data |
| [Input](guide/input.md) | Touch, mouse and pen, the keyboard, gamepads and text entry |
| [Audio](guide/audio.md) | Sounds and voices, streaming music with loops, and volume and fades |
| [Time](guide/time.md) | Delta time, slow motion and pausing, fixed steps and delays |
| [Loading content](guide/content.md) | Content keys, bundles, who releases what, data files and your own content types |
| [The content pipeline](guide/content-pipeline.md) | How the build processes textures, sound, music and shaders for each platform |
| [Platforms](guide/platforms.md) | Each platform's project and options, publishing, the launch screen and saving games |
| [Diagnostics](guide/diagnostics.md) | The stats overlay, tracing, crash reporting and startup timing |

## API reference

[The API reference](api/README.md) has a page for every public type and member, generated from the
source code:

| Package | What it's for |
|---|---|
| [Framework](api/Framework/README.md) | The core of every game: lifecycle, graphics, audio, input, content and time |
| [Desktop](api/Desktop/README.md), [Web](api/Web/README.md), [Android](api/Android/README.md), [iOS](api/iOS/README.md) | Running a game on each platform |

To find something fast, press `t` on the repository's GitHub page and type the type's name:
`SpriteBatch.md` takes you straight to it.

## Architecture

For contributors: how parts of the framework work and why they're designed that way.

- [The game host and the platform contract](architecture/game-host.md): how each platform runs a game
- [Threading](architecture/threading.md): the game thread, the worker pool and the continuation pump
- [The sprite batch](architecture/sprite-batch.md): one vertex format, and how each platform batches draws
- [Audio](architecture/audio.md): the audio API, the platform strategies, and running silently without audio
- [The launch screen](architecture/launch-screen.md): one description, built natively for each platform

## The sample

The sample is a small program with a page for each feature, and comments on why as well as what.
It runs on every platform. [The sample](../sample/README.md) covers sprites, shaders, render textures, blending, input,
audio, time and content.

## Changing the documentation

The pages in `api/` are generated from the doc comments in the source, so fix a mistake there by
editing the comment, not the page. After changing a public API, run the generator from the
repository's root:

```sh
dotnet run tools/api-docs/generate.cs           # write docs/api
dotnet run tools/api-docs/generate.cs -- --check    # fail if docs/api is out of date
dotnet run tools/api-docs/generate.cs -- --missing  # list what has no doc comment
dotnet run tools/api-docs/check-docs.cs        # check the links, and compile every code sample in the guides
```
