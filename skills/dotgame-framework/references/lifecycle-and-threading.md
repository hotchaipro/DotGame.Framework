# Lifecycle, threading, and time

## Contents

- Startup
- A frame
- The window changing
- Active, inactive, and hidden
- Threads and `async`
- Time
- Content loaders and threads
- Diagnostics

## Startup

A game derives from `Game`. The platform calls its methods on one thread, in the same order on every platform:

| Order | Method | Use it to |
|---|---|---|
| 1 | Constructor | Set fields and settings such as `FixedUpdateRate`. Services don't exist yet. |
| 2 | `OnInitialize` | Load what the launch screen should wait for; register content loaders |
| 3 | `OnStart` | Load content and set up the game |
| 4 | First frame | The launch screen fades out once it's on screen |
| 5 | `FirstFrameDrawn` completes | Start anything the first frame doesn't need |

- Nothing updates or draws until `OnStart` finishes, so everything awaited in it is ready for the first frame. No platform blocks on startup: the app stays responsive while it loads.
- A startup step that throws is a fatal error.
- Work the first frame doesn't need (sign-in, sync, analytics, ads consent) goes after `FirstFrameDrawn`:

```csharp
protected override async Task OnStart()
{
    this.texture = await this.Content.LoadAsync<Texture>("Textures/bear");
    _ = this.SignInAfterFirstFrame();
}

private async Task SignInAfterFirstFrame()
{
    await this.FirstFrameDrawn;
    // Sign in here. Catch what you expect: nobody observes this task's exceptions.
}
```

- Anything in `OnStart` that could wait on the network or the player holds the launch screen up. Cap it: `await Task.WhenAny(work, Task.Delay(timeout))`.

## A frame

Each frame runs, in order, and the next doesn't start until they finish:

