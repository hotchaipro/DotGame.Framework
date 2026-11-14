# GameHost and the platform contract

`GameHost` ([framework/Platform/GameHost.cs](../../framework/Platform/GameHost.cs)) is the one place
that knows how to run a game. Each platform host supplies only what only it knows: when the graphics
device is ready, when a frame is due and when it will be shown, whether it can draw, the display's
size and rate, the input, and the app's lifecycle. `GameHost` decides what runs: the startup steps,
updates and draws, and when the frame clock resets. The order, what a pause or stop part way through
startup does, and how a fault is reported are written once, so a game behaves the same on desktop,
the web, Android and iOS.

For the threads this runs on and the continuation pump, see [threading.md](threading.md).

## The contract

`framework/Platform` is the whole contract between the framework and its platforms, both ways:

| Part | Direction | What it is |
|---|---|---|
| `GameHost` | Platform calls framework | Startup, frames, the display, input, lifecycle; static members for the game thread, fatal errors, statistics and the startup timeline |
| Strategy interfaces (`framework/Platform/Audio`, `Content`, `Graphics`, `Input`) | Framework calls platform | One per service (`IGraphicsServiceStrategy`, `IAudioServiceStrategy`, `IContentServiceStrategy`, `IInputServiceStrategy`) and one per object behind a framework type (`ITextureStrategy`, `ISpriteBatchStrategy`, `IShaderStrategy`, `ISoundStrategy`, `IVoiceStrategy`, `IMusicStrategy`, `IVolumeControlStrategy`, ...) |
| `Strategy<T>` | Both | The box between a framework object and the platform object behind it |
| `PlatformServices` | Platform to framework | The four service strategies, passed to `BeginStartup` |
| `DisplayInfo`, `PointerEventData`, `KeyEventData` | Platform to framework | The values a host reports |
| `[PlatformContract]` members | Platform calls framework | The few internals of public framework types a platform needs: the strategy field behind `Texture`, `Sound`, `Music`, `Shader`, `SpriteBatch` and `VolumeControl`, the constructors that wrap one, and a handful of helpers such as `ContentManifestEntry.TryGetMusicDuration` |

Every type in it is internal. The framework gives its internals to the four platform assemblies
(`DotGame.Framework.Desktop`, `.WebGL`, `.Android`, `.iOS`) and its test assemblies through
`InternalsVisibleTo` in [framework/DotGame.Framework.csproj](../../framework/DotGame.Framework.csproj).
That gives them every internal, so the analyzer
[analyzers/PlatformContractAnalyzer.cs](../../analyzers/PlatformContractAnalyzer.cs) narrows it back:
error **DG0002** reports any use, in an assembly the framework gives its internals to, of a framework
internal that is neither in `DotGame.Framework.Platform` (or a namespace under it) nor marked
`[PlatformContract]`. A `cref` in a documentation comment isn't a use. To give platforms something
new, add it to `GameHost` or a strategy, or mark the member.

The contract stays internal so it can change with the framework without becoming public API, and the
analyzer keeps a platform from reaching past the framework's invariants into its implementation.

## Startup

A host calls these once, on the thread that will be the game thread:

1. `GameHost.InitializeGameThread()`: makes the calling thread the game thread and sets the game's
   synchronization context on it. It is called outside any `async` method, since an async method's
   builder restores `SynchronizationContext.Current` when it returns.
