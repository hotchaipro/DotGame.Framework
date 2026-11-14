// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Diagnostics;
using DotGame.Framework.Input;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;
using DotGame.Framework.Threading;
using Xunit;
using static DotGame.Framework.Tests.Platform.TestPlatform;

namespace DotGame.Framework.Tests.Platform;

/// <summary>
/// What runs in a frame and in what order, when <see cref="Game.OnClientBoundsChanged"/> fires
/// (finding C21), and what a fault during startup does. What a frame does while the game is
/// inactive is left to the pause rule (decision 1).
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class GameHostFrameTests : IDisposable
{
    private static readonly DisplayInfo Display = new(new RectangleInt(0, 0, 800, 600), new RectangleInt(0, 20, 800, 580), 144f, 60);

    private readonly List<Exception?> _fatal = [];

    public GameHostFrameTests()
    {
        FatalError.SetUnwindForTests(true);
        Trace.FatalException += this.OnFatal;
    }

    public void Dispose()
    {
        Trace.FatalException -= this.OnFatal;
        FatalError.SetUnwindForTests(false);
    }

    private void OnFatal(
        object? sender,
        FatalExceptionEventArgs e)
    {
        this._fatal.Add(e.Exception);
    }

    // ── A frame ────────────────────────────────────────────────────────

    [Fact]
    public void AFrame_RunsInItsOrder()
    {
        OnGameThread(() =>
        {
            var ran = new List<string>();
            var game = new ScriptedGame
            {
                Update = () => ran.Add("update"),
                Draw = () => ran.Add("draw"),
                Pointer = _ => ran.Add("pointer"),
                Key = _ => ran.Add("key"),
            };
            var host = Started(game);
            host.Tick(At(16), canDraw: true);
            ran.Clear();

            game.Time.Delay(TimeSpan.Zero, () => ran.Add("delay"));
            GameThread.Post(() => ran.Add("continuation"));
            host.QueueKeyEvent(new KeyEventData { State = KeyEventKind.Pressed, Key = Key.A });
            host.QueuePointerEvent(new PointerEventData { State = PointerEventKind.Moved, PointerId = 0, PointerType = PointerDevice.Mouse, Position = Vector2.One });

            Assert.True(host.Tick(At(33), canDraw: true));

            // A continuation's changes are seen by this frame's update, and so by its draw
            Assert.Equal(["delay", "pointer", "key", "continuation", "update", "draw"], ran);
        });
    }

    [Fact]
    public void AFrameThatCantDraw_DoesNotCountAsTheFirstFrame()
    {
        OnGameThread(() =>
        {
            int draws = 0;
            var game = new ScriptedGame { Draw = () => draws++ };
            var host = Started(game);

            Assert.False(host.Tick(At(16), canDraw: false));
            Assert.Equal(0, draws);
            Assert.False(game.FirstFrameDrawn.IsCompleted);

            Assert.True(host.Tick(At(33), canDraw: true));
            Assert.Equal(1, draws);
            Assert.True(game.FirstFrameDrawn.IsCompleted);
        });
    }

    [Fact]
    public void FirstFrameDrawn_ResumesItsAwaiter_InALaterPump_NotInsideTheFrame()
    {
        OnGameThread(() =>
        {
            int gameThread = Environment.CurrentManagedThreadId;
            int resumedOn = 0;
            var game = new ScriptedGame();
            var host = Started(game);

            async Task Body()
            {
                await game.FirstFrameDrawn;
                resumedOn = Environment.CurrentManagedThreadId;
            }

            _ = Body();
            host.Tick(At(16), canDraw: true);
            Assert.Equal(0, resumedOn);

            PumpUntil(() => resumedOn != 0);
            Assert.Equal(gameThread, resumedOn);
        });
    }

    [Fact]
    public void ADisposedGame_DoesNotDraw()
    {
        OnGameThread(() =>
        {
            int draws = 0;
            var game = new ScriptedGame { Draw = () => draws++ };
            var host = Started(game);

            game.Dispose();
            game.Dispose();

            Assert.False(game.IsActiveNow);
            Assert.False(host.Tick(At(16), canDraw: true));
            Assert.Equal(0, draws);
        });
    }

    [Fact]
    public void ATickBeforeStartup_RunsNothing()
    {
        OnGameThread(() =>
        {
            int updates = 0;
            var host = new GameHost(new ScriptedGame { Update = () => updates++ });

            host.ResetFrameClock();

            Assert.False(host.Tick(At(0), canDraw: true));
            Assert.Equal(0, updates);
            Assert.False(host.IsStarted);
        });
    }

    [Fact]
    public void Stop_Twice_RunsOnStopOnce()
    {
        OnGameThread(() =>
        {
            int stops = 0;
            var host = Started(new ScriptedGame
            {
                Stop = () =>
                {
                    stops++;
                    return Task.CompletedTask;
                },
            });

            Assert.True(host.StopAsync().IsCompletedSuccessfully);
            Assert.True(host.StopAsync().IsCompletedSuccessfully);

            Assert.Equal(1, stops);
        });
    }

    [Fact]
    public void Constructor_AndBeginStartup_RefuseNull()
    {
        Assert.Throws<ArgumentNullException>(() => new GameHost(null!));

        OnGameThread(() =>
        {
            var host = new GameHost(new ScriptedGame());

            Assert.Throws<ArgumentNullException>(() => host.BeginStartup(null!));
        });
    }

    public static TheoryData<string> GameThreadOnlyMembers =>
        ["BeginStartup", "PauseAsync", "ResumeAsync", "StopAsync", "SetDisplay"];

    [Theory]
    [MemberData(nameof(GameThreadOnlyMembers))]
    public void GameThreadOnlyMembers_AreFatalOnAnotherThread(
        string member)
    {
        var host = new GameHost(new ScriptedGame());

        void Call()
        {
            switch (member)
            {
                case "BeginStartup":
                    host.BeginStartup(CreateServices());
                    break;
                case "PauseAsync":
                    _ = host.PauseAsync();
                    break;
                case "ResumeAsync":
                    _ = host.ResumeAsync();
                    break;
                case "StopAsync":
                    _ = host.StopAsync();
                    break;
                default:
                    host.SetDisplay(Display);
                    break;
            }
        }

        // NOTE: StopAsync is async, so its fatal ends the returned task rather than the call
        _ = Record.Exception(Call);

        Assert.Single(this._fatal);
    }

    // ── The display (C21) ──────────────────────────────────────────────

    public static TheoryData<string> DisplayChanges => ["bounds", "safe area", "density"];

    private static DisplayInfo Changed(
        string change)
    {
        return change switch
        {
            "bounds" => Display with { ClientBounds = new RectangleInt(0, 0, 1024, 768) },
            "safe area" => Display with { SafeAreaBounds = new RectangleInt(0, 47, 800, 553) },
            _ => Display with { Dpi = 288f },
        };
    }

    [Theory]
    [MemberData(nameof(DisplayChanges))]
    public void SetDisplay_WithAChange_ReportsItOnce(
        string change)
    {
        OnGameThread(() =>
        {
            int changes = 0;
            var game = new ScriptedGame { BoundsChanged = () => changes++ };
            var host = new GameHost(game);
            host.SetDisplay(Display);
            StartHost(host);

            host.SetDisplay(Changed(change));
            Assert.Equal(1, changes);

            // Reporting it again is no change
            host.SetDisplay(Changed(change));
            Assert.Equal(1, changes);
        });
    }

    [Fact]
    public void SetDisplay_WithNoChange_DoesNotReportOne()
    {
        OnGameThread(() =>
        {
            int changes = 0;
            var game = new ScriptedGame { BoundsChanged = () => changes++ };
            var host = new GameHost(game);
            host.SetDisplay(Display);
            StartHost(host);

            host.SetDisplay(Display);

            // Only the refresh rate, which is the budget's business, not the game's layout's
            host.SetDisplay(Display with { RefreshRateHz = 120 });

            // A density the platform can't tell keeps the last
            host.SetDisplay(Display with { Dpi = 0f });

            Assert.Equal(0, changes);
            Assert.Equal(144f, game.DisplayDpi);
        });
    }

    [Fact]
    public void SetDisplay_ChangedWhileStopping_IsNotReportedToAStoppedGame()
    {
        OnGameThread(() =>
        {
            int changes = 0;
            var gate = new TaskCompletionSource();
            var game = new ScriptedGame
            {
                Start = async _ => await gate.Task,
                BoundsChanged = () => changes++,
            };
            var host = new GameHost(game);
            host.SetDisplay(Display);
            host.BeginStartup(CreateServices());

            host.SetDisplay(Changed("bounds"));
            var stop = host.StopAsync();
            gate.SetResult();
            PumpUntil(() => stop.IsCompleted);

            Assert.Equal(0, changes);
            Assert.Equal(Changed("bounds").ClientBounds, game.ClientBounds);

            // A stopped game hears no more about it
            host.SetDisplay(Display);
            Assert.Equal(0, changes);
        });
    }

    // ── Faults during startup ──────────────────────────────────────────

    [Fact]
    public void OnInitializeThatThrows_IsFatal_AndTheGameNeverStarts()
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("From OnInitialize");
            bool started = false;
            int updates = 0;
            var host = new GameHost(new ScriptedGame
            {
                Initialize = _ => throw fault,
                Start = _ =>
                {
                    started = true;
                    return Task.CompletedTask;
                },
                Update = () => updates++,
            });

            host.BeginStartup(CreateServices());

            Assert.Equal([fault], this._fatal);
            Assert.Same(fault, host.StartupFault);
            Assert.False(host.IsStarted);
            Assert.False(started);

            Assert.False(host.Tick(At(0), canDraw: true));
            Assert.Equal(0, updates);
        });
    }

    [Fact]
    public void OnStartThatFaultsAfterAnAwait_IsFatal()
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("From OnStart");
            var host = new GameHost(new ScriptedGame
            {
                Start = async _ =>
                {
                    await Task.Yield();
                    throw fault;
                },
            });

            host.BeginStartup(CreateServices());
            Assert.Empty(this._fatal);

            host.Tick(At(0), canDraw: true);

            Assert.Equal([fault], this._fatal);
            Assert.Same(fault, host.StartupFault);
            Assert.False(host.IsStarted);
            Assert.False(host.Tick(At(16), canDraw: true));
        });
    }

    [Fact]
    public void StopAfterAFaultedStartup_DoesNotRunOnStop()
    {
        OnGameThread(() =>
        {
            int stops = 0;
            var host = new GameHost(new ScriptedGame
            {
                Initialize = _ => throw new InvalidOperationException("From OnInitialize"),
                Stop = () =>
                {
                    stops++;
                    return Task.CompletedTask;
                },
            });
            host.BeginStartup(CreateServices());

            Assert.True(host.StopAsync().IsCompletedSuccessfully);

            Assert.Equal(0, stops);
            Assert.NotNull(host.StartupFault);
        });
    }

    [Fact]
    public void APendingPauseWhoseHookThrows_IsFatal()
    {
        OnGameThread(() =>
        {
            var fault = new InvalidOperationException("From OnPause");
            var gate = new TaskCompletionSource();
            var host = new GameHost(new ScriptedGame
            {
                Start = async _ => await gate.Task,
                Pause = () => throw fault,
            });
            host.BeginStartup(CreateServices());
            _ = host.PauseAsync();

            gate.SetResult();
            PumpUntil(() => this._fatal.Count > 0);

            Assert.Equal([fault], this._fatal);
        });
    }

    [Fact]
    public void TheRestOfATouchThatBeganWhileStarting_IsNotDelivered()
    {
        OnGameThread(() =>
        {
            var events = new List<PointerEvent>();
            var gate = new TaskCompletionSource();
            var game = new ScriptedGame
            {
                Start = async _ => await gate.Task,
                Pointer = e => events.Add(e),
            };
            var host = new GameHost(game);
            host.BeginStartup(CreateServices());

            host.QueuePointerEvent(new PointerEventData { State = PointerEventKind.Began, PointerId = 1, PointerType = PointerDevice.Touch, Position = Vector2.One });
            host.Tick(At(0), canDraw: true);

            gate.SetResult();
            host.Tick(At(16), canDraw: true);
            Assert.True(host.IsStarted);

            host.QueuePointerEvent(new PointerEventData { State = PointerEventKind.Moved, PointerId = 1, PointerType = PointerDevice.Touch, Position = Vector2.One });
            host.QueuePointerEvent(new PointerEventData { State = PointerEventKind.Ended, PointerId = 1, PointerType = PointerDevice.Touch, Position = Vector2.One });
            host.Tick(At(33), canDraw: true);

            Assert.Empty(events);
        });
    }

    // A started game, whose first frame hasn't run yet
    private static GameHost Started(
        ScriptedGame game)
    {
        var host = new GameHost(game);
        StartHost(host);
        return host;
    }

    private static void StartHost(
        GameHost host)
    {
        host.BeginStartup(CreateServices());
        PumpUntil(() => host.IsStarted);
    }
}
