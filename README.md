# DotGame

A lightweight, cross-platform 2D game framework for C# .NET.

## Why DotGame?

DotGame is inspired by MonoGame and XNA, redesigned for modern .NET with full support for
async/await and the Web as a first-class platform.

The redesigned content pipeline is tightly integrated into MSBuild and creates
native assets, enabling each platform to use native loaders and reducing interop overhead
and optimizing startup time.

## Supported platforms

| Platform | Package | Graphics |
|---|---|---|
| Web (WebAssembly) | `DotGame.Framework.WebGL` | WebGL 2 |
| Android | `DotGame.Framework.Android` | OpenGL ES 3 |
| iOS | `DotGame.Framework.iOS` | Metal |
| Windows, macOS, Linux | `DotGame.Framework.Desktop` | SDL3 GPU (Vulkan, Direct3D 12, Metal) |

## Resources

- [Getting started](docs/guide/getting-started.md)
- ["How To" Guides](docs/README.md)
- [Toolkit](https://github.com/hotchaipro/DotGame.Toolkit)
- [API reference](docs/api/README.md)
- [Samples](sample/README.md)
- [Agent skills](skills/README.md)

## Source code

A game is written against one platform-neutral API, and each platform implements that API:

- **[`framework/`](framework)** is the API a game uses (the `DotGame.Framework` package): the game
  loop, graphics, audio, input, content loading and time. It has no platform code of its own.
- **[`platform/`](platform)** has one project for each platform: `desktop` (SDL3), `webgl`,
  `android` and `ios`, plus the code they share (`shared`). Each one is the package a game's
  platform head references.
- **[`pipeline/`](pipeline)** is the content pipeline that runs during the game's build: MSBuild
  items and targets, the MSBuild tasks (`tasks/`), and the build tool (`tool/`) that turns textures,
  sounds, music and shaders into each platform's native formats.
- **[`analyzers/`](analyzers)** has the Roslyn analyzers that ship with the framework, and
  **[`native/`](native)** the prebuilt native libraries (SDL3, spirv2dxil) and the scripts that build
  them.
- **[`sample/`](sample)** is the sample game, with a head for each platform, and
  **[`test/`](test)** has the unit, content pipeline, analyzer and browser tests.
- **[`docs/`](docs)** has the guides, the API reference and the architecture notes, and
  **[`skills/`](skills)** has the agent skills.
- **[`packaging/`](packaging)** builds the NuGet packages and the `dotnet new` template, and
  **[`eng/`](eng)** and **[`tools/`](tools)** hold the repository's build rules and its docs tools.

## Contributing

[CONTRIBUTING](CONTRIBUTING.md) covers building from source, the repository's layout, tests and
pull requests.

## License

[MIT](LICENSE) © Hot Chai Games LLC
