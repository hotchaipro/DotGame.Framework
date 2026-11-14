# Content: loading it and building it

## Contents

- Content keys and loading
- Bundles and who releases what
- Data files
- Your own content types
- Images that arrive at run time
- Content items
- Textures, sounds, and music
- Shaders
- Sharing content between heads
- Tools and the content cache
- Your own content items

## Content keys and loading

Content is loaded through `Game.Content` by its key: the path under `Content/`, with no extension. The file format differs per platform, and game code never sees it. Keys are case-insensitive, and backslashes are treated as forward slashes.

```csharp
protected override async Task OnStart()
{
    var bear = this.Content.LoadAsync<Texture>("Textures/bear");
    var jump = this.Content.LoadAsync<Sound>("Sound/jump");
    await Task.WhenAll(bear, jump);   // start loads together to load faster

    this.texture = await bear;
    this.jump = await jump;
}
```

- Loading a key again returns the same object without a second load. Loading it as a different type throws.
- `Content.TryGet<T>(key, out var value)` finds content only if it's already loaded.
- The framework's own types: `Texture`, `Shader`, `Sound`, `Music`. Other types need their loaders registered (`Content.RegisterLoader`).

## Bundles and who releases what

A `ContentBundle` is a named set that loads (in parallel) and unloads together:

```csharp
private async Task LoadForest()
{
    var bundle = this.Content.Bundles.Add("forest");
    bundle.Add<Texture>("Levels/forest/background");
    bundle.Add<Music>("Levels/forest/theme");
    await bundle.LoadAsync();
}

private Task LeaveForest() => this.Content.Bundles["forest"].UnloadAsync();
```

- Content loaded by key is shared and belongs to the content service. Release it with `Content.UnloadAsync(key)` or by unloading its bundle. A key another loaded bundle uses stays until that bundle unloads too.
- `Dispose` on a texture loaded by key throws.
- What you create is yours to dispose: textures from `Graphics.CreateTexture`, render textures, run-time image textures.

## Data files

`DataContent` items are copied as they are and opened by their path under `Content/`, **with** the extension:

```csharp
using var stream = await this.Content.OpenStreamAsync("Data/notes.txt");
```

## Your own content types

A type implements `IContent` (`IsDisposed`, `Dispose`); a loader implements `IContentLoader<T>`:

```csharp
internal sealed class LevelLoader : IContentLoader<LevelData>
{
    private readonly ContentService content;

    public LevelLoader(ContentService content)
    {
        this.content = content;
    }

    public async Task<LevelData> LoadAsync(string contentKey)
    {
        using var stream = await this.content.OpenStreamAsync(contentKey + ".txt").ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        return new LevelData(text.Split('\n'));
    }
}
```

Register it in `OnInitialize` or `OnStart` with `this.Content.RegisterLoader(new LevelLoader(this.Content))`. A loader may run on either thread: decode inline (no `Task.Run`), wrap game-thread work such as `Graphics.CreateTexture` in `GameThread.InvokeAsync`, and use `ConfigureAwait(false)` on I/O.

## Images that arrive at run time

Screenshots and images from a server or the player are decoded by DotGame.Imaging (no dependencies; the same pixels on every platform): `PngCodec` (`DotGame.Imaging.Png`), `JpegCodec` (`DotGame.Imaging.Jpeg`), and `WebPCodec` (`DotGame.Imaging.WebP`) each decode and encode their format, and `ImageDecoder` decodes any of them, telling which from the bytes.

