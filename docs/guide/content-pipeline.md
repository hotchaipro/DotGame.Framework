# The content pipeline

The content pipeline builds your assets for each platform as part of a normal `dotnet build`. Content
is MSBuild items, and there's no separate content project or tool to run.

```xml
<ItemGroup>
  <TextureContent Include="Content\Textures\*.png" />
  <SoundContent Include="Content\Sound\*.wav" />
  <MusicContent Include="Content\Music\*.flac" />
  <ShaderContent Include="Content\Shaders\*.slang" />
  <DataContent Include="Content\Data\*.json" />
</ItemGroup>
```

Files under the project's own `Content` folder need no items: images (`.png`, `.jpg`, `.jpeg`, `.webp`)
are `TextureContent`, `.slang` files `ShaderContent`, `.flac` and `.ogg` files `MusicContent`, as is a
`.wav` under `Content\Music`, and any other `.wav` is `SoundContent`. A file that an item you declare
already includes, of any content type, isn't added again, so declaring an item is how a file gets
settings. `<EnableDefaultContentItems>false</EnableDefaultContentItems>` turns the defaults off.

Processed content goes to `$(OutDir)Content/`, or `wwwroot/Content/` for the web. You
[load it](content.md) by its content key: the path relative to `Content/`, without an extension, except
for data, which is opened by its file name, extension included. Two items can't have the same key, so
`foo.png` and `foo.webp` beside each other is a build error: rename one, or give one a `Link`.

## Content types

