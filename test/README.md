# Tests

## Framework: `DotGame.Framework.Tests`

Regression tests for `framework/` and `platform/shared/`, on the desktop .NET runtime. They need no
window, GPU or audio device: `GameHost` is driven as a platform drives it, over fake platform services
(`Platform/TestPlatform.cs`). About 640 cases run in a few seconds.

### Running

```bash
dotnet test --project test/DotGame.Framework.Tests -c Debug
```

```bash
dotnet test --project test/DotGame.Framework.Tests -c Release
```

Run both, since behavior differs on purpose: an audio failure ends a Debug build and is only reported
in Release, and Debug traces input. To run one class, or to get faster output, run the test executable
directly once it's built:

```bash
test/DotGame.Framework.Tests/bin/Debug/net11.0/DotGame.Framework.Tests.exe -class "DotGame.Framework.Tests.Time.TimeServiceTests"
```

To check for flakes, repeat it, ideally with several copies running at once to crowd the thread pool.

### What's covered

| Folder | Under test |
|---|---|
| `Threading/` | `GameSynchronizationContext` (the pump, budget stop, faults, cancellation, `Send`, sessions), `AdaptiveContinuationBudget`, `GameThread` (cancellation, finding C11), and that a worker's continuation doesn't inline on the game thread |
| `Time/` | `TimeService`: the clock, `Delay`, `DelayAsync` |
| `Platform/` | `GameHost`: startup, lifecycle, the order of a frame, `OnClientBoundsChanged` (C21), faults during startup, the launch fade; the fatal path; allocation guards for `GameHost.Tick` and `SpriteBatch.Draw` |
| `Content/` | `ContentService` (a failed load is tried again, C3; sharing, types, disposal), `ContentBundle`, `ContentManifest`, its registry and entries, KTX2 |
| `Audio/` | `MusicStrategyBase` (every order of four operations, with and without waiting, against its retention rules), `AsyncOperationQueue`, music failures, the volume control, Android's mixer |
| `Input/` | `InputService`: pointers and touch counts, the keyboard state, text input |
| `Graphics/` | `SpriteBatch`'s rules, `SpriteVertex`, the ASTC decoder |
| `Values/` | `Color` (HSL and HSV, C16), `Color32`, `Rectangle`, `RectangleInt`, `Angle`, `Vector2Int` |
| `Diagnostics/` | `FatalError`, `NonFatalError`, `StatsCollector` |

### Writing tests here

- **Assert what's correct, not what the code does today.** A test of a known bug is marked
  `[Fact(Skip = "Known issue <id>: ...")]`, with the code review's finding ID, or
  `"Bug found by this suite, <file:line>: ..."`. Fixing the bug means removing the `Skip`: the test is
  already written, and each skipped test has been checked to fail today for the reason it gives.
- **The game thread is one per process, and fatal errors and statistics are static.** A test touching any
  of these goes in `[Collection(nameof(GameThreadTests))]`, which runs alone. Use
  `TestPlatform.OnGameThread`, and `PumpUntil` for work on another thread that needs the pump.
- **A fatal error can't end the test process.** `FatalError.SetUnwindForTests(true)` makes it unwind, as on
  the browser; `NonFatalError.ResetForTests()` forgets which sites have reported. Reset both in `Dispose`.
- **No sleeps.** Wait on a condition, with a bound, so a failure doesn't hang the run. Only a test of the
  continuation budget may spin, and only because that's what it measures.
- **Linked sources:** internals of `platform/` are compiled in by the `.csproj`. Add a file a new test needs
  there. The build policy applies as it does to the framework: no LINQ or reflection (RS0030).
- **Decisions not implemented yet:** nothing pins what `Tick` does while inactive (decision 1), which thread a
  loader runs on (decision 2), or the blocking `InvokeOnGameThread` (decision 6, being removed). Tests near them
  only await, or stay off them. When a decision lands, add its tests.

### Skipped today

| Issue | Where | What's wrong |
|---|---|---|
| C4 | `TimeServiceTests` (2) | A `DelayAsync` from `OnInitialize` or awaited in `OnStart` never completes, silently |
| C6 | `AdaptiveContinuationBudgetTests` | A reported refresh rate faster than the real one pins the budget at its floor |
| C7 | `GameSynchronizationContextTests` (2) | The pump runs what's posted during it, so a self-posting continuation spins or hangs it |
| C9 | `TimeServiceTests` (4) | Delays fire in reverse order; continuations run inside the clock; token registrations leak |
| C10 | `InputServiceTests` | Held keys aren't released on focus loss |
| C11 | `GameThreadApiTests` | The `Action` and `Func<T>` overloads of `GameThread.InvokeAsync` report cancellation as a fault |
| C12 | `GameSynchronizationContextTests` (3) | A post after `Shutdown` runs in the next session; a task queued at `Shutdown` never completes; `Send` doesn't raise `WorkPosted` |
| C13 | `ContentServiceTests` | Concurrent first loads of a key can load it twice and leak the loser |
| C16 (2.4) | `ColorTests` | `Color` to `Color32` wraps rather than clamps |
| C17 | `InputServiceTests`, `GameHostFrameTests` | A canceled touch reports where it began; a touch begun during startup delivers the rest of itself |
| C22 | `InputServiceTests`, `AngleAndVectorTests` | `KeyboardState` isn't a snapshot; `Angle` isn't `IEquatable<Angle>` |
| New | `MusicStrategyTests` (2) | `MusicStrategyBase.cs:592`: `Unprepare` of a playing or paused track re-primes it. The two order walks leave these sequences out until it's fixed: remove `UnpreparesWhilePlaying` with the bug |
| New | `MusicStrategyTests` | `MusicStrategyBase.cs:552`: an acquire finishing after `Dispose` moves the track from `Unloaded` to `Loaded` and releases twice |
| New | `ColorTests` (2) | `Color.cs:453`: `ToHSL` reports HSV's saturation, so colors short of full saturation don't round-trip |
| New | `GameThreadApiTests` | `GameThread.cs:110`: `GameThread.InvokeAsync` throws at the call on the game thread, and faults the task from a worker |
| New | `AngleAndVectorTests` | `Angle.cs:168`: `ToString` depends on the culture |

