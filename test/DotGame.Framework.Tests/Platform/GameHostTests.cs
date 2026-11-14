// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Input;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;
using DotGame.Framework.Time;
using Xunit;
using static DotGame.Framework.Tests.Platform.TestPlatform;

namespace DotGame.Framework.Tests.Platform;

[CollectionDefinition(nameof(GameThreadTests), DisableParallelization = true)]
public sealed class GameThreadTests
{
}

/// <summary>
/// The sequencing <see cref="GameHost"/> owns for every platform: startup, the lifecycle around
/// it, frames, and the frame clock.
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class GameHostTests
{
    [Fact]
    public void Startup_InitializesThenStarts_WithoutUpdatingUntilStarted()
    {
        OnGameThread(() =>
        {
            var game = new TestGame();
            var host = new GameHost(game);

            host.BeginStartup(CreateServices());
            Assert.False(host.Tick(At(0), canDraw: true));
            Assert.False(host.Tick(At(16), canDraw: true));

            Assert.Equal(["Initialize", "Start"], game.Calls);
            Assert.False(host.IsStarted);

            game.FinishStart();
            Assert.False(host.Tick(At(33), canDraw: true));
            Assert.True(host.IsStarted);
            Assert.True(game.IsActiveNow);

            Assert.True(host.Tick(At(50), canDraw: true));
            Assert.Equal(["Initialize", "Start", "Started", "Update", "Draw"], game.Calls);
            Assert.True(game.FirstFrameDrawn.IsCompleted);
        });
    }

    [Fact]
    public void BeginStartup_Twice_Throws()
    {
        OnGameThread(() =>
        {
            var host = new GameHost(new TestGame());
            host.BeginStartup(CreateServices());

            Assert.Throws<InvalidOperationException>(() => host.BeginStartup(CreateServices()));
        });
    }

    [Fact]
    public void PauseWhileStarting_PausesOnceStarted_AndStillDrawsTheFirstFrame()
    {
        OnGameThread(() =>
        {
            var game = new TestGame();
            var host = new GameHost(game);

            host.BeginStartup(CreateServices());
            _ = host.PauseAsync();
            Assert.DoesNotContain("Pause", game.Calls);

            game.FinishStart();
            host.Tick(At(0), canDraw: true);

            Assert.Equal(["Initialize", "Start", "Started", "Pause"], game.Calls);
            Assert.False(game.IsActiveNow);

            // The first frame runs even while paused, so the window isn't left blank
            Assert.True(host.Tick(At(16), canDraw: true));
            Assert.Contains("Update", game.Calls);
        });
    }

    [Fact]
    public void ResumeWhileStarting_CancelsAPauseWhileStarting()
    {
        OnGameThread(() =>
        {
            var game = new TestGame();
            var host = new GameHost(game);

            host.BeginStartup(CreateServices());
            _ = host.PauseAsync();
            _ = host.ResumeAsync();

            game.FinishStart();
            host.Tick(At(0), canDraw: true);

            Assert.Equal(["Initialize", "Start", "Started"], game.Calls);
            Assert.True(game.IsActiveNow);
        });
    }

    [Fact]
    public void StopWhileStarting_WaitsForStartup_ThenStops()
    {
        OnGameThread(() =>
        {
            var game = new TestGame();
            var host = new GameHost(game);

            host.BeginStartup(CreateServices());
            var stop = host.StopAsync();
            Assert.False(stop.IsCompleted);

            // Startup still advances while the stop waits, as a platform pumps the game thread
            game.FinishStart();
            GameHost.DrainContinuations();
            GameHost.DrainContinuations();

            Assert.True(stop.IsCompletedSuccessfully);
            Assert.Equal(["Initialize", "Start", "Started", "Stop"], game.Calls);
            Assert.False(host.IsStarted);

            // A stopped game runs no more frames
            Assert.False(host.Tick(At(0), canDraw: true));
            Assert.DoesNotContain("Update", game.Calls);
        });
    }

    [Fact]
    public void StopBeforeStartup_DoesNothing()
    {
        OnGameThread(() =>
        {
            var game = new TestGame();
            var host = new GameHost(game);

            Assert.True(host.StopAsync().IsCompletedSuccessfully);
            Assert.Empty(game.Calls);
        });
    }

    [Fact]
    public void Stop_RunsOnStop_AndResetsFirstFrameDrawn()
    {
        OnGameThread(() =>
        {
            var (game, host) = StartGame();
            host.Tick(At(16), canDraw: true);
            Assert.True(game.FirstFrameDrawn.IsCompleted);

            Assert.True(host.StopAsync().IsCompletedSuccessfully);

            Assert.Equal("Stop", game.Calls[^1]);
            Assert.False(game.FirstFrameDrawn.IsCompleted);
        });
    }

    [Fact]
    public void Tick_WithoutDraw_UpdatesButDoesNotDraw()
    {
        OnGameThread(() =>
        {
            var (game, host) = StartGame();
            game.Calls.Clear();

            Assert.False(host.Tick(At(16), canDraw: false));

            Assert.Equal(["Update"], game.Calls);
            Assert.False(game.FirstFrameDrawn.IsCompleted);
        });
    }

    [Fact]
    public void PauseAndResume_RunTheHooks_AndAnInactiveGameKeepsItsPicture()
    {
        OnGameThread(() =>
        {
            var (game, host) = StartGame();
            Assert.True(host.Tick(At(16), canDraw: true));

            _ = host.PauseAsync();
            Assert.False(game.IsActiveNow);
            Assert.True(host.Tick(At(33), canDraw: true));

            // Hidden: the platform says there's nothing to draw on
            Assert.False(host.Tick(At(50), canDraw: false));

            _ = host.ResumeAsync();
            Assert.True(game.IsActiveNow);
            Assert.True(host.Tick(At(66), canDraw: true));

            Assert.Contains("Pause", game.Calls);
            Assert.Contains("Resume", game.Calls);
        });
    }

    [Fact]
    public void AnInactiveGame_StopsGameTimeAndUpdates_ButDrawsAndRunsContinuations()
    {
        OnGameThread(() =>
        {
            var (game, host) = StartGame();
            host.Tick(At(16), canDraw: true);
            host.Tick(At(33), canDraw: true);
            var total = game.Time.Total;
            game.Calls.Clear();

            _ = host.PauseAsync();
            bool ran = false;
            DotGame.Framework.Threading.GameThread.Post(() => ran = true);

            host.Tick(At(50), canDraw: true);
            host.Tick(At(66), canDraw: true);

            Assert.True(ran);
            Assert.Equal(total, game.Time.Total);
            Assert.DoesNotContain("Update", game.Calls);
        });
    }

    [Fact]
    public void RunWhenInactive_KeepsGameTimeAndUpdatesGoing()
    {
        OnGameThread(() =>
        {
            var (game, host) = StartGame();
            game.RunWhenInactive = true;
            host.Tick(At(16), canDraw: true);
            var total = game.Time.Total;

            _ = host.PauseAsync();
            game.Calls.Clear();
            host.Tick(At(33), canDraw: true);

            Assert.True(game.Time.Total > total);
            Assert.Contains("Update", game.Calls);
        });
    }

    [Fact]
    public void FrameClock_MeasuresFromTimestamps_AndLimitsAFrameToMaxDeltaTime()
    {
        OnGameThread(() =>
        {
            var (game, host) = StartGame();
            var time = game.Time;

            // The first frame after starting has no previous frame to measure from
            host.Tick(At(1000), canDraw: true);
            Assert.Equal(TimeSpan.Zero, time.Delta);

            host.Tick(At(1016), canDraw: true);
            Assert.Equal(At(16), time.Delta);

            // A stall nothing reported (a breakpoint) advances game time by at most the limit
            host.Tick(At(11016), canDraw: true);
            Assert.Equal(TimeService.DefaultMaxDelta, time.Delta);

            time.MaxDelta = At(50);
            host.Tick(At(11216), canDraw: true);
            Assert.Equal(At(50), time.Delta);

            Assert.Equal(At(16) + TimeService.DefaultMaxDelta + At(50), time.Total);
        });
    }

    [Fact]
    public void FrameClock_DoesNotCountTimeInTheBackground_OrAResetStall()
    {
        OnGameThread(() =>
        {
            var (game, host) = StartGame();
            var time = game.Time;
            host.Tick(At(1000), canDraw: true);
            host.Tick(At(1016), canDraw: true);

            // Resuming: the next frame repeats the last frame's length rather than counting the
            // time in the background
            _ = host.PauseAsync();
            _ = host.ResumeAsync();
            host.Tick(At(60000), canDraw: true);
            Assert.Equal(At(16), time.Delta);

            // A stall the platform reports (a lost context's recovery) likewise
            host.Tick(At(60020), canDraw: true);
            host.ResetFrameClock();
            host.Tick(At(62000), canDraw: true);
            Assert.Equal(At(20), time.Delta);
        });
    }

    [Fact]
    public void MaxDelta_MustBePositive()
    {
        OnGameThread(() =>
        {
            var (game, _) = StartGame();

            Assert.Throws<ArgumentOutOfRangeException>(() => game.Time.MaxDelta = TimeSpan.Zero);
        });
    }

    [Fact]
    public void FixedUpdateRate_SetsTheStep_AndMustBePositive()
    {
        OnGameThread(() =>
        {
            var (game, _) = StartGame();

            Assert.Null(game.FixedUpdateRate);
            Assert.Null(game.Time.FixedStep);

            game.FixedUpdateRate = 30;

            Assert.Equal(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30), game.Time.FixedStep);
            Assert.Throws<ArgumentOutOfRangeException>(() => game.FixedUpdateRate = 0);
        });
    }

    [Fact]
    public void FixedUpdates_RunOnlyOnceARateIsSet_AndStopWhenItsCleared()
    {
        OnGameThread(() =>
        {
            var (game, host) = StartGame();
            host.Tick(At(16), canDraw: true);
            host.Tick(At(32), canDraw: true);
            Assert.DoesNotContain("FixedUpdate", game.Calls);

            game.FixedUpdateRate = 1000;
            game.Calls.Clear();
            host.Tick(At(48), canDraw: true);
            Assert.Equal(8, game.Calls.FindAll(call => call == "FixedUpdate").Count);
            Assert.True(game.Calls.IndexOf("FixedUpdate") < game.Calls.IndexOf("Update"));

            game.FixedUpdateRate = null;
            game.Calls.Clear();
            host.Tick(At(64), canDraw: true);
            Assert.DoesNotContain("FixedUpdate", game.Calls);
            Assert.Contains("Update", game.Calls);
        });
    }

    [Fact]
    public void SetDisplay_StoresTheDisplay_AndReportsChangesOnceStarted()
    {
        OnGameThread(() =>
        {
            var game = new TestGame();
            var host = new GameHost(game);
            var display = new DisplayInfo(new RectangleInt(0, 0, 800, 600), new RectangleInt(0, 20, 800, 580), 144f, 120);

            // Before startup, so the game's OnInitialize can read it
            host.SetDisplay(display);
            Assert.Equal(display.ClientBounds, game.ClientBounds);
            Assert.Equal(display.SafeAreaBounds, game.SafeAreaBounds);
            Assert.Equal(144f, game.DisplayDpi);

            host.BeginStartup(CreateServices());
            game.FinishStart();
            host.Tick(At(0), canDraw: true);
            Assert.Equal(0, game.BoundsChanges);

            // A density that isn't known keeps the last
            host.SetDisplay(display with { ClientBounds = new RectangleInt(0, 0, 1024, 768), Dpi = 0f });
            Assert.Equal(1, game.BoundsChanges);
            Assert.Equal(new RectangleInt(0, 0, 1024, 768), game.ClientBounds);
            Assert.Equal(144f, game.DisplayDpi);
        });
    }

    [Fact]
    public void SetDisplay_WhileStarting_IsReportedOnceStarted()
    {
        OnGameThread(() =>
        {
            var game = new TestGame();
            var host = new GameHost(game);
            var display = new DisplayInfo(new RectangleInt(0, 0, 800, 600), new RectangleInt(0, 0, 800, 600), 144f, 120);
            host.SetDisplay(display);
            host.BeginStartup(CreateServices());

            // The same display again is no change
            host.SetDisplay(display);
            game.FinishStart();
            host.Tick(At(0), canDraw: true);
            Assert.Equal(0, game.BoundsChanges);

            // The safe area changes while starting (an iOS home-screen app's insets arrive late)
            var lateGame = new TestGame();
            var lateHost = new GameHost(lateGame);
            lateHost.SetDisplay(display);
            lateHost.BeginStartup(CreateServices());
            lateHost.SetDisplay(display with { SafeAreaBounds = new RectangleInt(0, 47, 800, 553) });
            Assert.Equal(0, lateGame.BoundsChanges);

            lateGame.FinishStart();
            lateHost.Tick(At(0), canDraw: true);
            Assert.True(lateHost.IsStarted);
            Assert.Equal(1, lateGame.BoundsChanges);
            Assert.Equal(new RectangleInt(0, 47, 800, 553), lateGame.SafeAreaBounds);
        });
    }

    [Fact]
    public void Input_BeforeStarting_IsDiscarded_AndAfterStarting_IsDelivered()
    {
        OnGameThread(() =>
        {
            var game = new TestGame();
            var host = new GameHost(game);
            var tap = new PointerEventData
            {
                State = PointerEventKind.Began,
                PointerId = 1,
                PointerType = PointerDevice.Touch,
                Position = new System.Numerics.Vector2(10, 20),
            };

            // Before the game's services exist, and while it starts
            host.QueuePointerEvent(tap);
            host.BeginStartup(CreateServices());
            host.QueuePointerEvent(tap);
            host.Tick(At(0), canDraw: true);
            game.FinishStart();
            host.Tick(At(16), canDraw: true);

            host.Tick(At(33), canDraw: true);
            Assert.Equal(0, game.PointerEvents);

            host.QueuePointerEvent(tap);
            host.QueueKeyEvent(new KeyEventData { State = KeyEventKind.Pressed, Key = Key.A });
            host.Tick(At(50), canDraw: true);
            Assert.Equal(1, game.PointerEvents);
        });
    }

    [Fact]
    public void Pause_CancelsAHeldMouseButton_AndDropsTheRestOfItsPress()
    {
        OnGameThread(() =>
        {
            var (game, host) = StartGame();
            host.QueuePointerEvent(Mouse(PointerEventKind.Began, 10, PointerButton.Left, PointerButton.Left));
            host.QueuePointerEvent(Mouse(PointerEventKind.Moved, 30, PointerButton.None, PointerButton.Left));
            host.Tick(At(16), canDraw: true);

            _ = host.PauseAsync();
            host.Tick(At(33), canDraw: true);

            var canceled = game.PointerEventLog[^1];
            Assert.Equal(PointerEventKind.Canceled, canceled.Kind);
            Assert.Equal(PointerDevice.Mouse, canceled.Device);
            Assert.Equal(PointerButton.Left, canceled.Button);
            Assert.Equal(PointerButton.None, canceled.HeldButtons);
            Assert.Equal(30, canceled.Position.X);
            Assert.Equal(0, canceled.TouchCount);

            // The release the platform delivers once the window is back is dropped, and the
            // mouse still moves, without the canceled press's buttons
            _ = host.ResumeAsync();
            game.PointerEventLog.Clear();
            host.QueuePointerEvent(Mouse(PointerEventKind.Moved, 40, PointerButton.None, PointerButton.Left));
            host.QueuePointerEvent(Mouse(PointerEventKind.Ended, 40, PointerButton.Left, PointerButton.None));
            host.QueuePointerEvent(Mouse(PointerEventKind.Began, 50, PointerButton.Left, PointerButton.Left));
            host.QueuePointerEvent(Mouse(PointerEventKind.Ended, 50, PointerButton.Left, PointerButton.None));
            host.Tick(At(50), canDraw: true);

            Assert.Equal(3, game.PointerEventLog.Count);
            Assert.Equal(PointerEventKind.Moved, game.PointerEventLog[0].Kind);
            Assert.Equal(PointerButton.None, game.PointerEventLog[0].HeldButtons);
            Assert.Equal(PointerEventKind.Began, game.PointerEventLog[1].Kind);
            Assert.Equal(PointerEventKind.Ended, game.PointerEventLog[2].Kind);

            // Nothing is held now, so another pause cancels nothing
            _ = host.PauseAsync();
            host.Tick(At(66), canDraw: true);
            Assert.Equal(3, game.PointerEventLog.Count);
        });
    }

    [Fact]
    public void Pause_CancelsEveryTouch_CountingDownToNoTouches()
    {
        OnGameThread(() =>
        {
            var (game, host) = StartGame();
            host.QueuePointerEvent(Touch(PointerEventKind.Began, 1));
            host.QueuePointerEvent(Touch(PointerEventKind.Began, 2));
            host.Tick(At(16), canDraw: true);

            game.PointerEventLog.Clear();
            _ = host.PauseAsync();
            host.Tick(At(33), canDraw: true);

            Assert.Equal(2, game.PointerEventLog.Count);
            Assert.All(game.PointerEventLog, e => Assert.Equal(PointerEventKind.Canceled, e.Kind));
            Assert.Equal(1, game.PointerEventLog[0].TouchCount);
            Assert.Equal(0, game.PointerEventLog[1].TouchCount);

            // The rest of a canceled touch is dropped; a new touch with its id is its own
            game.PointerEventLog.Clear();
            host.QueuePointerEvent(Touch(PointerEventKind.Moved, 1));
            host.QueuePointerEvent(Touch(PointerEventKind.Ended, 1));
            host.QueuePointerEvent(Touch(PointerEventKind.Began, 2));
            host.Tick(At(50), canDraw: true);

            Assert.Single(game.PointerEventLog);
            Assert.Equal(PointerEventKind.Began, game.PointerEventLog[0].Kind);
            Assert.Equal(1, game.PointerEventLog[0].TouchCount);
        });
    }

    private static PointerEventData Mouse(
        PointerEventKind state,
        float x,
        PointerButton button,
        PointerButton heldButtons)
    {
        return new PointerEventData
        {
            State = state,
            PointerId = 0,
            PointerType = PointerDevice.Mouse,
            Position = new System.Numerics.Vector2(x, 20),
            Button = button,
            HeldButtons = heldButtons,
        };
    }

    private static PointerEventData Touch(
        PointerEventKind state,
        int pointerId)
    {
        return new PointerEventData
        {
            State = state,
            PointerId = pointerId,
            PointerType = PointerDevice.Touch,
            Position = new System.Numerics.Vector2(10, 20),
        };
    }

    // A started game, whose first frame hasn't run yet
    private static (TestGame Game, GameHost Host) StartGame()
    {
        var game = new TestGame();
        var host = new GameHost(game);

        host.BeginStartup(CreateServices());
        game.FinishStart();
        host.Tick(At(0), canDraw: true);
        Assert.True(host.IsStarted);

        return (game, host);
    }
}
