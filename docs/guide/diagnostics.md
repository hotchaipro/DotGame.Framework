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
| `FpsOnly` | Frames per second |
| `Detailed` | Frames per second, update and draw time split into framework and game, and draw calls and sprites per frame |

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
