# Diagnostics

## The stats overlay

[`Game.Diagnostics`](../api/Framework/DiagnosticsService.md) has an optional overlay. Turned off, which
is the default, it costs nothing to speak of:

```csharp
protected override Task OnStart()
{
    this.Diagnostics.StatsOverlayMode = StatsOverlayMode.Detailed;
    return Task.CompletedTask;
}
```

| Mode | What it shows |
|---|---|
| `Off` | Nothing |
| `FpsOnly` | Frames per second, colored against the display's refresh rate |
| `Detailed` | Frames per second and the display's refresh rate, update and draw time split into framework (cyan) and game (violet), dropped frames, the game thread's share of each frame (CPU), and draw calls and sprites per frame |

The numbers that say whether all is well are colored: green is good, yellow a warning, and red a
problem.

- **FPS** is judged by the game's target rate, which the detailed overlay gives beside it: the
  display's refresh rate (`DISPLAY`), the most frames it can show, or the game's
  [`PreferredFrameRate`](../api/Framework/Game.md#preferredframerate) when that's lower (`TARGET`).
  It's green at the target, yellow under 95% of it and red under 75%.
- **DROPPED** counts the last 60 frames that missed the target, taking half a refresh longer than a
  frame at the target rate: each one is a stutter you can see. A frame shows for a whole number of
  refreshes, so a target that doesn't divide into the display's rate, such as 50 on a 60 Hz display,
  mixes frames of one and two refreshes, and those don't count. It's green at none and red at any.
- **CPU** is the game thread's update and draw time as a share of a frame at the target rate. It's
  green under 50%, yellow to 80%, and red above, as the game nears the point where it can't finish
  a frame in the time it has. It isn't the CPU use the system shows, which is of every core, and
  it's elapsed time, so a wait inside update or draw counts too.

The overlay is drawn so that turning it on hardly changes what it shows: each frame draws it as one
sprite, and its numbers are drawn again four times a second, if they've changed. Its own draw calls,
sprites and time are left out of what it shows.

Low FPS with a green `CPU` points at the GPU, whose time the overlay doesn't measure. The times are
in milliseconds and averaged over the last 60 frames. The refresh rate is the one the platform
reports; where it can't tell, as on the web, it's estimated from the times of the display's refreshes the
frames were given, since the game started, the overlay on or off, so a game that slows later is still
judged by the display. One that has never reached the display's rate, from its first frame, or that
asks for a lower one, reads as a slower display.

Draw calls and sprites per frame are the numbers to watch when you're
[keeping draws cheap](graphics.md#keeping-draws-cheap).

## Tracing

[`Trace`](../api/Framework/Trace.md) writes messages to the platform's log: the debugger's output,
logcat, or the browser's console. Each call has a **tag**, a number unique in your source, so you can
find a message's call site from a log, even in a trimmed build:

```csharp
private void LogLevelStart(int level)
{
    Trace.Write(0x5a3c91e7, $"Level {level} started");
    Trace.WriteWarning(0x1e60b2a4, "The save file was out of date");
}
```

Pick any random number for each new call site. `Trace.MinLevel` sets how much gets through, and
`Trace.Writer` where it goes, if you want to send it somewhere else with your own
[`ITraceWriter`](../api/Framework/ITraceWriter.md).

## Crash reporting

Two events on [`Trace`](../api/Framework/Trace.md) let a crash reporter hear about faults. They're static,
so you can subscribe before the game starts, and they can be raised on any thread, so a handler has to
be thread-safe and quick:

```csharp
private static void SetUpReporting()
{
    // A fault the game can't recover from, just before the process ends
    Trace.FatalException += (_, e) => Report(e.Exception);

    // A failure the game runs on without, such as music that can't play: once per place per run
    Trace.NonFatalException += (_, e) => Report(e.Exception);
}

private static void Report(Exception? exception)
{
    Trace.WriteError(0x7c15d0ee, exception?.Message, exception);
}
```

[`ExceptionExtensions`](../api/Framework/ExceptionExtensions.md) can tag an exception with a stable
number that pinpoints where it was thrown, without needing symbols, which trimmed builds don't have.

## Startup timing

[`Diagnostics.MarkStartupMilestone`](../api/Framework/DiagnosticsService.md#markstartupmilestone) marks the milestones of startup, so that you
can measure it the same way in every build, including a published one. Each is traced with the time
since the process started, and on the web it's also a performance mark you can see in the browser's
profiler. Only the first mark of each name counts, so you can mark freely on paths that run again:

```csharp
protected override async Task OnStart()
{
    this.texture = await this.Content.LoadAsync<Texture>("Textures/bear");
    this.Diagnostics.MarkStartupMilestone("content-loaded", "1 texture");
}
```

## Debugging graphics

On desktop, [`GraphicsDebug`](../api/Desktop/DesktopGameEngine-Options.md#graphicsdebug) in the engine's
options turns on the GPU's debug mode, with its checks and any validation layers installed. It's much
slower, so leave it off in anything you ship.

## See also

- [`DiagnosticsService`](../api/Framework/DiagnosticsService.md)
- The framework sample's Time and content page
