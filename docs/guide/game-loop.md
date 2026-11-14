# The game loop

Your game derives from [`Game`](../api/Framework/Game.md) and overrides the methods it needs. The
platform calls them, on one thread, in a fixed order that's the same on every platform.

## Startup

| Order | Method | Use it to |
|---|---|---|
| 1 | Your constructor | Set fields. The game's services don't exist yet. |
| 2 | `OnInitialize` | Load what the launch screen should wait for |
| 3 | `OnStart` | Load content and set up the game |
| 4 | First frame | The launch screen fades out once it's on screen |
| 5 | `FirstFrameDrawn` completes | Start anything the first frame doesn't need |

Nothing updates or draws until `OnStart` has finished, so everything you await in it is ready for
the first frame. The app stays responsive while it runs, because no platform blocks on startup.

Start work the first frame doesn't need, such as signing in, syncing or analytics, after
[`FirstFrameDrawn`](../api/Framework/Game.md#firstframedrawn). On the web the game shares the page's
only thread, so anything started earlier delays the first frame even if nothing awaits it:

```csharp
protected override async Task OnStart()
{
    this.texture = await this.Content.LoadAsync<Texture>("Textures/bear");

    _ = this.SignInAfterFirstFrame();
}

private async Task SignInAfterFirstFrame()
{
    await this.FirstFrameDrawn;

    // Sign in here
}
```

## A frame

Each frame runs these in order, and the next frame doesn't start until they've finished:

1. The clock advances, and [`Time`](time.md) holds this frame's times.
2. **Input:** `OnPointerEvent`, `OnKeyEvent` and `OnGamepadEvent`, for each event since the last
   frame, and then text input's events.
3. **Continuations:** code after an `await` that's ready to go on.
4. **`OnFixedUpdate`:** none, one or several times, if you've turned on [fixed steps](time.md#fixed-steps).
5. **`OnUpdate`:** move things on by `Time.DeltaSeconds`.
6. **`OnDraw`:** draw everything.

Keep game logic out of `OnDraw`, as a hidden game skips it.

A draw call only records the work, so when it reaches the GPU isn't promised. That's why anything
that needs a result back from the GPU, such as
[`ReadPixelsAsync`](../api/Framework/GraphicsService.md#readpixelsasync), is asynchronous.

To handle a whole frame's events at once, for example to recognize gestures, override
`HandlePointerEvents`, `HandleKeyEvents` or `HandleGamepadEvents` instead. Their default
implementations call the single-event methods.

## The window changing

The game's area is [`ClientBounds`](../api/Framework/Game.md#clientbounds), in pixels. Keep controls
and important content inside [`SafeAreaBounds`](../api/Framework/Game.md#safeareabounds), the part
clear of notches, rounded corners and system bars. Both can change at any time, as a window is
resized or a device turned, and `OnClientBoundsChanged` tells you when:

```csharp
protected override void OnClientBoundsChanged()
{
    var safe = this.SafeAreaBounds;
    this.pageBounds = new RectangleInt(safe.X, safe.Y + 56, safe.Width, safe.Height - 56);
}
```

## Pausing, resuming and stopping

A game is always in one of three states, which work the same on every platform:

| State | When | Game time and `OnUpdate` | `OnDraw` | Continuations | Music |
|---|---|---|---|---|---|
| **Active** | In front, with the focus | Run | Runs | Run | Plays |
| **Inactive** | Visible, but it lost the focus or something covers it: another window, Control Center, the notification shade | Stop | Runs, so the picture stays correct | Run | Plays |
| **Hidden** | Not visible: minimized, in the background, a hidden page | Stop | Stops | Run | Paused, and resumed on return |

`OnPause` runs on the way out of active, and `OnResume` on the way back. Three things to remember:

- **Save in `OnPause`.** An app can be ended without warning after that. `OnStop` isn't guaranteed to
  run on a phone.
- **Don't wait on game time in `OnPause`.** Game time has stopped, so `Time.DelayAsync` wouldn't
  finish. Other awaited work does carry on, within what the platform allows: a few seconds on iOS and
  on a hidden web page.
- **Presses are canceled for you.** Any finger or button that's down gets a
  [`Canceled`](input.md#handling-a-press) event, and held keys are released, so nothing sticks. Input
  events are still delivered while the game is inactive.

To keep updating while inactive, for a game played on a second monitor for example, set
[`RunWhenInactive`](../api/Framework/Game.md#runwheninactive). A hidden game stops drawing either way.

`OnLowMemory` runs when the system is short of memory and may end the app, on platforms that report
it. Release whatever you can load again.

## Threads and async

**Your game code runs on one thread, the game thread.** `await` comes back to it, so you don't need
locks to touch game state, and you can load content and carry on as if it were sequential code:

```csharp
private async Task ShowLevel(int number)
{
    var data = await this.Content.OpenStreamAsync($"Data/level{number}.json");

    // Back on the game thread, so this is safe
    this.levelNumber = number;
    data.Dispose();
}
```

Framework APIs may only be called on the game thread, unless their documentation says otherwise.

**CPU-heavy work** goes on another thread with `Task.Run`, where it mustn't touch the framework or
game state. Awaiting it brings you back to the game thread with the result:

```csharp
private async Task Solve()
{
    var solution = await Task.Run(() => ComputeSolution());

    // Back on the game thread
    this.solution = solution;
}
```

**From another thread**, reach the game thread with
[`GameThread`](../api/Framework/GameThread.md). There's deliberately no way to block until it's done,
as that deadlocks as soon as the game thread is waiting on the thread that's asking:

```csharp
private async Task OnPurchaseCompletedOnAnotherThread()
{
    await GameThread.InvokeAsync(() => this.score += 100);
}
```

**The web has only one thread.** `Task.Run` queues work on the game thread there, so long work still
holds up frames. Split it up by awaiting
[`Time.NextFrameAsync`](../api/Framework/TimeService.md#nextframeasync) now and then.

**Continuations share each frame's time.** Each frame runs as many continuations as it has time for,
at least one, and spreads a burst over several frames rather than dropping a frame. While the game is
starting, they all run with no limit, as there's no frame to protect.

### Fire and forget

An `async` method you don't await, as in `_ = this.SignInAfterFirstFrame();`, reports any exception
through its task, where nobody sees it. Catch what you expect inside the method.

## Errors

[Diagnostics](diagnostics.md#crash-reporting) covers the events for reporting faults.

## See also

- [`Game`](../api/Framework/Game.md), every method and property
- [Time](time.md), for delays and game time
