# Threading

Game code, the framework and its platforms share one threading model: a single game thread that owns
all framework state, and the .NET thread pool for work that doesn't touch it. `await` on the game
thread comes back to the game thread, a pump the framework owns runs those continuations once a
frame, and content loaders split their work between the two by where they call
`GameThread.InvokeAsync`. The model is the same on every platform, including the single-threaded
web.

Platform hosts drive the game through `GameHost` ([game-host.md](game-host.md)): `BeginStartup`,
`Tick` from every frame callback, `GameHost.DrainContinuations` outside frames, and the lifecycle
methods. `GameHost` owns the sequencing this document describes.

## Roles

| Role | Identity | Owns |
| --- | --- | --- |
| **Game thread** | One thread per process at a time, set by `GameHost.InitializeGameThread`. `GameThread.IsCurrent` is true on it, and `GameSynchronizationContext` is its synchronization context. | All framework and game state: the services, content, the graphics device, the audio service, the content cache. |
| **Worker pool** | The .NET thread pool: anything reached through `Task.Run`, `await ... .ConfigureAwait(false)`, and so on. | Nothing of the framework's. CPU-bound and I/O-bound work only. |

There is deliberately no render thread, loader thread or audio thread at the framework level.
Drawing runs on the game thread; an audio thread is an implementation detail of a platform's audio
service.

Which thread is the game thread depends on the platform:

| Platform | Game thread |
| --- | --- |
| Desktop | The main thread, which runs the SDL event and frame loop |
| WebGL | The browser's main thread, the only one |
| Android | A dedicated render thread (`platform/android/Graphics/RenderThread.cs`) that owns the EGL context and runs a `Looper`; the Android UI thread talks to it only through non-blocking commands |
| iOS | The main thread, where the display link and MetalKit callbacks arrive |

On the web the worker pool is the same thread as the game thread, and the contract still holds.

`InitializeGameThread` is called outside any `async` method, since an async method's builder restores
`SynchronizationContext.Current` when it returns. Only one thread holds the role at a time. Android
can start a new render thread in the same process (a new activity) before the old one has finished
stopping, so `InitializeGameThread` waits up to 10 seconds for the previous thread's `ShutdownGameThread`
before treating a second claim as a fatal error. The browser runtime can drop the synchronization
context between interop calls, so every web entry point calls `GameHost.EnsureGameThreadContext`
first, and the pump restores it after each continuation.

## What games can rely on

Game code runs on one thread, one frame at a time: a frame's time is set once, then its input,
continuations, `OnFixedUpdate`, `OnUpdate` and `OnDraw` run in that order, and the next frame doesn't
start until they've finished.

What isn't promised is when drawing reaches the GPU. A draw call only records the work, so a platform
is free to submit it later, or from another thread, without game code changing. Public APIs don't
promise otherwise: an API that needs a result from the GPU, such as `GraphicsService.ReadPixelsAsync`,
is asynchronous.

Only the game's root reads `Game.Time`. Reusable components take the time or delta as a parameter,
so one can run sub-steps, slow motion or a paused scene under live UI, and can be tested.

## Rules

### Framework APIs

- Game thread only, unless their documentation says otherwise. Calling them from the worker pool is
  a bug.
- Members that any thread may call say so in their XML documentation. `GameThread`'s members are
  the way in from another thread; on the platform side, `GameHost`'s input and low-memory methods.
- `GameThread.VerifyCurrent(tag)` ends the game if the caller isn't on the game thread, or has lost
  its synchronization context. It runs in every build, and the framework uses it at its own entry
  points; game code and libraries built on the framework may use it too.

### Async methods on the game thread

An `await` on the game thread captures `GameSynchronizationContext`, which resumes the continuation
on the game thread. `GameThread.InvokeAsync` called on the game thread runs its body at once,
synchronously; from another thread it posts the body and returns a task that completes with its
outcome.

```csharp
// On a worker thread:
var bytes = await stream.ReadAllAsync().ConfigureAwait(false);
// Still on a worker thread, because of ConfigureAwait(false)
var decoded = Decode(bytes);
// Back to the game thread for framework state
return await GameThread.InvokeAsync(() => CreateTexture(decoded));
```

`ConfigureAwait(false)` belongs on awaits inside background work that doesn't need the game thread.
On the game thread it is harmless but has no effect: the work runs inline.

A body's failure reaches its caller through the task on every thread, including a body that throws on
the game thread, where `InvokeAsync` runs it inline; a cancellation comes back as a canceled task.
`GameThread.Post`, which returns nothing, ends the game if its body throws, as an exception in a
frame does. `GameThread` has no blocking way to run work on the game thread: it would deadlock as
soon as the game thread waited on the worker that was asking.

### The continuation pump

Continuations posted to `GameSynchronizationContext`
([framework/Threading/GameSynchronizationContext.cs](../../framework/Threading/GameSynchronizationContext.cs))
run only when the game thread pumps its queue. The framework owns the pump, and it has two forms:

