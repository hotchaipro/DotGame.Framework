# Content pipeline tests

Regression tests for the content pipeline: the build tool (`pipeline/tool/`), the MSBuild tasks
(`pipeline/tasks/`) and the targets (`pipeline/`).

| Project | Covers |
|---|---|
| `test/DotGame.Framework.ContentPipeline.Tests` | Textures, shaders, sound, music, data, the cache, the manifest, pruning |
| `test/DotGame.ContentPipeline.FakeTool` | A stand-in for the encoders and external tools |

A library built on the framework, with a content pipeline of its own, can test it the same way: its
test project links `Support/` and references the fake tool, and tells `Support/Paths.cs` where the
library is through assembly metadata. `DotGameLibraryDir` is its repository,
`DotGameLibraryContentTargets` the targets that `ContentProject.ImportLibrary` imports after the
framework's, and `DotGameLibraryBuildToolProperty` and `DotGameLibraryBuildTool` the property that
names its build tool and the tool's file beside the tests.

## Running

Everything:

```bash
dotnet test --project test/DotGame.Framework.ContentPipeline.Tests
```

Just the fast set (no MSBuild runs and no real tools):

```bash
dotnet test --project test/DotGame.Framework.ContentPipeline.Tests --filter-not-trait "Category=Slow"
```

Or run a test project directly, which is quicker and prints each skip's reason:

```bash
test/DotGame.Framework.ContentPipeline.Tests/bin/Debug/net11.0/DotGame.Framework.ContentPipeline.Tests -trait- "Category=Slow"
```

`Category=Slow` marks every test that starts MSBuild or a real external tool. Slow tests make a
temporary folder under the system's temp folder (`dgpt/`), and remove it afterwards.

## What's in them

- **Unit tests** call the code directly, or run a task with `RecordingBuildEngine`. The build tools
  grant these projects `InternalsVisibleTo`, and `ShaderCompiler` has four members
  made `internal` for them. That's all the production code changed for the tests.
- **Integration tests** run the real targets with the real MSBuild on a temporary project
  (`Support/ContentProject.cs`). It's a plain project, not an SDK one, so a build takes a second or
  so; `SdkProjectTests` checks that an SDK build hooks up the same way. The processor commands point
  at the fake tool, unless a test uses the real ones.
- **Real-tool tests** (`RealToolTests`) use the encoders on `PATH` or in the
  Vulkan SDK, and skip with a reason when one isn't installed. `pipeline/install` installs them.

### The fake tool

`FakeContentTool` writes an output that depends only on its input's bytes and its other options. It
reads its behavior from a `fake-tool.config` it finds by walking up from any path in its arguments,
so each test configures its own folder. `fail.texture=partial` writes half an output and exits with
an error; `exit`, `after-primary` and `hang` are the other failures. It logs each run in `calls.log`,
which is how a test knows an encoder did or didn't run. See its `Program.cs`.

### Timing

No test waits for time to pass. A test that changes a source just writes it, which makes it newer
than outputs a finished build wrote. Tests that kill a build wait for a marker file the fake encoder
writes, and any limit in a test is there to fail a stall, not to time something: limits are generous,
because a machine busy with other builds can starve these low-priority ones for minutes.

### Golden hashes

`Goldens/goldens.txt` beside the project's sources holds `case|identity|hash` lines. The identity is what the
hash holds for: the content type's version in `ContentVersions.props`, and the exact build of the
tools, where their output isn't otherwise stable. So goldens are recorded per machine's tools, and
hash decoded pixels and samples where a container's bytes aren't stable.

- A mismatch means the output changed with no version bump. If that's intended, bump the type's
  version in `ContentVersions.props`, or caches keep serving the old output, and record the new hash.
- An identity not on file skips the test, and says which tool it's about.
- `DOTGAME_UPDATE_GOLDENS=1` records the hashes for the tools at hand. Look at the output first.

## Known issues

A known bug is a test of the correct behavior with `Skip = "Known issue <id>: ..."`, where `<id>`
is the issue's id. When you fix one, remove its `Skip`: the test is your regression test. Don't
change a test to fit a bug. None is open.

P14 (no ffprobe per sound) and P16 (only the audio stream is encoded) are fixed, and these tests
cover them.

These tests deliberately don't pin two things that are decided but not yet done: where the cache
lives by default and whether it's ignored, and the default atlas page limit. The tests always set
`ContentCacheDir` and `TextureAtlasMax*` themselves, so they hold before and after those changes.