- Decoding yourself: `ImageDecoder.TryGetDecodedSize(...)`, then `ImageDecoder.Decode(..., premultiplyAlpha: true)` into a `Color32[]`, then `Graphics.CreateTexture(size, pixels)`.
- A damaged image throws `InvalidDataException`; an unsupported valid one (arithmetic-coded JPEG, animated WebP) throws `NotSupportedException`.
- Saving a screenshot: draw to a `RenderTexture`, `await Graphics.ReadPixelsAsync(renderTexture, pixels)` (RGBA, premultiplied, top row first), then `PngCodec.Encode(..., unpremultiplyAlpha: true)`.
- A game that ships the JPEG decoder credits the Independent JPEG Group (see DotGame.Imaging's THIRD-PARTY-NOTICES.md). Trimming removes decoders a game never calls, but `ImageDecoder` refers to all of them.

## Content items

Content is declared as MSBuild items in the head (or a `.props` it imports), processed for the platform, and written to `$(OutDir)Content/` (or `wwwroot/Content/` on the web). There's no separate content project.

```xml
<ItemGroup>
    <TextureContent Include="Content\Textures\*.png" />
    <SoundContent Include="Content\Sound\*.wav" />
    <MusicContent Include="Content\Music\*.flac" />
    <ShaderContent Include="Content\Shaders\*.slang" />
    <DataContent Include="Content\Data\*.json" />
</ItemGroup>
```

| Item | Source | Output |
|---|---|---|
| `TextureContent` | PNG (any color type and bit depth) or JPEG (baseline or progressive) | Premultiplied alpha. WebP; KTX2 on desktop |
| `ShaderContent` | `.slang` | SPIR-V, MSL, and DXIL on desktop; MSL on iOS; GLSL ES 3.0 on the web and Android |
| `SoundContent` | WAV, FLAC, or Ogg Opus | Lossless 16-bit WAV (or FLAC), so it plays with no delay |
| `MusicContent` | WAV, FLAC, or Ogg Opus | Streaming: Ogg Opus at 48 kHz on the web, desktop, and Android; AAC on iOS |
| `DataContent` | Anything | Copied as is; its key keeps its extension |
| `LaunchScreen` | PNG logo | Each platform's launch screen (see `platforms-and-publishing.md`) |

Common metadata:

| Metadata | On | Meaning |
|---|---|---|
| `Link` | All | Output path and name. On the web it's the full path, starting `wwwroot\` |
| `Compression` | Textures, sound banks | `Lossless` (default) or `Lossy` |
| `LossyQuality` | Textures, music, sound banks | 1-10 |
| `GpuCompression` | Textures | `true`: KTX2 of ASTC blocks on Android and iOS |
| `LosslessFormat` | Sounds, sound banks | `FLAC` (default; `Default` is the same) or `WAV` |
| `LossyFormat` | Music, lossy sound banks | `Opus` (Opus on iOS too), or `Default` |
| `Preload` | All | `true`: on the web, start downloading it with the page |

Two items can't share a key: `foo.png` and `foo.jpg` side by side is a build error. Rename one or give one a `Link`.

## Textures, sounds, and music

- `GpuCompression="true"` makes Android and iOS textures ASTC, which the GPU samples compressed: a quarter of the memory or less, but larger downloads. `LossyQuality` picks the block size (8-10 is 4x4, 5-7 is 6x6, 1-4 is 8x8). It implies lossy; with `Compression="Lossless"` it's an error. Other platforms use their lossy format. Use it for large, memory-bound art, not pixel art or data textures.
- A PNG with a color profile other than sRGB is used as is and can look duller; the build warns `IMG0001`.
- `SoundContent` is always lossless; `Compression="Lossy"` on it is an error. For lossy audio use `MusicContent`.
- Lossless sounds are FLAC: 45 to 85% of WAV's compressed download and a quarter of its installed size on Android. Decoding costs little (a 111-second bank: 47 ms more than WAV on a Pixel 3a). `LosslessFormat="WAV"` keeps an item WAV.
- Lossy audio on iOS is AAC from macOS's encoder, so iOS content builds only on a Mac. `LossyFormat="Opus"` gives iOS Opus in a `.caf` file instead: about 56% of AAC's size, buildable anywhere, but about five times the CPU to decode.
- Safari before 18.4 can't decode Ogg: lossy sounds there stay silent and the game carries on.

## Shaders

Shaders are Slang. Most effects need only a pixel shader; the sprite batch's vertex shader runs before it. The pixel shader receives `float4 Color : COLOR0`, `float2 TextureCoordinate : TEXCOORD0`, and per-sprite `float4 CustomData : TEXCOORD1`.

- Load with `Content.LoadAsync<Shader>(key)`, look parameters up once with `shader.GetParameter("Name")`, and draw in a `using (spriteBatch.UseShader(shader))` scope.
- Textures are premultiplied: divide color by alpha before changing it, and multiply back.
- **Take `ddx`/`ddy` (and implicit-derivative samples) at the top of the pixel shader, and return once at the end.** An early return compiles to a loop in GLSL, where WebGL's derivatives can be zero, so antialiased edges turn hard on the web alone.

## Sharing content between heads

The template keeps content in a `Content` folder at the game's root, and each head declares the items it builds from there, with `LinkBase` naming them in the project (the file's path under the wildcard goes below it, as for the SDK's own items):

```xml
<TextureContent Include="..\Content\Textures\*.png">
    <LinkBase>Content\Textures</LinkBase>
</TextureContent>
```

- The web head's `LinkBase` starts with `wwwroot\` (`wwwroot\Content\Textures`). A web item whose output lands outside `wwwroot` isn't served or published; the build warns, naming the item.
- Each head's items have their own settings (and Visual Studio property pages). Items the heads share can be written once in a props file every head imports, with a property such as `ContentBase` for the part before the folder (the web head sets `wwwroot\Content\` before the import); a setting there changes every platform. The guide's "Multiple platforms" page has both.
- `Link` still names a single file exactly; an item with `Link` ignores `LinkBase`.
- Files under a project's own `Content` folder need no items (images, `.slang`, `.wav`, `.flac`, `.ogg`; `.wav` under `Content\Music` is music). A declared item of any content type removes its files from the defaults, so declare one to give files settings. `EnableDefaultContentItems=false` turns them off.

## Tools and the content cache

- A game's first build restores the build machine's tool package, `DotGame.Framework.Build.Tools.<rid>` (astcenc, slangc, SPIRV-Cross, spirv2dxil), from the game's package sources. Build machines: win-x64, win-arm64, osx-arm64, linux-x64, linux-arm64. Override a tool with `AstcEncExe`, `SlangCExe`, `SpirvCrossExe`, or `Spirv2DxilExe`; `DotGameRestoreTools=false` uses tools from `PATH`. WebP needs no tool.
- `ContentBuildParallelism` sets how many items encode at once (default: half the processors).
- Processed content is cached in `.dotgame/contentcache` at the root of the repository holding the game, shared by every head. The first build writes `.dotgame/.gitignore`. `ContentCacheDir` moves it; `ContentCacheEnabled=false` turns it off.
- Maintain it (not during a build):

```sh
dotnet build -t:PruneContentCache -tl:off                                    # report size and unused entries
dotnet build -t:PruneContentCache -tl:off -p:ContentCacheUnusedDays=90 -p:ContentCacheWhatIf=true
dotnet build -t:PurgeContentCache -tl:off                                    # delete it all
```

- Removing an asset: delete the file and every reference in code. Keys are strings, so a stale one fails at load time, not build time.

## Your own content items

A game or package can add an item type: a target added to `BuildContentDependsOn` that writes the files, a `ContentOutputFile` item for each file it writes (relative to `ContentOutputDir`; anything unlisted is pruned as stale), and, to load by key, a `ContentManifestEntry` added in a target with `BeforeTargets="BuildContentManifest"`. The repository's `docs/guide/content-pipeline.md` has the full example.
