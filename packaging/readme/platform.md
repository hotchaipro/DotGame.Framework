## Getting started

Add this package to the game's @PLATFORM@ project from NuGet. It brings the framework and the
content pipeline with it. Nothing else needs installing: on the first build, the pipeline fetches the
content tools for the machine it builds on (win-x64, win-arm64, osx-arm64, linux-x64 or linux-arm64)
through NuGet, once per version.

A game derives from `Game` and overrides what it needs: `OnStart` to load content, `OnUpdate` and
`OnDraw` every frame, and the input and lifecycle callbacks. The platform's engine runs it:

```csharp
@SNIPPET@
```

Content goes in the project as items, and the build turns it into the platform's formats:

```xml
<ItemGroup>
  <TextureContent Include="Content\Textures\*.png" />
  <SoundContent Include="Content\Sound\*.wav" />
  <MusicContent Include="Content\Music\*.wav" />
  <ShaderContent Include="Content\Shaders\*.slang" />
  <DataContent Include="Content\Data\*.*" />
</ItemGroup>
```

Sounds and music can be WAV, FLAC or Ogg Opus; textures are PNG; shaders are Slang. Put a game's
shared code in a class library that references `DotGame.Framework`.

## Building on CI

A job that restores and builds online needs nothing more. To build offline, cache NuGet's packages
folder after a build, or fetch the tools after restoring:

```bash
dotnet restore
dotnet package download DotGame.Framework.Build.Tools.linux-x64@@VERSION@ -o ~/.nuget/packages
```
