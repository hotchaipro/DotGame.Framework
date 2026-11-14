// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Diagnostics;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;
using DotGame.Framework.Threading;
using DotGame.Framework.Time;

namespace Sample.Pages;

/// <summary>
/// Game time and delays, content (bundles, and data files read as streams), the stats overlay
/// and tracing, and what the game knows about its window and its lifecycle.
/// </summary>
/// <remarks>
/// Game time advances only while the game runs: delays measured in it pause with the game.
/// Async code in a game continues on the game thread, so it can touch game state without locks.
/// </remarks>
internal sealed class TimeAndContentPage : SamplePage
{
    private const string BundleName = "extras";

    private readonly List<string> _notes = new();
    private DelayHandle? _timer;
    private TimeSpan _timerEnd;
    private string _timerStatus = "Not started";
    private Action? _stopTicking;
    private int _ticks;
    private string _bundleStatus = "Not loaded";
    private bool _bundleBusy;

    private RectangleInt _timerButton;
    private RectangleInt _cancelButton;
    private RectangleInt _bundleButton;
    private RectangleInt _statsButton;
    private RectangleInt _traceButton;

    public TimeAndContentPage(
        FrameworkSample game)
        : base(game)
    {
    }

    public override string Title => "Time, content and diagnostics";

    public override string Hint => "Timers run on game time; bundles load content in groups.";

    public override async Task Load()
    {
        // A data file is read as a stream, by its path under Content (with its extension)
        using var stream = await this.Game.Content.OpenStreamAsync("Data/notes.txt");
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            this._notes.Add(line);
        }

        // A bundle is a named group of content, loaded and unloaded together: a level's
        // textures, or a screen's. It loads on demand below. Unloading disposes its content, so
        // a bundle should hold only content nothing else is using.
        var bundle = this.Game.Content.Bundles.Add(BundleName);
        bundle.Add<Texture>("Textures/character");
    }

    public override void Enter()
    {
        // An async loop that ticks twice a second on game time, until the page is left
        var ticking = new CancellationTokenSource();
        _ = this.TickAsync(ticking.Token);
        this._stopTicking = () =>
        {
            ticking.Cancel();
            ticking.Dispose();
        };
    }

    public override void Leave()
    {
        this._stopTicking?.Invoke();
        this._stopTicking = null;
    }

    private async Task TickAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await this.Game.Time.DelayAsync(TimeSpan.FromSeconds(0.5), cancellationToken);

                // Back on the game thread after every await
                this._ticks++;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartTimer()
    {
        this._timer?.Cancel();
        this._timerEnd = this.Game.Time.Total + TimeSpan.FromSeconds(3);
        this._timerStatus = "Running";

        // Delay runs an action on the game thread after an amount of game time
        this._timer = this.Game.Time.Delay(TimeSpan.FromSeconds(3), () => this._timerStatus = "Done");
    }

    private async Task ToggleBundle()
    {
        var content = this.Game.Content;
        if (this._bundleBusy || !content.Bundles.TryGet(BundleName, out var bundle))
        {
            return;
        }

        this._bundleBusy = true;
        try
        {
            if (content.TryGet<Texture>("Textures/character", out _))
            {
                await bundle.UnloadAsync();
                this._bundleStatus = "Unloaded";
            }
            else
            {
                this._bundleStatus = "Loading";
                await bundle.LoadAsync();
                this._bundleStatus = "Loaded";
            }
        }
        finally
        {
            this._bundleBusy = false;
        }
    }

    public override void OnPointerEvent(
        PointerEvent e)
    {
        if (IsTap(e, this._timerButton))
        {
            this.StartTimer();
        }
        else if (IsTap(e, this._cancelButton) && (this._timer is { IsCanceled: false }))
        {
            this._timer.Cancel();
            this._timerStatus = "Canceled";
        }
        else if (IsTap(e, this._bundleButton))
        {
            _ = this.ToggleBundle();
        }
        else if (IsTap(e, this._statsButton))
        {
            this.Game.CycleStatsOverlay();
        }
        else if (IsTap(e, this._traceButton))
        {
            // Traces go to the platform's log (the debugger's output, logcat, the browser's
            // console). Tag each call site with its own number, to find it from the log.
            Trace.Write(0x5a3c91e7, $"Traced at {this.Game.Time.Total.TotalSeconds:0.00} s");
        }
    }

    public override void Draw(
        SpriteBatch spriteBatch,
        RectangleInt bounds)
    {
        var game = this.Game;
        var text = game.Text;
        using var batch = spriteBatch.Begin();

        int x = bounds.X + 16;
        int y = bounds.Y + 12;
        const float Small = 0.6f;
        var gray = Color.FromRgb(0xAAB4C8);

        // Time
        text.Draw(spriteBatch, $"Game time {game.Time.Total.TotalSeconds:0.0} s, frame {game.Time.Delta.TotalMilliseconds:0.0} ms, ticks {this._ticks}", new Vector2(x, y), Color.White, Small);
        y += 30;
        this._timerButton = new RectangleInt(x, y, 160, 44);
        this._cancelButton = new RectangleInt(x + 172, y, 120, 44);
        game.DrawButton(spriteBatch, this._timerButton, "3 s timer");
        game.DrawButton(spriteBatch, this._cancelButton, "Cancel");
        var remaining = (this._timerStatus == "Running") ? $" {(this._timerEnd - game.Time.Total).TotalSeconds:0.0} s" : string.Empty;
        text.Draw(spriteBatch, this._timerStatus + remaining, new Vector2(x + 306, y + 12), gray, Small);

        // Content
        y += 64;
        this._bundleButton = new RectangleInt(x, y, 220, 44);
        game.DrawButton(spriteBatch, this._bundleButton, this._bundleStatus == "Loaded" ? "Unload bundle" : "Load bundle");
        text.Draw(spriteBatch, $"Bundle '{BundleName}': {this._bundleStatus}", new Vector2(x + 234, y + 12), gray, Small);

        // TryGet finds content that's already loaded, without waiting
        if (game.Content.TryGet<Texture>("Textures/character", out var character))
        {
            spriteBatch.Draw(character, new Vector2(bounds.Right - 90, y - 20), Color.White, Angle.Zero, Vector2.Zero, new Vector2(72f / character.Size.Y));
        }
        y += 56;
        foreach (var note in this._notes)
        {
            text.Draw(spriteBatch, note, new Vector2(x, y), gray, 0.5f);
            y += 20;
        }

        // Diagnostics
        y += 16;
        this._statsButton = new RectangleInt(x, y, 240, 44);
        this._traceButton = new RectangleInt(x + 252, y, 120, 44);
        game.DrawButton(spriteBatch, this._statsButton, $"Stats: {game.Diagnostics.StatsOverlayMode}");
        game.DrawButton(spriteBatch, this._traceButton, "Trace");

        // The window and the lifecycle
        y += 64;
        var client = game.ClientBounds;
        var safe = game.SafeAreaBounds;
        text.Draw(spriteBatch, $"Client {client.Width} x {client.Height}, safe area {safe.X},{safe.Y} {safe.Width} x {safe.Height}, {game.DisplayDpi:0} dpi", new Vector2(x, y), gray, Small);
        y += 24;
        text.Draw(spriteBatch, $"Paused {game.PauseCount}, resumed {game.ResumeCount}, resized {game.BoundsChangedCount}, game thread {GameThread.IsCurrent}", new Vector2(x, y), gray, Small);
    }
}
