# Time

[`Game.Time`](../api/Framework/TimeService.md) is the game's clock. Game time runs only while the
game does, so anything measured in it pauses along with the game.

## Moving things

Multiply speeds by the time the frame took, so that they don't depend on the frame rate:

```csharp
protected override void OnUpdate()
{
    this.position.X += 200 * this.Time.DeltaSeconds;
}
```

| Property | What it is |
|---|---|
| `DeltaSeconds` | This update's game time, in seconds as a float, for arithmetic |
| `Delta` | The same as a `TimeSpan` |
| `Total` | All the game time so far |
| `UnscaledDeltaSeconds`, `UnscaledDelta`, `UnscaledTotal` | The same, ignoring [`Scale`](#slow-motion-and-pausing) |

One frame advances game time by at most a third of a second. After a breakpoint or a stall, the game
slows down rather than jumping ahead.

## Slow motion and pausing

[`Scale`](../api/Framework/TimeService.md#scale) sets how fast game time runs: 1 is real time, 0.5 is
half speed, and 0 stops it, as a pause menu might. It applies to `Delta`, `Total`, delays and
fixed steps.

Whatever should carry on while game time is stopped, such as the pause menu itself, uses
`UnscaledDeltaSeconds`:

```csharp
private void ShowPauseMenu()
{
    this.Time.Scale = 0;
}

protected override void OnUpdate()
{
    // Stops while paused
    this.position.X += 200 * this.Time.DeltaSeconds;

    // Keeps going
    this.menuPulse += this.Time.UnscaledDeltaSeconds;
}
```

## Fixed steps

Physics and anything that must behave the same at any frame rate should run in steps of a fixed
length. Set [`FixedUpdateRate`](../api/Framework/Game.md#fixedupdaterate), usually in the game's
constructor, and override [`OnFixedUpdate`](../api/Framework/Game.md#onfixedupdate), which runs that
many times a second of game time: none, one or several times a frame, before `OnUpdate`.

`DeltaSeconds` is the step's length there, so the same movement code works in either update. To stay
smooth on any display, draw between the last two steps using `FixedAlpha`, how far game time has got
toward the next step:

<!-- no-check -->
```csharp
this.FixedUpdateRate = 60;
```

```csharp
protected override void OnFixedUpdate()
{
    this.previousPosition = this.position;
    this.velocity.Y += 980 * this.Time.DeltaSeconds;
    this.position += this.velocity * this.Time.DeltaSeconds;
}

protected override void OnDraw()
{
    var drawn = Vector2.Lerp(this.previousPosition, this.position, this.Time.FixedAlpha);

    var spriteBatch = this.Graphics.SharedSpriteBatch;
    using var batch = spriteBatch.Begin();
    spriteBatch.Draw(this.texture, drawn, Color.White);
}
```

`UnscaledDelta` stays the frame's real time in a fixed step. A slow frame runs only a limited
number of steps, so a stall can't turn into a spiral of ever more steps.

Fixed updates are off by default, so a game that doesn't use them pays nothing for them. An override of
`OnFixedUpdate` with no rate set anywhere in the game fails the build (rule DG0005), and `FixedAlpha`
throws while they're off.

## Delays

All of these use game time and run on the game thread, so they stop while the game is paused.

**Run something later**, with a handle to cancel it:

```csharp
private void StartTimer()
{
    this.timer = this.Time.Delay(TimeSpan.FromSeconds(3), () => this.score += 10);
}

private void CancelTimer()
{
    this.timer?.Cancel();
}
```

**Wait in an `async` method**, with a token to cancel it:

```csharp
private async Task TickUntilCanceled(CancellationToken cancellationToken)
{
    try
    {
        while (true)
        {
            await this.Time.DelayAsync(TimeSpan.FromSeconds(0.5), cancellationToken);
            this.score++;
        }
    }
    catch (OperationCanceledException)
    {
        // Stopped
    }
}
```

**Wait for the next frame**, to spread long work over frames:

```csharp
private async Task LoadLevels(string[] names)
{
    foreach (var name in names)
    {
        this.Parse(name);
        await this.Time.NextFrameAsync();
    }
}
```

Delays due on the same frame run in the order they were due, and those due at the same time in the
order they were scheduled.

Don't await game time in `OnPause`: [it has stopped](game-loop.md#pausing-resuming-and-stopping).

## Reusable code

Only your game's root should read `Game.Time`. Have reusable components take the time as a parameter.
They can then run in slow motion, or stop under a live
menu, and they can be tested.

## See also

- [`TimeService`](../api/Framework/TimeService.md)
- [The game loop](game-loop.md)