2. `SetDisplay(...)`, so the game's `OnInitialize` can read `ClientBounds`.
3. `BeginStartup(PlatformServices)`, once the graphics device is ready (on Android, once the first
   surface's EGL context is current).

`BeginStartup` runs synchronously up to its first await, so the game's services exist when it
returns. Startup then runs as one task:

1. `Game.CreateServices` makes `Time`, `Content`, `Graphics`, `Audio`, `Input` and `Diagnostics` over
   the platform's strategies, and `InitializeServices` initializes them in that order.
2. `OnInitialize` runs. Game time is then reset to zero.
3. `OnStart` runs.
4. The game is started: `IsStarted` is true, the game is active, the frame time is reset so the first
   frame has no elapsed time, and content loads move to worker threads (except on the web).
5. A display change that arrived while starting is reported (`OnClientBoundsChanged`), and a pause
   that arrived while starting is applied (`OnPause`), as if each had arrived just after.

The host never waits on startup. From `BeginStartup` on, it calls `Tick` from every frame callback,
and until the game has started each `Tick` only advances startup: it discards queued input (canceling
a press in progress, so its end isn't delivered without its beginning), advances game time so a delay
awaited in `OnInitialize` or `OnStart` finishes, and drains the game thread's continuations. It
doesn't update or draw, so nothing animates before the first frame, and everything awaited in
`OnStart` is ready when it's drawn. The app stays responsive while the game loads: a desktop window
can be moved, resized or closed, an app backgrounded, a tab closed.

A startup step that throws sets `StartupFault`, ends startup, and is reported through
`FatalError.Terminate`, the same on every platform.

### The launch screen

Until the game's first frame is on screen, each platform shows its launch screen. A host gives
`GameHost` the platform's `ILaunchScreenStrategy` before startup (`ShowLaunchScreen`), and reports
the first frame on screen (`NotifyFirstFramePresented`, from any thread; only the first report counts),
which dismisses it: it fades out over `GameHost.LaunchScreenFadeDuration` with the curve
`GameHost.LaunchScreenFadeEase` (see [launch-screen.md](launch-screen.md)). `Tick` returns whether the
game drew, which is how a host knows: Android presents nothing until a frame is drawn, desktop draws
the launch screen until then, iOS clears to `LaunchScreen.Background` under the launch storyboard, and
the web page waits for the first drawn frame to be composited. `Game.FirstFrameDrawn` completes when
the host draws the first frame.

## Frames

```csharp
public bool Tick(TimeSpan frameTimestamp, bool canDraw);
```

`frameTimestamp` is a monotonic time from the platform's frame source, with any origin; only
differences between timestamps are used. Each platform uses the display's own frame timing, not the
time its callback happened to run:

| Platform | Frame source | Timestamp |
|---|---|---|
| Desktop | The SDL event and frame loop, paced by the swapchain | `SDL_GetTicksNS` after the wait for the swapchain |
| WebGL | `requestAnimationFrame` | The animation frame's timestamp, through `GameBindings.RunFrame` |
| Android | `Choreographer` callbacks on the render thread's `Looper` | The preferred frame timeline's expected presentation time on API 33 and later, otherwise the frame's vsync time |
| iOS | A `CADisplayLink`, which draws the paused `MTKView` | `CADisplayLink.TargetTimestamp`: when the frame will be shown |

The origins differ (presentation time on iOS and recent Android, frame start on the web), but a
constant offset doesn't change a difference.

Once the game has started, a frame runs in this order:

1. Game time advances (`TimeService.Update`), and due delays run.
2. Pointer, key and gamepad events queued since the last frame are delivered, then text input events.
3. The steady-state continuation pump runs, under its adaptive budget ([threading.md](threading.md)).
   It comes before the update, so state a continuation changes is applied by this frame's update
   rather than drawn stale.
4. `OnFixedUpdate`, as many times as fixed steps are due, if `Game.FixedUpdateRate` is set; then
   `OnUpdate`.
5. If `canDraw`: `Graphics.BeginFrame`, `OnDraw`, then the diagnostics overlay.

Steps 1 and 4 run only while the game is active, or `Game.RunWhenInactive` is set, or the game hasn't
drawn its first frame yet (so a game paused while starting has something to show). Input and
continuations run either way. The update and draw are bracketed for the frame statistics.

`canDraw: false` runs the frame without the draw: while there's no swapchain image on desktop (a
minimized or occluded window), while a lost graphics context is being recovered on Android and the
web, and while Android's window can't present during startup. A host reads that from its own graphics
service; nothing is set on the game.

An exception from the game's update or draw is reported through `FatalError.Terminate`, the same on
every platform. With a debugger attached it stays unhandled, so the debugger stops where it was
thrown.

### The frame clock

[framework/Time/TimeService.cs](../../framework/Time/TimeService.cs) measures each frame as the
difference from the last frame's timestamp, limited to `TimeService.MaxDelta`. The first frame after
a reset has no previous timestamp, so it repeats the last frame's length (zero after startup).
`Total` advances by the limited value, so timers stay consistent with what was animated.

`GameHost` resets the clock itself after startup and on resume, so time spent loading or in the
background isn't game time. A platform calls `ResetFrameClock()` only after a stall the host can't
see: desktop after the event pump has been held up (a title bar or menu held) for over 100 ms, and
Android and the web after recovering a lost graphics context.

**A maximum frame time.** `MaxDelta` is a third of a second. It covers stalls nothing reports: a
debugger breakpoint, a long garbage collection, the OS suspending the process without a lifecycle
event. Without it, the frame after a 10-second breakpoint would move every animation, timer and
physics step 10 seconds at once. With it, time runs slow through a stall instead of jumping; below 3
frames a second the game slows down rather than skipping. Fixed steps have a matching limit, 8 per
frame (`TimeService.MaxFixedStepsPerFrame`), past which the rest is dropped rather than owed to the
next frame.

Widely used engines all have this limit, and most let the game change it:

| Engine | Limit | Default |
|---|---|---|
| Unity | `Time.maximumDeltaTime` | 1/3 s |
| XNA, MonoGame, FNA | `Game.MaxElapsedTime` | 500 ms |
| "Fix Your Timestep" (Gaffer on Games), the usual reference for fixed-step loops | Clamps the frame time | 250 ms |

The default follows Unity's: long enough that no real frame is cut short, short enough that a stall
doesn't skip a visible amount of animation. `MaxDelta` is internal; a game can't change it.

## Lifecycle

```csharp
public Task PauseAsync();
public Task ResumeAsync();
public Task StopAsync();
```

All three are called on the game thread. A game is active, inactive or hidden, the same on every
platform ([threading.md](threading.md#inactive-and-hidden) has the table).

- **`PauseAsync`** makes the game inactive (`OnPause`) when its window loses focus, something covers
  it, or the app goes to the background. It cancels pointer presses in progress, since the platform
  may never report their release. A repeat while already inactive does nothing more, as platforms can
  report one change more than once (the web reports losing focus and being hidden separately). A
  pause while starting is remembered and applied once the game has started.
- **`ResumeAsync`** makes the game active again (`OnResume`) and resets the frame clock. A resume
  while starting cancels a pending pause.
- **`StopAsync`** stops the game (`OnStop`) before the platform disposes it, and is safe at any point.
  During startup it waits for startup to finish (frames and pumps keep advancing it), so a game whose
  `OnStart` ran always gets its `OnStop`. After a stop, `Tick` does nothing and
  `Game.FirstFrameDrawn` is replaced, so a later start waits for its own first frame.

A hook that throws before its first await faults the returned task rather than throwing, so the
platform's handling of the task (`SafeAsync.Invoke` in [platform/shared](../../platform/shared)) is
what reports a fault, as for an async hook.

The platform's sources:

| Platform | Pause and resume | Hidden | Stop |
|---|---|---|---|
| Desktop | Window focus lost and gained | Minimized or hidden: no swapchain, so frames run with `canDraw: false` every 16 ms, and music stops | The window closes or `DesktopGameEngine.Stop()` is called: the loop ends, then the host hides the window, stops the game while pumping the game thread (up to 5 s), and disposes it |
| Android | The activity paused or resumed, or the window losing or gaining focus | Presentation stops on the window's visibility, detach or the activity's pause; with no frames, posted work is drained as it arrives | The activity is destroyed: the render thread stops the game while pumping (up to 5 s), disposes it, then releases EGL and ends the game thread |
| iOS | `WillResignActive` and `DidBecomeActive` | Entering the background pauses the display link, and pumps the game thread (up to 3 s, under a background task) so an async `OnPause` can save | `WillTerminate`: the display link is invalidated, then the host stops the game while pumping (up to 2 s) and disposes it |
| WebGL | The window's `blur` and `focus`, the page being hidden or shown, and input reaching the page (iOS can blur a page and never send the focus that ends it) | No animation frames: work posted while hidden is drained on demand, through `WorkPosted` | Not called: a page can be discarded at any time, so `OnPause` is where a web game saves |

## The display

```csharp
public void SetDisplay(in DisplayInfo display);

internal readonly record struct DisplayInfo(
    RectangleInt ClientBounds,
    RectangleInt SafeAreaBounds,
    float Dpi,
    double RefreshRateHz);
```

The client bounds, safe area, density and refresh rate describe one display and change together (a
window moved to another monitor), so a host reports all four, when it first knows them and whenever
any changes. `GameHost` stores the bounds and density on the game, raises `OnClientBoundsChanged` if
the bounds, safe area or density changed (a change while starting is reported once the game has
started), and passes the refresh rate to the continuation budget. A density that isn't positive keeps
the last one, and `Game.DisplayDpi` is 96 until a platform reports one.

| Platform | Safe area | Density | Refresh rate |
|---|---|---|---|
| Desktop | The client bounds | 96 × the window's display scale, over its pixel density | The SDL current display mode, read again when the display or its mode changes |
| WebGL | The page's safe area insets | 96 × `devicePixelRatio` | 0: browsers don't report it, so the budget assumes 60 |
| Android | Inset for system bars, cutouts, rounded corners and the soft keyboard | The average of the X and Y DPI | `Display.RefreshRate` |
| iOS | The view's safe area insets | 163 × the content scale | `UIScreen.MaximumFramesPerSecond` at first, then the rate the display link delivers (`TargetTimestamp − Timestamp`) |

The refresh rate only seeds the continuation budget, which calibrates from measured frame intervals.
`Game.PreferredFrameRate` goes the other way, through the graphics strategy: each platform applies it
to its own frame source (the desktop and web loops skip frames, Android sets the surface's frame rate,
iOS sets the display link's preferred range).

## Input

```csharp
public void QueuePointerEvent(in PointerEventData e);
public void QueueKeyEvent(in KeyEventData e);
public void QueueGamepadEvent(in GamepadEvent e);
```

Any thread may call these: Android's touch events arrive on its UI thread, not the game thread.
`InputService` queues them under a lock, and the next frame delivers them. Events before the game's
services exist are dropped, and events that arrive while starting are discarded by the starting
frames.

Pointer and key events are readonly structs with `init` properties and `required` fields, not
methods with long parameter lists, whose adjacent `PointerButton` and `float` parameters are easy to
pass in the wrong order; a field added later doesn't change every host's call. Their names end in
`Data` to stay clear of the public `PointerEvent` and `KeyEvent` games see. `PointerEventData.AgeMs`
lets a platform that delivers events late say how late.

## Other notifications

- `NotifyDeviceReset()`, on the game thread, once a lost graphics device is back and the platform has
  restored what it keeps (Android, the web). It raises `GraphicsService.DeviceReset`.
- `NotifyLowMemory()`, from any thread, posts `OnLowMemory` to the game thread.
- `WindowBackground`: the color the last frame first cleared to, which the web page shows where the
  canvas doesn't reach.
- `ShowLaunchScreen(ILaunchScreenStrategy)`, once, before startup, and `NotifyFirstFramePresented()`,
  from any thread: the launch screen, shown and then dismissed at the first frame on screen.

## The static members

The game thread, fatal errors, frame statistics and the startup timeline are one per process, and
code deep in a platform (a texture loader, a sprite batch) uses them without a host to hand, so they
are static on `GameHost`:

| Member | Use |
|---|---|
| `InitializeGameThread`, `ShutdownGameThread` | Begin and end the game thread's role on the calling thread |
| `EnsureGameThreadContext` | Set the game's synchronization context again, where the browser runtime drops it between interop calls |
| `DrainContinuations` | Run the game thread's queue outside frames: while a paused or hidden app idles, and during teardown |
| `WorkPosted` | A callback when work is posted, for a platform that pumps on demand (the web while starting or hidden, Android while it has no frames) |
| `Terminate`, `ReportNonFatal`, `ReportAudioFailure` | Fatal and non-fatal error reports; an audio failure is fatal only in a Debug build |
| `CountDraws` | Draw statistics, reported once per sprite batch flush |
| `StartupTimelineSink` | Receives startup timeline marks, for a platform with its own timeline (the browser's performance marks) |
| `LaunchScreenFadeDuration`, `LaunchScreenFadeEase`, `LaunchScreenFadeCssEase`, `EaseLaunchScreenFade` | The launch screen fade every platform uses |

## What each platform host supplies

| | Desktop | WebGL | Android | iOS |
|---|---|---|---|---|
| Host type | `DesktopGameEngine` | `WebGLGameEngine`, `GameBindings` | `AndroidGameEngine`, `RenderThread` | `iOSGameEngine` |
| Game thread | The main thread | The browser main thread | The render thread, which owns the EGL context | The main thread |
| `BeginStartup` | After the GPU device and window exist and the launch screen is shown | After the canvas is registered with the animation frame loop | On the first surface | After the window is shown and the launch storyboard overlaid |
| Startup while it can't draw | Frames with `canDraw: false` | A startup pump on a `MessageChannel` message whenever work is posted, since a hidden tab gets no animation frames | Delayed messages every 8 ms on the `Looper` | Display link frames |

`GameHost` owns everything else: the startup sequence and its faults, the pending pause, display
change and stop, the frame order, the draw decision, the frame clock's resets, the continuation pump,
and the active state.

## Strategy&lt;T&gt;

`Strategy<T>` ([framework/Platform/Strategy.cs](../../framework/Platform/Strategy.cs)) holds the
platform object behind each framework object:

- `Interface` is the platform object as the strategy interface, and throws `ObjectDisposedException`
  once disposed.
- `ToConcrete<U>()` gets back the platform's own type, in one named place, with the disposed check.
  It is an `Unsafe.As` cast, so Release builds pay nothing; Debug builds check the type and throw
  `InvalidCastException`, since a wrong type would otherwise be silent memory corruption.
- `IsDisposed` and `Dispose` give every framework object the same disposal state.
- `Reset(newValue)` swaps in a new platform object and disposes the old one. Android and the web use
  it after a lost context, to recover textures, render textures and shaders (and on Android, sprite
  batches; on the web, shader parameters), while the game's `Texture`, `Shader` and `SpriteBatch`
  references stay valid.

It doesn't force a recovery design: a platform may swap the object with `Reset` or recover inside its
own object. Its cost is one extra object per resource and one extra load per access, small next to a
draw call or a texture upload.

`PlatformServices` is a record of the four service strategies rather than an interface a platform
implements, so a service added to it is a constructor parameter, and a host that forgets one fails to
compile. Music playback (`Play`, `Prepare`, `Unprepare`, `Stop`, `Pause`) is on `IMusicStrategy`
rather than the audio service, so a platform's music object handles its own state machine without
casting back to itself; the audio service keeps what is service-wide, such as voices and
`ShouldPlayMusic`.

## Why Game and GameHost are separate

`Game` is for game developers: its public members and its virtual `On...` hooks are what a game sees.
`GameHost` holds the hosting machinery. `GameHost` calls the hooks through internal members on `Game`
that each call one virtual (`internal Task InvokeOnStart() => this.OnStart();`), so the sequencing is
tested with a test game and fake strategies, with no window or device
([test/DotGame.Framework.Tests/Platform](../../test/DotGame.Framework.Tests/Platform), and the test
platform in [test/DotGame.Framework.Testing](../../test/DotGame.Framework.Testing)).

| On GameHost | On Game |
|---|---|
| The order of initialize, start, stop, pause and resume, and what arrives part way | `Content`, `Graphics`, `Audio`, `Input`, `Time`, `Diagnostics` |
| The frame: continuations, the clock, the statistics, the draw decision | `ClientBounds`, `SafeAreaBounds`, `DisplayDpi`, `IsActive`, `RunWhenInactive`, `FirstFrameDrawn` |
| Reporting display changes | The hooks: `OnInitialize`, `OnStart`, `OnStop`, `OnPause`, `OnResume`, `OnFixedUpdate`, `OnUpdate`, `OnDraw`, input, `OnClientBoundsChanged`, `OnLowMemory` |

Merging the two would put the platform contract's internals on the public class every game derives
from, turning the contract from one namespace into attributes scattered over a public type, which is
harder to review and to enforce.

## Precedent

- **MonoGame and FNA:** `Game` is the developer's class; `GamePlatform` (per platform) owns the run
  loop, the window and the lifecycle, and calls `Game.Tick`. `GameHost` plays the shared part of
  `GamePlatform`.
- **.NET Generic Host:** `IHost` owns startup, shutdown and the lifetime events; the app supplies
  hosted services. The host, not each service, decides the order and handles a stop during startup.
- **Android frame pacing:** the Android Game Development Kit's frame pacing library (Swappy) drives
  rendering from Choreographer rather than pacing on buffer swaps.
- **Frame timing:** Apple recommends `CADisplayLink.targetTimestamp` for animation timing, and
  browsers pass a frame timestamp to `requestAnimationFrame` callbacks for the same reason: the time a
  frame is shown matters more than the time its callback happened to run.
