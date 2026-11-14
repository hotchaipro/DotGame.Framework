# Build and run-time errors

Find the message or code below. Content pipeline messages name the item or file at fault.

## Setup and tools

| Symptom | Cause and fix |
|---|---|
| "The content pipeline has no platform, so it can't build this project's content" | Content items in a project with no platform, such as a class library. Move the items to the heads, or reference a platform package (`DotGame.Framework.Desktop`, `.WebGL`, `.Android`, `.iOS`), or import `packaging\build\DotGame.Framework.targets` from source. |
| `DGBUILD0001`: no content tools for this build machine | Builds run on win-x64, win-arm64, osx-arm64, linux-x64, linux-arm64. Elsewhere, install the tools and set `DotGameRestoreTools=false` (tools from `PATH`, or set `AstcEncExe`, `SlangCExe`, `SpirvCrossExe`, `Spirv2DxilExe`). |
| `DGBUILD0002`: content tools couldn't be restored | The game's package sources (NuGet.config) must reach the DotGame packages, including `DotGame.Framework.Build.Tools.<rid>` at the same version. Fix the sources and restore again. |
| A tool is missing, and the first item that needs it fails naming it | A tool path property points at nothing. Unset it, or point it at a real binary. |
| Workload errors (`NETSDK1147` and similar) | Install the workload: `dotnet workload install wasm-tools`, `android`, or `ios`. iOS needs a Mac with Xcode. |
| A Release publish fails in ILLink with `IL1012` and a `KeyNotFoundException` in `CompilerGeneratedState.MapGeneratedTypeTypeParameters` | An `async` lambda inside a generic method crashes the trimmer. Move the lambda's body into a static `async` helper and call that from a plain lambda (`() => _ = CompleteAsync(action, tcs)`); a generic `async` method with no lambda inside is fine. `-p:PublishTrimmed=false` publishes untrimmed while investigating something else. |
| A build "succeeds" but the change isn't there | Check the output assembly is newer than your edit. A running desktop game locks its executable (MSB3021/MSB3027): close it or build with `-o` to another folder. |

## Content items

| Symptom | Cause and fix |
|---|---|
| Two items with the same key (`foo.png` and `foo.jpg`) | Keys have no extension. Rename one, or give one a `Link`. |
| "Content item ... is output to ..., outside wwwroot, so it won't be served" (warning) | A web item's `Link` is its full output path. Start it with `wwwroot\`, or keep the files under `wwwroot`. |
| `TextureContent` with `GpuCompression='true'` and `Compression='Lossless'` | GPU formats are lossy. Remove `Compression` or set it to `Lossy`. |
| Invalid `Compression`, `PremultiplyAlpha`, or `GpuCompression` | `Compression` is `Lossless` or `Lossy`; the others `true` or `false`. |
| `SoundContent` asks for `Compression='Lossy'` | A standalone sound is always lossless. Use `MusicContent` for lossy audio. |
| `SND0002` | `LosslessFormat` must be `Default`, `FLAC` or `WAV`. |
| `SND0003` | `LossyFormat` must be `Opus`, `Default`, or left out. |
| `IMG0001` (warning) | The PNG has a non-sRGB color profile, which isn't applied. Export it as sRGB. |
| `TEX0001`, `SHD0001`, `SND0001` | A texture, shader, or sound failed to process; the message has the tool's output. For shaders, it's usually a Slang compile error. |
| More than one `LaunchScreen` item; logo or branding not found; no `Background` | One `LaunchScreen` per game, with a real logo path and `Background="#rrggbb"`. |
| `LSC0002` (warning) | The logo is cut off by Android 12's splash circle. Lower `LogoSize` to the number the message gives. |
| iOS content fails on Windows or Linux | Lossy sounds and music for iOS are encoded by macOS. Build iOS on a Mac, or set `LossyFormat="Opus"` on those items. |

## Analyzers (from the DotGame.Framework package; all errors)

| Code | Meaning | Fix |
|---|---|---|
| `DG0004` | A method of an immutable value (such as `Rectangle.Inflate`) returns a new value that's thrown away | Assign the result |
| `DG0005` | `OnFixedUpdate` is overridden but nothing sets `FixedUpdateRate` | Set `FixedUpdateRate` in the game's constructor |
| `DG0006` | A scope (`spriteBatch.Begin()`, `UseShader`, `UseScissor`, `UseRenderTarget`, or any library's `...Scope`) is discarded and never ends | `using var batch = spriteBatch.Begin();` or a `using (...) { }` block |
| `DG0007` | `Color.FromRgb` gets a number that can't be meant: decimal (`FromRgb(255)` is blue, as it reads `0xRRGGBB`), or one that would throw | Write it as `0xRRGGBB`, with alpha as a separate 0-1 value |

## Run time

| Symptom | Cause and fix |
|---|---|
| "No content loader is registered for ..." | Your own types need `Content.RegisterLoader`; a library's types need the call that registers its loaders, in `OnInitialize` or `OnStart`. |
| "Content key ... is already loaded as X and cannot be loaded as Y" | One key, one type. Load it with the type it was first loaded as, or use a different key. |
| A content key isn't found | Use the path under `Content/` with no extension (data files keep it). Check the item's `Link` and the output folder. A stale key from a removed asset only fails at load time. |
| `Dispose` on a texture throws | It was loaded by key and belongs to the content service; call `Content.UnloadAsync(key)`. |
| A call from a background thread fails, or state is corrupted | Framework APIs are game-thread-only. Hop back with `await GameThread.InvokeAsync(...)`. |
| `Time.DelayAsync` never completes in `OnPause` | Game time has stopped. Don't wait on game time there. |
| The web page stops with an error naming a `.br` file | The host doesn't send `Content-Encoding: br`. Set the host up, or use `DotGameWebDecompressionFallback` (see `platforms-and-publishing.md`). |
| The web freezes during long work | The web has one thread; `Task.Run` doesn't help there. Split the work with `await Time.NextFrameAsync()`. |
| Saves vanish on the web after a reload | `File.*` writes to memory there. Save through the browser. |
| Edges or text hard on the web only | A pixel shader takes derivatives after a branch or early return. Compute `ddx`/`ddy` first and return once at the end. |
| iOS Release crashes at launch (`ExecutionEngineException`), Debug works | Run-time code generation. Keep the package's `DynamicCodeSupport=false` and `managed-static` registrar; remove reflection-based code paths. |
| A published (trimmed) build fails where Debug works | Trim or AOT warnings in the build log point at the code. Replace reflection with generated or explicit code. |
| No sound on the web until a tap | Browsers play audio only after a gesture; expected. |
| A render texture is blank after the app returns | The device was lost (web, Android). Redraw on `Graphics.DeviceReset`. |