| Item | Source | Output |
|---|---|---|
| `TextureContent` | PNG (any color type and bit depth), JPEG (baseline or progressive) or WebP (lossy or lossless; not animated) | Premultiplied alpha. WebP, or KTX2 on desktop. See [textures](#textures) |
| `ShaderContent` | `.slang` | SPIR-V, MSL and DXIL on desktop, MSL on iOS, GLSL ES 3.0 on the web and Android. See [Shaders](shaders.md) |
| `SoundContent` | WAV, FLAC or Ogg Opus | Lossless, as 16-bit WAV (or FLAC), so it plays with no delay |
| `MusicContent` | WAV, FLAC or Ogg Opus | A format suited to each platform for streaming: Ogg Opus at 48 kHz on the web, desktop and Android, and AAC on iOS |
| `DataContent` | Anything | Copied as it is. Its key has its extension |

Items can use `Link` to choose their output path and name:

```xml
<TextureContent Include="Art\Bear.png" Link="Content\Textures\bear.png" />
```

In a web project, a `Link` is the full output path, so it starts with `wwwroot\`:

```xml
<TextureContent Include="Art\Bear.png" Link="wwwroot\Content\Textures\bear.png" />
```

Without it the output lands outside `wwwroot`, and isn't published. The build warns you. A web project's
content can also simply be kept in `wwwroot`, which needs no `Link`, and only the processed file is published:

```xml
<TextureContent Include="wwwroot\Content\Textures\*.png" />
```

## Sharing content between projects

A game with a project for each platform keeps one folder of content, and each project includes it from
there, with a `LinkBase` to say where it goes. Each file is named by its path under the wildcard, below
`LinkBase`, as the .NET SDK names its own items:

```xml
<TextureContent Include="..\Content\Textures\*.png">
  <LinkBase>Content\Textures</LinkBase>
</TextureContent>
```

A web project's `LinkBase` starts with `wwwroot\`, as above. [Multiple platforms](multiple-platforms.md)
has the whole layout, and how to write the items once for every project.

## Textures

Textures accept `Compression`, `Lossless` (the default) or `Lossy`, and a quality for it from 1 to 10:
`LosslessQuality` (default 10) or `LossyQuality` (default 8). Music is always lossy and takes
`LossyQuality`. Setting one compression's quality on an item of the other is an error.

`Lossless` keeps every pixel's color close to the source: exact at 10, and at lower qualities it lets
colors change a little where that makes the file smaller, rounding toward the source so it's never
further from it than the quality asked for. It keeps the sharp edges and flat colors that lossy encoding
smears. `LosslessQuality="6"` looked the same as exact on game atlases and cut them by 7-14%. `Lossy`
only has to look right, and is much smaller for photographic art such as backgrounds. Desktop stores
every texture exactly.

With today's WebP encoder, `LosslessQuality` 9 and 10 are exact, 7 and 8 move a channel by 1 level at
most (of 255), 5 and 6 by 2, 3 and 4 by 4, and 1 and 2 by 8.

```xml
<TextureContent Include="Content\Backgrounds\*.png" Compression="Lossy" LossyQuality="7" />
```

Set `GpuCompression="true"` on a texture to make it KTX2 of ASTC blocks on Android and iOS, whose GPUs
sample it while it's compressed. It takes a quarter of the memory or less, but makes for larger
downloads. `LossyQuality` picks the block size: 8-10 is 4x4, 5-7 is 6x6, and 1-4 is 8x8. Other platforms
give such items their lossy format.

A PNG with a color profile other than sRGB is encoded with its colors as they are, which can look
duller than the art did where it was made. The build warns with `IMG0001`.

## Sounds

`SoundContent` is always lossless, and `Compression="Lossy"` on it is an error. For lossy audio,
use `MusicContent`.

Lossless sounds are FLAC: the same samples in 45 to 85% of WAV's compressed download, and a quarter of
its size installed on Android, which stores audio uncompressed. On the web, sounds load after the first frame,
since the browser plays nothing before the player's first tap or key, so they're ready sooner. Elsewhere they
load before it, and decoding costs a little: a 111-second bank took a Pixel 3a 47 ms more than WAV. For a sound
that must be read as it is, `LosslessFormat="WAV"` on its `SoundContent` or `SoundBankContent` item
makes it 16-bit WAV.

Lossy sounds are Ogg Opus at 48 kHz on the web, desktop and Android, and AAC on iOS. AAC
comes from macOS's own encoder, so iOS content builds only on a Mac, as iOS apps do. Safari decodes Ogg
from 18.4: before that a lossy sound stays silent, and the game carries on, as it does without music
there.

`LossyFormat="Opus"` on a lossy `SoundBankContent` or a `MusicContent` makes it Opus on iOS too, in a
Core Audio Format (`.caf`) file that iOS decodes itself, with exact lengths. It's about 56% of AAC's size
at the same `LossyQuality`, and needs no Mac to build, but iOS decodes it with about five times the CPU
of AAC. Elsewhere it changes nothing: Opus is already the format there.

## Speed

Textures, sounds and music encode several at once. Set `ContentBuildParallelism` to choose how many,
which is half the processors by default. In Visual Studio, each item type has its own Properties page.

## Tool paths

The tools come with the packages. A game's first build restores the build machine's tool package,
`DotGame.Framework.Build.Tools.<rid>` (astcenc, Slang's `slangc`, SPIRV-Cross and Mesa's `spirv2dxil`),
from the game's package sources, and later builds use it. To use a specific tool instead, set these
properties, for example in `Directory.Build.props` or with `-p:` on the command line: `AstcEncExe`,
`SlangCExe`, `SpirvCrossExe` and `Spirv2DxilExe`. A tool that isn't there fails the first item that
needs it, with a message naming it.

WebP needs no tool: the build tool encodes it with DotGame.Imaging, whose files are cwebp 1.6.0's at
the same settings. Sounds and music need none either: the build tool reads WAV, FLAC and Ogg Opus
sources, and encodes WAV, FLAC and Opus, with
[DotGame.AudioCodecs](https://github.com/hotchaipro/DotGame.AudioCodecs), the codecs desktop and
Android decode them with, whose Opus files are opusenc's at the same settings. The pipeline signs
`spirv2dxil`'s DXIL itself. The build reads music's duration and
format from the files it writes, with no other tool.

## The content cache

Processed content is cached by its source and settings in `.dotgame/contentcache`, at the root of the
repository that contains your game project. Clean builds and branch switches don't re-encode what
hasn't changed, and every project and platform in the repository shares the cache.

The first build writes a `.dotgame/.gitignore` that ignores everything in the folder, and never rewrites
it. So to share the cache through git, with LFS for example, edit that file and set up the storage you
want. An entry that isn't what was written, such as one not fetched yet, just encodes again.

Builds never trim the cache, but they record the date on each entry they use. To maintain it, run these
targets on your game project, from its directory or by passing its path. `-tl:off` turns off the
terminal logger, which would otherwise hide their output:

```sh
# Report the cache size and how much of it has gone unused
dotnet build -t:PruneContentCache -tl:off

# Preview, then remove, entries no build has used for 90 days
dotnet build -t:PruneContentCache -tl:off -p:ContentCacheUnusedDays=90 -p:ContentCacheWhatIf=true
dotnet build -t:PruneContentCache -tl:off -p:ContentCacheUnusedDays=90

# Delete the whole cache; the next build re-encodes everything
dotnet build -t:PurgeContentCache -tl:off
```

These targets only maintain the cache, and don't build the game. Don't run them during a build.

Set the `ContentCacheDir` property to move the cache, or set `ContentCacheEnabled` to `false` to turn it
off.

## Your own content items

A game or package can add its own item type, such as an atlas or a font. Add a target
that builds your items, and tell the pipeline about what it writes:

```xml
<PropertyGroup>
  <BuildContentDependsOn>$(BuildContentDependsOn);BuildLevels</BuildContentDependsOn>
  <!-- Metadata to copy from manifest entries into the generated manifest -->
  <ContentManifestProperties>Difficulty</ContentManifestProperties>
</PropertyGroup>

<Target Name="BuildLevels" Inputs="@(LevelContent)" Outputs="@(LevelContent->'$(ContentOutputDir)Content\%(Filename).level')">
  <Exec Command="my-level-tool &quot;%(LevelContent.FullPath)&quot; &quot;$(ContentOutputDir)Content\%(LevelContent.Filename).level&quot;" />
</Target>

<ItemGroup>
  <!-- Each file the target writes, relative to ContentOutputDir: anything not listed is pruned as stale -->
  <ContentOutputFile Include="@(LevelContent->'Content\%(Filename).level')" />
</ItemGroup>
```

To load an item by key, add a `ContentManifestEntry` in a target with
`BeforeTargets="BuildContentManifest"`. Its identity is the key's path, and its `RelativePath` metadata
is the file, both relative to `ContentOutputDir`:

```xml
<Target Name="AddLevelManifestEntries" BeforeTargets="BuildContentManifest">
  <ItemGroup>
    <ContentManifestEntry Include="@(LevelContent->'Content\%(Filename)')" RelativePath="Content\%(Filename).level" />
  </ItemGroup>
</Target>
```

To load the result, write [a content loader](content.md#your-own-content-types).

## See also

- [Loading content](content.md)
- [Shaders](shaders.md)
- [Platforms](platforms.md), for preloading on the web