- **`ExecutePendingContinuations`**, the steady-state pump. It runs once per frame, from each started
  frame of `GameHost.Tick`, under a per-frame time budget. `AdaptiveContinuationBudget`
  ([framework/Threading/AdaptiveContinuationBudget.cs](../../framework/Threading/AdaptiveContinuationBudget.cs))
  tunes it with an additive-increase, multiplicative-decrease controller: the budget grows by 0.5 ms
  while frames have headroom under the display's frame interval and halves when a frame overruns it,
  between 1 ms and half the frame interval (at most 8 ms). Bursts of continuations, such as a cold
  start's shader compiles, are spread across frames instead of dropping one. A positive budget always
  runs at least one continuation, so work always progresses. The pump measures the interval between
  its own calls, so it needs no timing source from the platform.
- **`DrainPendingContinuations`**, the drain, with no budget. `GameHost.Tick` uses it before the game
  has started, to advance `OnInitialize` and `OnStart` as fast as possible, since there is no rendered
  frame to protect; `GameHost.DrainContinuations` exposes it to platforms for pumping outside frames.
  It doesn't touch the budget's timing state, and it never draws or presents, so a long `OnStart`
  advances without a blank frame and without holding the GPU.

A pump runs **only what was queued when it began**. What that work posts waits for the next pump, so
`while (!ready) await Task.Yield();` waits for a frame rather than spinning the budget away. A drain
runs passes until the queue is empty, up to 50 ms, so a chain of hops finishes quickly during startup,
while work that keeps posting itself can't hang a page or a paused app.

A continuation that throws ends the game through `FatalError.Terminate` (an `async void` method's
exception arrives this way); an `OperationCanceledException` is ignored, as cancellation is control
flow.

When the game thread shuts down (`GameHost.ShutdownGameThread`), whatever is still queued is
abandoned: a task waiting on it through `GameThread.InvokeAsync` is canceled, and later posts are
dropped until the next `InitializeGameThread`, so they can't run against a disposed game.

**One pump per frame.** There is exactly one steady-state pump per rendered frame, in `GameHost.Tick`.
Platform loops don't pump again while the game runs: a second, unbudgeted drain would defeat the
budget and let a burst of GPU work monopolize the driver and starve the system's own render thread.
They call `GameHost.DrainContinuations` only outside frames: while a paused or hidden app has no
frames, and during teardown.

**Pumping on demand.** `GameHost.WorkPosted` is called on the posting thread whenever work is posted,
so a platform that has no frames coming can schedule a drain:

