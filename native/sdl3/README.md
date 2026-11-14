# SDL3 Native Library

This directory contains the native SDL3 binary used by the
`DotGame.Framework.SDL3` platform project. The pre-built binaries are
checked into the repository so no extra build tools or downloads are needed
— just clone and build the .NET solution.

The binaries are built from source using a git submodule, either locally or by
the `native-sdl3` GitHub workflow.

## Version Compatibility

The SDL3 source code is pinned via a git submodule under `src/`.
The submodule tracks the **latest stable release** that is compatible with the
`SDL3-CS` NuGet managed bindings.

SDL3 uses an [odd/even versioning policy](https://github.com/libsdl-org/SDL/blob/main/docs/README-versions.md):
- **Stable** releases have even minor AND even patch (e.g., 3.4.0, 3.4.2, 3.4.4).
- **Newer patch** releases within the same minor are **backwards-compatible**
  (e.g., native 3.4.4 works with bindings built against 3.4.2).
- **Newer minor** versions are also backwards-compatible
  (e.g., native 3.6.x will work with bindings from 3.4.x).

This means the native SDL3 library can be newer than the `SDL3-CS` NuGet
package version — it just can't be older.

| Component  | Submodule path   | Minimum version (SDL3-CS) | Current |
|------------|------------------|---------------------------|---------|
| SDL3       | `src/SDL`        | 3.4.2                     | 3.4.4   |

## Quick Start

The native binaries are already in the repository. Just build the .NET solution
— no extra steps required.

### Building from source

If you need to rebuild the native library yourself (e.g., for a custom
configuration or a new platform):

#### Prerequisites

- [CMake](https://cmake.org/) 3.16 or later
- [PowerShell](https://github.com/PowerShell/PowerShell) 7+ (cross-platform)
- Platform-specific toolchain (MSVC, GCC/Clang, Xcode, or Android NDK)

#### 1. Initialize submodules (first time only)

```bash
git submodule update --init
```

#### 2. Build for your current platform

```powershell
./native/sdl3/build.ps1
```

This auto-detects your OS and architecture, builds SDL3,
and copies the binary to the correct output directory (e.g., `win-x64/`).

#### 3. Build for a specific platform

```powershell
# Desktop
./native/sdl3/build.ps1 -Platform win-x64
./native/sdl3/build.ps1 -Platform win-arm64
./native/sdl3/build.ps1 -Platform linux-x64
./native/sdl3/build.ps1 -Platform osx-arm64

# Mobile (cross-compilation)
./native/sdl3/build.ps1 -Platform android-arm64-v8a
./native/sdl3/build.ps1 -Platform android-x86_64
./native/sdl3/build.ps1 -Platform ios-arm64
```

Android builds require `ANDROID_NDK_ROOT` to be set (or pass
`-AndroidNdkRoot <path>`). iOS builds require macOS with Xcode.

## GitHub Actions

The workflow at `.github/workflows/native-sdl3.yml` runs `native/build.ps1`
for every platform on the matching runner (Windows, Linux x64/arm64, macOS)
and commits the resulting binaries back to the repository.

It runs on pushes to `main` that change the SDL3 submodule or the build
scripts, and can be run manually from the GitHub Actions UI. Other pushes
don't trigger it. The committed binaries are outside its trigger paths, and
pushes made by the workflow don't trigger workflows, so it doesn't re-run
itself.

## Directory Layout

After building, the directory structure will be:

```
native/sdl3/
  build.ps1                 ← build script (from source)
  update-submodules.ps1     ← update to latest compatible releases
  src/
    SDL/                     ← git submodule (libsdl-org/SDL)
  build/                     ← CMake build trees (gitignored)
  win-x64/
    SDL3.dll
  win-arm64/
    SDL3.dll
  linux-x64/
    libSDL3.so
  linux-arm64/
    libSDL3.so
  osx-x64/
    libSDL3.dylib
  osx-arm64/
    libSDL3.dylib
  android-arm64-v8a/
    libSDL3.so
  android-x86_64/
    libSDL3.so
  ios-arm64/
    libSDL3.a
```

## Updating Native Libraries

### Pick up latest bugfix releases (recommended)

Run the update script to automatically find the latest stable releases
compatible with the current `SDL3-CS` NuGet version:

```powershell
# Preview what would change
./native/sdl3/update-submodules.ps1 -DryRun

# Apply the update
./native/sdl3/update-submodules.ps1
```

Then commit and push the submodule changes:

```powershell
git add native/sdl3/src/SDL
git commit -m "Update SDL3 native library to latest compatible release"
git push
```

The CI workflow will automatically rebuild and commit the updated binaries.
This is safe to do at any time — SDL3's versioning guarantees that newer
stable patch/minor releases are backwards-compatible.

### Upgrade to a new SDL3-CS NuGet version

When upgrading the `SDL3-CS` managed bindings to a new version:

1. Update the `SDL3-CS` NuGet package version in
   `platform/desktop/DotGame.Framework.Desktop.csproj`.
2. Run `./native/sdl3/update-submodules.ps1` (it uses the minimum
   version from the NuGet package — update the `-MinVersion` default
   in the script if needed).
3. Commit and push — CI will rebuild the native binaries automatically.