1. The clock advances (`Time` holds this frame's times).
2. Input: `OnPointerEvent`, `OnKeyEvent`, `OnGamepadEvent` for each event since the last frame, then text input. Override `HandlePointerEvents`, `HandleKeyEvents`, or `HandleGamepadEvents` to take a whole frame's events at once (gestures).
3. Continuations: code after an `await` that's ready to continue.
4. `OnFixedUpdate`, zero or more times, only if `FixedUpdateRate` is set.
5. `OnUpdate`.
6. `OnDraw`.

Draw calls only record work; when it reaches the GPU isn't promised, so anything that reads back from the GPU (`Graphics.ReadPixelsAsync`) is async.

Pointer events cover touch, mouse, and pen alike, so the same input code works everywhere.

## The window changing

`ClientBounds` is the game's area in pixels; `SafeAreaBounds` is the part clear of notches, rounded corners, and system bars. Both can change at any time (resize, rotation); `OnClientBoundsChanged` reports it. Keep controls inside `SafeAreaBounds`.

## Active, inactive, and hidden

| State | When | Game time, `OnUpdate` | `OnDraw` | Continuations | Music |
|---|---|---|---|---|---|
| Active | In front with focus | Run | Runs | Run | Plays |
| Inactive | Visible but lost focus or covered | Stop | Runs | Run | Plays |
| Hidden | Minimized, backgrounded, hidden page | Stop | Stops | Run | Paused, resumed on return |

- `OnPause` runs on the way out of active, `OnResume` on the way back.
- **Save in `OnPause`.** `OnStop` isn't guaranteed to run on a phone.
- **Don't await game time in `OnPause`**: it has stopped, so `Time.DelayAsync` never finishes. Other awaited work carries on as far as the platform allows (a few seconds on iOS and on a hidden web page).
- Presses are canceled for you on pause: down pointers get a `Canceled` event and held keys are released.
- `RunWhenInactive = true` keeps updating while inactive. A hidden game never draws.
- `OnLowMemory`: release what you can load again.

## Threads and `async`

- **Game code runs on the game thread.** `await` comes back to it through the framework's synchronization context, so game state needs no locks.
- **Framework APIs are game-thread-only** unless their docs say otherwise. `GameThread.IsCurrent` tells you where you are; `GameThread.VerifyCurrent(tag)` fails fast on misuse.
- **CPU-heavy work** goes in `Task.Run`, which must not touch framework or game state. Awaiting it returns to the game thread:

```csharp
private async Task Solve()
{
    var solution = await Task.Run(() => ComputeSolution());
    this.solution = solution;   // back on the game thread
}
```

- **From another thread** (a store or SDK callback), hop with `GameThread.InvokeAsync(...)` or `GameThread.Post(...)`. There's deliberately no synchronous wait: it would deadlock when the game thread is waiting on the caller.

```csharp
private async Task OnPurchaseCompleted()
{
    await GameThread.InvokeAsync(() => this.score += 100);
}
```

- **The web has one thread.** `Task.Run` queues on the game thread there, so long work still holds up frames. Split it with `await this.Time.NextFrameAsync()`.
- **Continuations share each frame's time.** Each frame runs as many as it has time for (at least one), spreading a burst over frames. During startup they all run with no limit.
- **Fire and forget** (`_ = Method();`) loses exceptions in the task. Catch what you expect inside the method.

## Time

`Game.Time` is game time: it stops while the game is paused or hidden.

| Member | Meaning |
|---|---|
| `DeltaSeconds` / `Delta` | This update's game time (float seconds / `TimeSpan`) |
| `Total` | All game time so far |
| `UnscaledDeltaSeconds`, `UnscaledDelta`, `UnscaledTotal` | The same, ignoring `Scale` |
| `Scale` | 1 real time, 0.5 half speed, 0 stopped (a pause menu); applies to delays and fixed steps |
| `FixedAlpha` | In `OnDraw`, how far toward the next fixed step; throws when fixed steps are off |

- One frame advances game time by at most a third of a second, so a stall slows the game rather than jumping it.
- Fixed steps: set `FixedUpdateRate` (usually in the constructor) and override `OnFixedUpdate`; `DeltaSeconds` is the step length there. Interpolate in `OnDraw` with `FixedAlpha`. An `OnFixedUpdate` override with no rate set anywhere is build error DG0005.
- Delays use game time and run on the game thread: `Time.Delay(span, action)` returns a `DelayHandle` (`Cancel()`); `await Time.DelayAsync(span, token)`; `await Time.NextFrameAsync()`.
- `PreferredFrameRate` (null for the display's rate) is a preference; Android 11+ and iOS ask the system, which decides.

## Content loaders and threads

Content loads on background threads once the game has started (on the web, the one thread). A custom `IContentLoader<T>`:

- may be called on the game thread or a worker, and assumes neither;
- parses and decodes inline, without `Task.Run` (the content service already chose the thread);
- does anything needing the game thread, such as creating a texture, through `GameThread.InvokeAsync(...)`, which just runs it when already there;
- uses `ConfigureAwait(false)` on awaited I/O.

See `content.md` for the loader code.

## Diagnostics

- `Trace.Write`, `WriteWarning`, `WriteError`, `WriteDebug`, `WriteVerbose` each take a unique `uint` tag first. `Trace.MinLevel` filters; `Trace.Writer` takes an `ITraceWriter` to send output elsewhere.
- `exception.WithTag(0x...)` tags a thrown exception so its origin is known without symbols.
- `Trace.FatalException` and `Trace.NonFatalException` are static events for a crash reporter; they can be raised on any thread, so handlers must be thread-safe and quick.
- `Diagnostics.StatsOverlayMode` (`Off`, `FpsOnly`, `Detailed`) shows FPS, update and draw time, draw calls, and sprites per frame.
- `Diagnostics.MarkStartupMilestone(name, detail)` marks startup milestones in every build; on the web they're also performance marks.
- Desktop: `DesktopGameEngine.Options.GraphicsDebug = true` turns on the GPU's debug mode (slow; never ship it).
