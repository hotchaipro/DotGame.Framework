# DotGame framework sample

A tour of the framework, using nothing else: one page for each part of it.
Start with the page for what you need: each is a small, self-contained example
with comments on why, not just what.

| Page | What it shows |
|---|---|
| Sprites | Textures and source rectangles, color and opacity, rotation, scale and flipping; dragging, pinching and the mouse wheel |
| Shaders | A custom pixel shader with a parameter, and per-sprite shader data, so differently set sprites share one draw |
| Render textures and clipping | Drawing into a render texture and reusing it, clearing, the scissor rectangle, reading pixels back and showing them as a texture |
| Input | Pointers (mouse, touch, pen), keyboard events and state, and text entry with the platform's editor |
| Audio | Sounds and voices, music with a loop, preparing music, and volume controls and fades |
| Time, content and diagnostics | Delays on game time, content bundles and data files, the stats overlay, tracing, and the window and lifecycle |

It runs on every platform: `desktop` (Windows, macOS and Linux), `webgl`,
`android` and `ios`. The game code is a shared project in `shared`, which each
platform's project compiles in; a platform's project holds only its entry point
and the content it builds. They're laid out as a game's are, but build the
framework from its source where a game uses its packages: `Directory.Build.targets`
imports the framework's build files, which add the framework for each platform.

```sh
dotnet run --project sample/desktop/DotGame.Framework.Sample.Desktop.csproj
```

Or open `DotGame.Framework.slnx`, which has the sample under `sample`. Move between pages with the arrows at the
top, Tab and Shift+Tab, or the number keys; F1 cycles the stats overlay.

## Layout

```
shared/              The game's code, shared by every platform (DotGame.Framework.Sample.shproj)
  FrameworkSample.cs The game: loads content, switches pages, handles the lifecycle
  SamplePage.cs      What a page is
  PixelText.cs       Text from a texture of character cells (the framework doesn't draw text)
  SampleOptions.cs   What every platform asks of its engine (multisampling)
  Pages/             One file per page
Content/             Textures, sounds, music, a shader and a data file
desktop/ webgl/ android/ ios/
```

The content items in each platform's project show how each kind of content is
built: `TextureContent`, `SoundContent`, `MusicContent`, `ShaderContent` and
`DataContent`.

The art and sounds are made by `Content/make-content.py`: the sprite sheet from the sample's character
(`Content/Source/character.png`), the rest procedurally, so they're under this repository's license, as
the character is.
`Content/Textures/ascii.png` is rendered from JetBrains Mono
Medium (Copyright 2020 The JetBrains Mono Project Authors), licensed under the SIL Open Font License 1.1.
[Content/CREDITS.md](Content/CREDITS.md) lists where each file comes from.