| Platform | When | How |
| --- | --- | --- |
| WebGL | Starting, or hidden after it has started | A `MessageChannel` message, which background tabs don't throttle, coalesced so one message serves any number of posts |
| Android | Starting while the window can present (a hop from a worker shouldn't wait for the next vsync), or no frames running | A message on the render thread's `Looper` |

While Android's window can't present during startup, startup advances on delayed `Looper` messages
every 8 ms instead.

**Startup is the same on every platform.** A host calls `BeginStartup` once its graphics device is
ready, then `Tick` from every frame callback. Until the game has started, `Tick` only drains
continuations and discards queued input; it never updates or draws, so nothing animates before the
first frame, and everything awaited in `OnStart` completes before anything is drawn. No host blocks
on startup, so the app stays responsive while the game loads. The game thread is the browser's main
thread on the web, so it can never block and pump there: blocking it would stop the very callbacks
that pump it. The `Tick` model needs no blocking anywhere, which is what lets every platform share
it. Until the game draws its first frame, each platform shows a launch surface (see
[game-host.md](game-host.md#the-launch-screen)).

| Platform | Frames |
| --- | --- |
| Desktop | The SDL event and frame loop, startup included |
| WebGL | `requestAnimationFrame`, and while starting, the startup pump above, since a hidden tab gets no animation frames |
| Android | `Choreographer` frame callbacks on the render thread's `Looper` while the window can present, and delayed messages while it can't during startup (a cold start in the background) |
| iOS | A `CADisplayLink`, which draws the paused `MTKView` once per display refresh |

**Frame time.** Each `Tick` carries the platform's frame timestamp: the expected presentation time
where the platform reports one (iOS `CADisplayLink.TargetTimestamp`, Android `Choreographer` on API
33 and later), otherwise the frame's vsync or start time. `TimeService` measures each frame from
these, limited to a third of a second, and `GameHost` resets the frame clock after starting and
resuming ([game-host.md](game-host.md#the-frame-clock)). Game time runs during startup too, so a delay
awaited in `OnInitialize` or `OnStart` finishes; the code after it resumes in the pump, like code
after any other await. Delays due on one frame run in the order they were due, and for the same time
in the order they were scheduled. What a delayed action schedules waits for the next frame, so an
action that reschedules itself with no delay can't hang one.

**The frame rate the budget tunes against.** Each platform reports the display's refresh rate in
`GameHost.SetDisplay` (`DisplayInfo.RefreshRateHz`), first and whenever it changes: Android from
`Display.RefreshRate`, iOS from the rate its display link delivers (starting from
`UIScreen.MaximumFramesPerSecond`), desktop from the SDL current display mode, and the web as 0, since
browsers don't report it. A rate that isn't positive, or is implausible, falls back to a 60 FPS
target. That is only where the controller starts, since a reported rate is often wrong: a ProMotion
iPhone reports 120 Hz and runs apps at 60 unless they ask for more, Low Power Mode lowers the rate
without saying so, and a game can hold a steady 30. The controller calibrates its target from a low
percentile of the frame intervals it measures, which is what the device delivers when a frame isn't
held up.

### Worker pool

- For file I/O, image and audio decoding, parsing, hashing, compression, and other CPU-bound or
  I/O-bound work that doesn't touch framework state.
- Doesn't call framework APIs that aren't documented as thread-safe: content, graphics, audio,
  input, time, or the game's own state.

## Inactive and hidden

A game is in one of three states, the same on every platform:

| State | When | Game time and `OnUpdate` | `OnDraw` | Continuations | Music |
| --- | --- | --- | --- | --- | --- |
| Active | Has focus, in front | Run | Runs | Run | Plays |
| Inactive | Lost focus or covered, still visible: another window, Control Center, the notification shade, split screen's other side | Stop | Runs, so the picture stays correct | Run | Plays |
| Hidden | Not visible: minimized, in the background, a hidden page | Stop | Stops: no GPU work | Run on demand | Paused, and resumed on return |

`OnPause` runs on the way out of active and `OnResume` on the way back. `Game.RunWhenInactive` keeps
a game updating while inactive (one played on a second monitor, say); a hidden game stops drawing
either way. Android stops presenting on the signals that its window is going away (visibility,
detach, the activity's pause), not on focus loss, and keeps its context current on a small offscreen
surface while it has no window, so game code that runs then has a live context. What each platform
does with a hidden app's continuations is in [game-host.md](game-host.md#lifecycle).

## Content loaders

`IContentLoader<TContent>.LoadAsync(string)`
([framework/Content/IContentLoader.cs](../../framework/Content/IContentLoader.cs)) is the framework's
main multi-threaded entry point. Its contract:

1. **It may be called on the game thread or on a worker thread.** Don't assume either.
   `ContentService.LoadAsync` runs loaders on the calling thread (the game thread) while the game
   starts, and through `Task.Run` on the worker pool once it has started, so a load during play
   doesn't hold up frames. The web, with only one thread, always runs them on the calling thread.
2. **Decoding and parsing run inline**, on the calling thread. A loader doesn't wrap its decode in
   `Task.Run`: `ContentService` has already chosen the thread, and on the web it would only add
   continuations.
3. **Work restricted to the game thread runs there:** GPU resource creation and upload (textures,
   buffers, pipelines), shader compilation where the platform requires the device's thread, and
   attaching to the audio service. The content cache is `ContentService`'s, and loaders don't touch
   it. Wrap the restricted step in `GameThread.InvokeAsync(...)`; on the game thread it runs inline.
   Metal creates and fills textures from any thread, so the iOS texture loader doesn't hop at all.
4. **The returned task may complete on any thread.** A caller that needs the game thread awaits it
   from the game thread, and the synchronization context brings it back.
5. **`ConfigureAwait(false)`** goes on awaited I/O inside a loader, so a loader running on a worker
   doesn't bounce back to the game thread between its own steps. On the game thread it's harmless.

A loader looks like this:

```csharp
public async Task<Texture> LoadAsync(string contentKey)
{
    // Decode: inline on whatever thread called the loader. While the game starts, and on the
    // web, that's the game thread, with no hops; once it has started, a worker.
    var pixels = DecodePng(contentKey);

    // The GPU step: on the game thread, inline if already there
    return await GameThread.InvokeAsync(() => CreateTexture(pixels));
}
```

If decoding is itself asynchronous (network I/O, JavaScript interop):

```csharp
await using var stream = await content.OpenStreamAsync(contentKey).ConfigureAwait(false);
var pixels = await DecodePngAsync(stream).ConfigureAwait(false);
return await GameThread.InvokeAsync(() => CreateTexture(pixels));
```

There is deliberately no two-phase loader interface. The split between decoding and publishing is
where `GameThread.InvokeAsync` is called, which works the same whichever thread the loader starts
on, and costs nothing when it starts on the game thread.

Because loads during startup run inline on the game thread, content loaded in `OnStart`, a
`ContentBundle.LoadAsync` included, loads with no thread-pool round trips. Once the game has started,
the same calls fan out across the worker pool, and each loader's game-thread step comes back through
the pump under its budget.

## Why

- **One game thread** keeps game code free of locks and data races, matches what mobile 2D games and
  the platforms' own UI models expect, and works unchanged on the web, which has only one thread. A
  dedicated render or loader thread would need every framework object to be thread-safe or
  double-buffered, for a 2D workload whose drawing is rarely the bottleneck.
- **A budgeted pump** spreads bursts of continuations without a fixed cap: a fixed budget either
  wastes headroom on light frames or overruns on heavy ones, while the adaptive controller finds the
  largest budget the current workload sustains.
- **Recording, not submitting, draw calls** keeps a later move of submission to another thread
  possible without changing game code or public API.
