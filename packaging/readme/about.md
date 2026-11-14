## About DotGame

DotGame is a cross-platform 2D game framework for .NET 11: write a game once in C#, and it runs on
Windows, macOS, Linux, the web, Android and iOS. A game's head for each platform references that
platform's package:

| Package | For |
|---|---|
| `DotGame.Framework.Desktop` | Windows, macOS and Linux |
| `DotGame.Framework.WebGL` | the web |
| `DotGame.Framework.Android` | Android |
| `DotGame.Framework.iOS` | iOS |
| `DotGame.Framework` | a game's shared code, in a class library |
| `DotGame.Templates` | `dotnet new dotgame`: a new game with a project for each platform |
| `DotGame.Imaging` | PNG, JPEG and WebP decoding and encoding, for any .NET code |

The others (`DotGame.Framework.Build` and `DotGame.Framework.Build.Tools.<rid>`) come with these;
don't reference them directly. Every DotGame package has the same version, and each
pins the others it needs to it exactly.

This is a preview: the API may change between previews.

MIT. Copyright (c) 2026 Hot Chai Games LLC. Third-party software in a package is listed in its
`THIRD-PARTY-NOTICES.md`.
