# Multiple platforms

A game for several platforms has one project for each, all sharing the same code and content. Each
platform's project, its "head", is small: it references its platform's package, holds the entry point,
and says which content it builds. Everything else is shared. This is how `dotnet new dotgame` lays a
game out:

```
MyGame/
  MyGame.slnx               Every project, for Visual Studio or Rider
  Directory.Build.props     Settings every project shares, such as DotGameVersion
  MyGame/                  The game's code, shared by every platform: a shared project
    MyGame.shproj           (MyGame.projitems lists its files)
    MainGame.cs             The game
  Content/                  Its content, shared by every platform
    Textures/ball.png
    LaunchScreen/logo.png
  MyGame.Desktop/           Windows, macOS and Linux
  MyGame.WebGL/             The web
  MyGame.Android/
  MyGame.iOS/
```

`--applicationId com.yourcompany.mygame` sets the Android application ID and iOS bundle ID, which are
otherwise `com.example.` and the game's name in lower case.

## A head

The desktop's project, `MyGame.Desktop.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <RootNamespace>MyGame</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="DotGame.Framework.Desktop" Version="$(DotGameVersion)" />
  </ItemGroup>

  <!-- The game's code, shared by every platform: a shared project, compiled into each platform's -->
  <Import Project="..\MyGame\MyGame.projitems" Label="Shared" />

  <!-- The game's content, which the content pipeline builds for this platform -->
  <ItemGroup>
    <LaunchScreen Include="..\Content\LaunchScreen\logo.png" Background="#181c28" LogoSize="140" />
    <TextureContent Include="..\Content\Textures\*.png">
      <LinkBase>Content\Textures</LinkBase>
    </TextureContent>
  </ItemGroup>

</Project>
```

The other heads are the same, with their own package, project type and entry point
([Platforms](platforms.md) has each one).

### LinkBase

Content outside a project needs a name inside it, which is what `LinkBase` gives, as it does for the
.NET SDK's own items: each file is named by its path under the `Include`'s wildcard, below `LinkBase`.
So `..\Content\Textures\ball.png` is `Content\Textures\ball.png` in the project, and the game loads it as
`"Textures/ball"`. Visual Studio shows the files in that folder of the project.

The web's content is served from `wwwroot`, so the web head's `LinkBase` starts there:

```xml
<TextureContent Include="..\Content\Textures\*.png">
  <LinkBase>wwwroot\Content\Textures</LinkBase>
</TextureContent>
```

`LinkBase` names a whole folder at once. To name one file differently, give that item a `Link`, its full
path in the project, instead.

### Each platform's settings

Each head declares its own items, so each platform's content has its own settings. A game can preload
its title music on the web only, or build its backgrounds smaller for phones:

```xml
<TextureContent Include="..\Content\Backgrounds\*.png" Compression="Lossy" LossyQuality="6">
  <LinkBase>Content\Backgrounds</LinkBase>
</TextureContent>
```

In Visual Studio, a content file's properties are its settings for the project it's selected in.

## Sharing the content items

When the heads' items are the same but for where they go, they can be written once instead, in a file
that every head imports. A property says where the content goes, and the web head sets it before the
import:

```xml
<!-- Content/MyGame.Content.props -->
<Project>

  <PropertyGroup>
    <ContentBase Condition="'$(ContentBase)' == ''">Content\</ContentBase>
  </PropertyGroup>

  <ItemGroup>
    <TextureContent Include="..\Content\Textures\*.png">
      <LinkBase>$(ContentBase)Textures</LinkBase>
    </TextureContent>
    <MusicContent Include="..\Content\Music\*.flac">
      <LinkBase>$(ContentBase)Music</LinkBase>
    </MusicContent>
  </ItemGroup>

</Project>
```

```xml
<!-- MyGame.WebGL/MyGame.WebGL.csproj -->
<PropertyGroup>
  <ContentBase>wwwroot\Content\</ContentBase>
</PropertyGroup>

<Import Project="..\Content\MyGame.Content.props" />
```

An `Include` is relative to the project, not to the file it's written in, which works here because
every head is one folder below the game's root.

The cost is in the settings: the items belong to the shared file, so a setting changed for one platform,
in Visual Studio too, changes it for all of them. A head can still override one for itself, after the
import:

```xml
<TextureContent Update="..\Content\Textures\title.png" Preload="true" />
```

## Shared code as a library

The template's shared code is a shared project, which each head imports and compiles in: in Visual Studio
it's a project of its own, a file added to it goes into every head, and the game's types can stay
internal. It can be a class library instead, referenced by
every head, as long as it references only `DotGame.Framework` (and libraries built on it),
never a platform package. A library builds no content: the content items stay in the heads.

## See also

- [Platforms](platforms.md): each platform's entry point, options and publishing
- [The content pipeline](content-pipeline.md): every content type and its settings
