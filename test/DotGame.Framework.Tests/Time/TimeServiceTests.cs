// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Runtime.CompilerServices;
using DotGame.Framework.Diagnostics;
using DotGame.Framework.Platform;
using DotGame.Framework.Tests.Platform;
using DotGame.Framework.Time;
using Xunit;
using static DotGame.Framework.Tests.Platform.TestPlatform;

namespace DotGame.Framework.Tests.Time;

/// <summary>
/// Game time and the work scheduled on it. <see cref="GameHostTests"/> covers the clock as the
/// host drives it; time while paused is left to the pause rule (decision 1).
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class TimeServiceTests
{
    // A clock whose first frame has run, at time zero
    private static TimeService Started()
    {
        var time = new TimeService();
        time.Update(At(0));
        return time;
    }

    // ── The clock ──────────────────────────────────────────────────────

    [Fact]
    public void Update_AdvancesByTheDifferenceBetweenTimestamps()
    {
        var time = new TimeService();

        // Only differences count, so a first timestamp of any size starts at zero
        time.Update(TimeSpan.FromHours(5));
        Assert.Equal(TimeSpan.Zero, time.Delta);
        Assert.Equal(TimeSpan.Zero, time.Total);

        time.Update(TimeSpan.FromHours(5) + At(16));
        Assert.Equal(At(16), time.Delta);
        Assert.Equal(At(16), time.Total);

        time.Update(TimeSpan.FromHours(5) + At(40));
        Assert.Equal(At(24), time.Delta);
        Assert.Equal(At(40), time.Total);
    }

    [Fact]
    public void Update_WithATimestampThatGoesBackwards_DoesNotReverseTime()
    {
        var time = Started();
        time.Update(At(100));

        time.Update(At(50));

        Assert.Equal(TimeSpan.Zero, time.Delta);
        Assert.Equal(At(100), time.Total);

        // It carries on from the new timestamp
        time.Update(At(66));
        Assert.Equal(At(16), time.Delta);
    }

    [Fact]
    public void Update_NeverAdvancesAFrameBeyondTheLimit()
    {
        var time = Started();
        Assert.Equal(TimeService.DefaultMaxDelta, time.MaxDelta);

        time.Update(TimeSpan.FromHours(1));

        Assert.Equal(TimeService.DefaultMaxDelta, time.Delta);
        Assert.Equal(TimeService.DefaultMaxDelta, time.Total);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void MaxDelta_MustBePositive(
        double milliseconds)
    {
        var time = new TimeService();

        Assert.Throws<ArgumentOutOfRangeException>(() => time.MaxDelta = At(milliseconds));
        Assert.Equal(TimeService.DefaultMaxDelta, time.MaxDelta);
    }

    [Fact]
    public void ResetFrameClock_MakesTheNextFrameRepeatTheLast_KeepingGameTime()
    {
        var time = Started();
        time.Update(At(16));

        time.ResetFrameClock();
        time.Update(At(100000));

        Assert.Equal(At(16), time.Delta);
        Assert.Equal(At(32), time.Total);

        time.Update(At(100010));
        Assert.Equal(At(10), time.Delta);
    }

    [Fact]
    public void Reset_StartsGameTimeAgain()
    {
        var time = Started();
        time.Update(At(16));

        time.Reset();
        Assert.Equal(TimeSpan.Zero, time.Total);
        Assert.Equal(TimeSpan.Zero, time.Delta);

        time.Update(At(500));
        Assert.Equal(TimeSpan.Zero, time.Total);
    }

    // ── Delay ──────────────────────────────────────────────────────────

    [Fact]
    public void Delay_RunsWhenGameTimeReachesIt_AndOnlyOnce()
    {
        var time = Started();
        int runs = 0;

        time.Delay(At(100), () => runs++);

        time.Update(At(99));
        Assert.Equal(0, runs);

        time.Update(At(100));
        Assert.Equal(1, runs);

        time.Update(At(200));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Delay_CountsGameTime_NotTheClockOnTheWall()
    {
        var time = Started();
        bool ran = false;

        time.MaxDelta = At(100);
        time.Delay(At(150), () => ran = true);

        // A stall of ten seconds is a hundred milliseconds of game time
        time.Update(At(10000));
        Assert.False(ran);

        time.Update(At(10050));
        Assert.True(ran);
    }

    [Fact]
    public void Delay_OfZero_RunsAtTheNextFrame_NotBefore()
    {
        var time = Started();
        bool ran = false;

        time.Delay(TimeSpan.Zero, () => ran = true);
        Assert.False(ran);

        time.Update(At(16));
        Assert.True(ran);
    }

    [Fact]
    public void Delay_RefusesANullAction()
    {
        Assert.Throws<ArgumentNullException>(() => Started().Delay(At(1), null!));
    }

    [Fact]
    public void Cancel_StopsADelay()
    {
        var time = Started();
        bool ran = false;

        var handle = time.Delay(At(10), () => ran = true);
        Assert.False(handle.IsCanceled);

        handle.Cancel();
        Assert.True(handle.IsCanceled);

        time.Update(At(100));
        Assert.False(ran);
    }

    [Fact]
    public void Cancel_AfterItRan_DoesNothing()
    {
        var time = Started();
        int runs = 0;

        var handle = time.Delay(At(10), () => runs++);
        time.Update(At(100));

        handle.Cancel();
        time.Update(At(200));

        Assert.Equal(1, runs);
    }

    [Fact]
    public void ADelayScheduledByADelay_WaitsForAFrame()
    {
        var time = Started();
        var ran = new List<string>();

        // A delay of zero that schedules another can't keep a frame from ending
        time.Delay(TimeSpan.Zero, () =>
        {
            ran.Add("first");
            time.Delay(TimeSpan.Zero, () => ran.Add("second"));
        });

        time.Update(At(16));
        Assert.Equal(["first"], ran);

        time.Update(At(32));
        Assert.Equal(["first", "second"], ran);
    }

    [Fact]
    public void ADelayThatThrows_DoesNotRunAgain_AndTheOthersStillRun()
    {
        var time = Started();
        int thrown = 0;
        int others = 0;

        time.Delay(At(1), () => others++);
        time.Delay(At(1), () =>
        {
            thrown++;
            throw new InvalidOperationException("From a delay");
        });
        time.Delay(At(1), () => others++);

        Assert.Throws<InvalidOperationException>(() => time.Update(At(10)));
        time.Update(At(20));

        Assert.Equal(1, thrown);
        Assert.Equal(2, others);
    }

    [Fact]
    public void DelaysDueOnOneFrame_RunInTheOrderTheyWereDue()
    {
        var time = Started();
        var ran = new List<int>();

        time.Delay(At(30), () => ran.Add(30));
        time.Delay(At(10), () => ran.Add(10));
        time.Delay(At(20), () => ran.Add(20));

        time.Update(At(100));

        Assert.Equal([10, 20, 30], ran);
    }

    [Fact]
    public void DelaysDueTogether_RunInTheOrderTheyWereScheduled()
    {
        var time = Started();
        var ran = new List<int>();

        time.Delay(At(10), () => ran.Add(1));
        time.Delay(At(10), () => ran.Add(2));
        time.Delay(At(10), () => ran.Add(3));

        time.Update(At(100));

        Assert.Equal([1, 2, 3], ran);
    }

    // ── DelayAsync ─────────────────────────────────────────────────────

    [Fact]
    public void NextFrameAsync_CompletesAtTheNextUpdate()
    {
        var time = Started();

#pragma warning disable xUnit1051 // The test's token has nothing to do with game time
        var frame = time.NextFrameAsync();
#pragma warning restore xUnit1051
        Assert.False(frame.IsCompleted);

        time.Update(At(1));
        Assert.True(frame.IsCompletedSuccessfully);
    }

    [Fact]
    public void DelayAsync_CompletesWhenGameTimeReachesIt()
    {
        var time = Started();

#pragma warning disable xUnit1051 // The test's token has nothing to do with game time
        var delay = time.DelayAsync(At(100));
#pragma warning restore xUnit1051
        Assert.False(delay.IsCompleted);

        time.Update(At(99));
        Assert.False(delay.IsCompleted);

        time.Update(At(100));
        Assert.True(delay.IsCompletedSuccessfully);
    }

    [Fact]
    public void DelayAsync_WithAToken_IsCanceledWithIt_AndNeverFires()
    {
        var time = Started();
        using var source = new CancellationTokenSource();

        var delay = time.DelayAsync(At(100), source.Token);
        source.Cancel();

        Assert.True(delay.IsCanceled);
        var canceled = Assert.ThrowsAny<OperationCanceledException>(() => delay.GetAwaiter().GetResult());
        Assert.Equal(source.Token, canceled.CancellationToken);

        time.Update(At(200));
        Assert.True(delay.IsCanceled);
    }

    [Fact]
    public void DelayAsync_WithACanceledToken_IsCanceledAtOnce()
    {
        var time = Started();
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.True(time.DelayAsync(At(100), source.Token).IsCanceled);
    }

    [Fact]
    public void DelayAsync_CanceledAfterItCompleted_StaysCompleted()
    {
        var time = Started();
        using var source = new CancellationTokenSource();

        var delay = time.DelayAsync(At(10), source.Token);
        time.Update(At(100));

        source.Cancel();

        Assert.True(delay.IsCompletedSuccessfully);
    }

    [Fact]
    public void AwaitingDelayAsync_ResumesOnTheGameThread()
    {
        OnGameThread(() =>
        {
            var time = Started();
            int gameThread = Environment.CurrentManagedThreadId;
            int resumedOn = 0;

            async Task Body()
            {
                await time.DelayAsync(At(10));
                resumedOn = Environment.CurrentManagedThreadId;
            }

            var body = Body();
            time.Update(At(100));
            PumpUntil(() => body.IsCompleted);

            Assert.Equal(gameThread, resumedOn);
        });
    }

    [Fact]
    public void AwaitingDelayAsync_ResumesAtThePump_NotInsideTheClock()
    {
        OnGameThread(() =>
        {
            var time = Started();
            bool resumed = false;

            async Task Body()
            {
                await time.DelayAsync(At(10));
                resumed = true;
            }

            _ = Body();
            time.Update(At(100));
            Assert.False(resumed);

            GameHost.DrainContinuations();
            Assert.True(resumed);
        });
    }

    [Fact]
    public void ACompletedDelay_DoesNotStayRegisteredWithItsToken()
    {
        var time = Started();
        using var source = new CancellationTokenSource();

        var delay = CompleteADelay(time, source.Token);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(delay.TryGetTarget(out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Task> CompleteADelay(
        TimeService time,
        CancellationToken token)
    {
        var delay = time.DelayAsync(At(1), token);
        time.Update(time.Total + At(10));

        return new WeakReference<Task>(delay);
    }

    // ── During startup ─────────────────────────────────────────────────

    [Fact]
    public void ADelayFromOnStart_RunsOnceTheGameHasStarted()
    {
        OnGameThread(() =>
        {
            bool ran = false;
            var game = new ScriptedGame
            {
                Start = g =>
                {
                    g.Time.Delay(At(10), () => ran = true);
                    return Task.CompletedTask;
                },
            };
            var host = new GameHost(game);

            host.BeginStartup(CreateServices());
            host.Tick(At(0), canDraw: true);
            Assert.True(host.IsStarted);

            host.Tick(At(100), canDraw: true);
            host.Tick(At(200), canDraw: true);

            Assert.True(ran);
        });
    }

    [Fact]
    public void ADelayAsyncFromOnInitialize_DoesNotWaitForever()
    {
        OnGameThread(() =>
        {
            Task? delay = null;
            var game = new ScriptedGame
            {
                Initialize = g =>
                {
                    delay = g.Time.DelayAsync(At(10));
                    return Task.CompletedTask;
                },
            };
            var host = new GameHost(game);

            host.BeginStartup(CreateServices());
            for (int i = 0; i < 100; i++)
            {
                host.Tick(At(i * 16), canDraw: true);
            }

            Assert.True(delay!.IsCompleted);
        });
    }

    [Fact]
    public void ADelayAsyncAwaitedInOnStart_DoesNotHangTheLaunchSilently()
    {
        OnGameThread(() =>
        {
            var fatal = new List<Exception?>();
            void OnFatal(object? sender, FatalExceptionEventArgs e) => fatal.Add(e.Exception);

            FatalError.SetUnwindForTests(true);
            Trace.FatalException += OnFatal;
            try
            {
                var game = new ScriptedGame
                {
                    Start = async g => await g.Time.DelayAsync(At(10)),
                };
                var host = new GameHost(game);

                host.BeginStartup(CreateServices());
                for (int i = 0; i < 100; i++)
                {
                    host.Tick(At(i * 16), canDraw: true);
                }

                Assert.True(host.IsStarted || (fatal.Count > 0));
            }
            finally
            {
                Trace.FatalException -= OnFatal;
                FatalError.SetUnwindForTests(false);
            }
        });
    }
}