### What only a device can check

These can't run here, as they need a real platform, its runtime, or wall-clock measurement. Check them by
hand, or in the platform's own harness, when the code near them changes.

**Ending the process**

- `Environment.FailFast` itself, and that the crash reporter gets a managed stack on each platform (the tests
  stop at the unwind).
- A managed exception crossing an Objective-C callback on iOS or a Java callback on Android.
- The web's crash dialog showing once and staying up, and third-party errors not ending the game
  (`test/web` covers the classification, not the page).
- `GameSynchronizationContext.Initialize` failing after its 10-second wait for a real double initialization:
  too slow here, and there's no clock to replace.

**Threads and runtimes**

- An Android same-process relaunch on a real device, with its new render thread starting before the old one has stopped.
  `test/android/lifecycle.sh` runs one on the emulator, and isn't part of the test run, as it needs an emulator or device.
- The WebAssembly runtime clearing `SynchronizationContext.Current` between interop calls, and whether a
  `ConfigureAwait(false)` resumes on the only thread there. `EnsureContext` is only tested against a context
  cleared by hand.
- Whether a generic instance runs interpreted under WebAssembly AOT (the reason for several spans and arrays).
- That a worker's continuation stays off the game thread under Mono on Android, iOS and WebAssembly: the test
  runs on CoreCLR, and the behavior comes from the runtime.

**Frame timing and the continuation budget**

- The measured frame interval, target and budget on a ProMotion iPhone, in Low Power Mode, at a steady 30 fps,
  and in Chrome at 120 and 144 Hz (C6, I10).
- Whether a budget stops a real burst, such as a cold start's shader compiles, without dropping frames or
  starving Android's RenderThread.

**Allocations and GC**

- The allocation guards measure the framework and `SpriteVertex` on CoreCLR. Each platform's own frame path
  isn't covered: desktop display polling (D14), iOS's native wrappers (I12), Android's `AudioTrack` arrays and
  decoder peers (M-15, S-3), and the web's JS garbage (W2).
- GC pauses across a level load (C8), and frame time during a load (C1).
- Draw cost per sprite on WebAssembly AOT, Android and iOS.

**Graphics**

- That each backend's pipeline reads `SpriteVertex` as laid out, draws premultiplied colors correctly, and
  gives the same picture (the layout is tested, not what's drawn).
- A lost GPU context on Android, a failed command buffer on iOS, a lost device on desktop, and a lost WebGL
  context, with resources recovered and the game continuing.
- Real KTX2 and ASTC textures uploaded and sampled.

**Audio**

- Sound actually coming out: every `MusicStrategyBase` test uses a fake output.
- iOS interruptions and music starting once audio comes back (6bb54c2, I1).
- Android audio focus at launch and around ads (M-2, M-3, M-9); the emulator has no audio.
- The ad scene on a real ad: black behind the ad, the music pausing and continuing.
- Sound effects waiting on the music decoder on desktop (D3).

**Input and the window**

- A browser's missing `keyup` after focus loss (C10), and whether a real platform delivers the rest of a
  canceled press.
- Real pointer IDs: negative ones from iOS browsers, and a browser's ever-increasing ones.
- The iOS Safari soft keyboard appearing only inside a trusted gesture; text input through each native editor.
- Late safe area insets in an iOS home-screen app, and refresh rates and densities as each platform reports them.
- Window move and resize on desktop (D6), and the launch screen's fade on each platform.

**Lifecycle**

- The pause rule on each platform once decision 1 is implemented: focus loss, Control Center, a hidden page, split
  screen, and Android's original ANR.
- Android's stop sequence and exit (decision 4), on a real device. `test/android/lifecycle.sh` takes a game through
  home and back, rotation, the screen going off, memory pressure, a kill in the background, the back button and a force
  stop, and checks after each that it's in front and still drawing, with no crash or ANR.

**Content**

- A real network failure during a lazy load on the web, then a retry (C3 is tested against a fake loader).
- Loaders on worker threads once decision 2 is implemented, including Android's upload path.
