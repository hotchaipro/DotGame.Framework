# Contributing

Thanks for helping with DotGame.Framework. Bug reports, fixes and new tests are all welcome.

## Reporting a bug

Open an issue with what you did, what you expected and what happened instead, and the platform it
happened on. For a security problem, see `SECURITY.md` instead.

## Building from source

You need the [.NET 11 SDK](https://dotnet.microsoft.com/download), and for Android or iOS the matching
workload (`dotnet workload install android` or `dotnet workload install ios`). Building for iOS needs
a Mac.

The framework compiles its default sprite shader during the build, and the sample builds textures,
sounds, music and shaders, so install the content pipeline's tools first:

```sh
git clone https://github.com/hotchaipro/DotGame.Framework.git
cd DotGame.Framework
pipeline/install.sh      # macOS and Linux
pipeline\install.cmd     # Windows
```

The scripts install Arm's astcenc (ASTC textures for Android and iOS) and the shader tools (Slang and
SPIRV-Cross). DXIL for Direct3D 12 comes from Mesa's `spirv2dxil`, checked in under
`native/spirv2dxil`. On Linux, unzip, curl and a C++ compiler come from your package manager, and the
tools are installed for the current user into `~/.local/share/DotGame/ContentPipelineTools`, with links
in `~/.local/bin`.

Nothing else is needed to build, test or check the docs. Python, with numpy and Pillow, is needed only
to regenerate the checked-in files that scripts make: the sample's art and sounds
(`sample/Content/make-content.py`) and the tests' fixtures (`test/DotGame.Framework.Tests/FixtureSources`).

Then run the sample:

```sh
dotnet run --project sample/desktop/DotGame.Framework.Sample.Desktop.csproj
```

`DotGame.Framework.slnx` has every project: the framework, its platforms, the content pipeline, the
analyzers, the tests, and the sample for every platform (under `sample`), which builds them from source,
so a change shows in the sample at once. It's a contributor's workspace: a game doesn't see the
framework's projects, only its packages, as `dotnet new dotgame` sets it up.

## Repository layout

```text
framework/     The platform-neutral API (DotGame.Framework)
platform/      The four platforms: desktop (SDL3), webgl, android and ios
pipeline/      The content pipeline: MSBuild items and targets, the default shaders, its MSBuild tasks
               (tasks/) and the build tool that processes content (tool/)
analyzers/     Analyzers that ship with the framework
native/        Prebuilt native libraries (SDL3, spirv2dxil) and the scripts that build them
sample/       The sample, for every platform
test/          Tests
docs/          Guides, architecture notes and the API reference
tools/         The docs' tools: the API reference's generator and the guides' checker (api-docs/)
skills/        Agent skills for games using the framework
packaging/     NuGet packaging: the packages' MSBuild files and READMEs, and the dotnet new template
eng/           The repository's own build rules: build policy, banned APIs, public API lists, versions
```

## Making a change

Build and test with the .NET 11 SDK or later, from `DotGame.Framework.slnx`, which has every project:

```bash
dotnet build DotGame.Framework.slnx
dotnet test --solution test/DotGame.Framework.Tests.slnf
```

`test/DotGame.Framework.Tests.slnf` is the solution's test projects: the sample's apps aren't tests, and
`dotnet test` on the whole solution would ask for an iOS or Android device to run them on.

The build treats every warning as an error, including the code style in `.editorconfig`, so a change
that builds cleanly already follows it. `dotnet format` fixes most style errors.

A few things the framework holds to:

- **The same behavior on every platform:** desktop, web, Android and iOS. When a platform differs
  (startup, stopping, frame timing, the launch surface), make it match the others rather than documenting
  the difference: shared sequencing belongs in `GameHost`, shared code in `platform/shared`.
- **The framework holds only what needs a per-platform implementation.** Cross-platform code belongs in
  a library above it, even when a platform has a native version: images are read and written by
  DotGame.Imaging, not the platforms' codecs, so every platform gets the same pixels.
- **Platforms use only the platform contract.** `framework/Platform` is the whole contract between the
  framework and its platforms, both ways: the strategy interfaces a platform implements, `Strategy<T>`,
  and `GameHost`, which owns the sequencing (`docs/architecture/game-host.md`). The few internal members a
  platform needs carry `[PlatformContract]`; any other use of an internal from a platform is error DG0002.
  To give platforms something new, add it to `GameHost` or a strategy, or mark the member.
- **Drawing calls share one parameter order:** the target and where it goes, then `color`, then the
  optional rest (`rotation`, `origin`, `scale`, then extras such as `sourceRectangle`). A new overload
  keeps that order, so a shorter call is a prefix of a longer one, and adds no overload whose only job is
  a conversion the language does implicitly.
- **Trace tags are unique.** `Trace.Write(0x3a5f8e12, ...)` and `.WithTag(0x...)` take a random 32-bit
  number per call site, so a log line names exactly one place. Generate a new one and check it's unused
  (in PowerShell, `'0x{0:x8}' -f [BitConverter]::ToUInt32([Security.Cryptography.RandomNumberGenerator]::GetBytes(4))`);
  never copy one. Tag `0` is for
  temporary debug output.
- **Each source file starts with the header** `// Copyright (c) <years> Hot Chai Games LLC. Licensed
  under the MIT License.`
- **Trim and AOT safe, with no reflection or LINQ.** The build fails on either (`eng/BannedSymbols.txt`).
- **Each fix comes with a test** that fails without it.
- **Public API changes are discussed first,** in an issue, before a pull request. `eng/api/<project>/`
  lists the public API, and the build fails until a change to it is in `PublicAPI.Unshipped.txt`: the
  analyzer's code fix (or `dotnet format analyzers --diagnostics RS0016`) adds it.
- **The docs follow the code.** `docs/api` is generated from the doc comments: after changing a public
  API, run `dotnet run tools/api-docs/generate.cs` (`-- --check` fails if it's out of date). The
  generator is DotGame.Imaging's, copied: what's particular to this repository is in
  `tools/api-docs/packages.json`, and a change to `generate.cs` itself is made in DotGame.Imaging and
  copied here (CI fails while they differ). After changing a guide,
  `dotnet run tools/api-docs/check-docs.cs` checks its links and compiles its samples; it's
  DotGame.Imaging's too, with this repository's settings in `tools/api-docs/check-docs.json`.

## Native binaries

The native binaries (SDL3, see [native/sdl3/README.md](native/sdl3/README.md)) are prebuilt and
checked in, so they're rebuilt only when a native dependency changes. Usually that's updating the
submodule and pushing to `main`: the [native-sdl3](.github/workflows/native-sdl3.yml) workflow
builds every platform on Windows, Linux and macOS runners and commits the binaries.

To build them locally, initialize the submodules and run [native/build.ps1](native/build.ps1). It
builds for the current machine by default; `-Platform` selects others, including
`android-arm64-v8a` and `android-x86_64` (with `-AndroidNdkRoot`) and `ios-arm64` (on macOS):

```sh
git submodule update --init --recursive
pwsh native/build.ps1
```

## Pull requests

Open a pull request against `main`. By contributing, you agree that your contribution is licensed under
the MIT License, as the rest of the framework is (`LICENSE`).