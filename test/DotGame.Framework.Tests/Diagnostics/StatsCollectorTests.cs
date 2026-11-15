// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Diagnostics;
using DotGame.Framework.Diagnostics;
using DotGame.Framework.Tests.Platform;
using Xunit;

namespace DotGame.Framework.Tests.Diagnostics;

/// <summary>
/// The overlay's numbers: what each mode collects, the rolling window, and that framework time is
/// bracketed properly. Counts are exact; times are only checked against each other.
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class StatsCollectorTests : IDisposable
{
    private const int Window = 60;

    public StatsCollectorTests()
    {
        StatsCollector.SetMode(StatsOverlayMode.Off);
        StatsCollector.ResetDisplayEstimate();
    }

    public void Dispose()
    {
        StatsCollector.SetMode(StatsOverlayMode.Off);
        StatsCollector.SetDisplayRefreshRate(0);
        StatsCollector.SetPreferredFrameRate(null);
        StatsCollector.ResetDisplayEstimate();
    }

    [Fact]
    public void AnUnreportedDisplayRate_IsEstimatedFromTheFrameTimestamps_AndKeptWhenTheGameSlows()
    {
        // A 48 Hz display the platform can't report, as on the web: a frame's timestamp is when
        // its refresh began, so its interval is a whole number of refreshes. The game misses
        // some, so most intervals are two or three refreshes, never less than one.
        const double Refresh = 1000.0 / 48.0;
        double time = 1000;
        int[] refreshes = [1, 2, 2, 1, 3, 2, 1, 2];
        for (int i = 0; i < 64; i++)
        {
            time += refreshes[i % refreshes.Length] * Refresh;
            StatsCollector.RecordFrameTimestamp(TimeSpan.FromMilliseconds(time));
        }

        // Then the game slows to every other refresh for good
        for (int i = 0; i < 200; i++)
        {
            time += 2 * Refresh;
            StatsCollector.RecordFrameTimestamp(TimeSpan.FromMilliseconds(time));
        }

        StatsCollector.SetMode(StatsOverlayMode.FpsOnly);
        Frame();
        Publish();

        Assert.Equal(48f, StatsCollector.Snapshot.DisplayHz, 0.01f);
    }

    [Fact]
    public void AReportedDisplayRate_IsntEstimated()
    {
        StatsCollector.SetDisplayRefreshRate(60);
        double time = 1000;
        for (int i = 0; i < 64; i++)
        {
            time += 50;
            StatsCollector.RecordFrameTimestamp(TimeSpan.FromMilliseconds(time));
        }

        StatsCollector.SetMode(StatsOverlayMode.FpsOnly);
        Frame();
        Publish();

        Assert.Equal(60f, StatsCollector.Snapshot.DisplayHz, 0.01f);
    }

    [Fact]
    public void APreferredRate_BelowTheDisplays_IsTheTarget()
    {
        // A 500 Hz display and a game that prefers 50: a frame has 20 ms, ten refreshes, and
        // missed it past 21 ms
        StatsCollector.SetDisplayRefreshRate(500);
        StatsCollector.SetPreferredFrameRate(50);
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Frame();
        Frame(update: () => Thread.Sleep(4));
        Publish();

        var snapshot = StatsCollector.Snapshot;
        Assert.Equal(500f, snapshot.DisplayHz);
        Assert.Equal(50f, snapshot.TargetHz, 0.01f);
        Assert.Equal(0, snapshot.DroppedFrames);

        // 4 ms of update over two frames is about 2 ms a frame, a tenth of the target's 20
        Assert.True(snapshot.CpuPercent < 50f);
    }

    [Fact]
    public void Detailed_CountsTheFramesThatMissedTheDisplay_AndTheGameThreadsShareOfEachFrame()
    {
        // A 500 Hz display: a frame over 3 ms missed it, and 2 ms is a whole refresh interval
        StatsCollector.SetDisplayRefreshRate(500);
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Frame();
        Frame(update: () => Thread.Sleep(20));
        Publish();

        var snapshot = StatsCollector.Snapshot;
        Assert.Equal(500f, snapshot.DisplayHz);
        Assert.Equal(1, snapshot.DroppedFrames);

        // 20 ms of update over two frames is about 10 ms a frame, five refresh intervals
        Assert.True(snapshot.CpuPercent > 400f);
    }

    [Fact]
    public void TheOverlaysOwnTime_IsntAFrameDropped_ButStaysInTheFrame()
    {
        // A 500 Hz display: a frame over 3 ms missed it
        StatsCollector.SetDisplayRefreshRate(500);
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        // A quick frame, then 20 ms of the overlay drawing itself after it
        Frame();
        long start = Stopwatch.GetTimestamp();
        Thread.Sleep(20);
        StatsCollector.ExcludeOverlayTime(Stopwatch.GetTimestamp() - start);
        Publish();

        var snapshot = StatsCollector.Snapshot;
        Assert.Equal(0, snapshot.DroppedFrames);
        Assert.True(snapshot.FrameMs >= 20f);
    }

    [Fact]
    public void AnUnknownDisplayRate_IsEstimatedFromTheFrames()
    {
        StatsCollector.SetMode(StatsOverlayMode.FpsOnly);

        Frame();
        Publish();

        var snapshot = StatsCollector.Snapshot;
        Assert.Equal(1000f / snapshot.FrameMs, snapshot.DisplayHz, snapshot.DisplayHz * 1e-4f);
        Assert.Equal(0, snapshot.DroppedFrames);
    }

    // A frame's work, published by the BeginUpdate of the next
    private static void Frame(
        int drawCalls = 0,
        int spritesPerDraw = 0,
        Action? update = null,
        Action? draw = null)
    {
        StatsCollector.BeginUpdate();
        update?.Invoke();
        StatsCollector.EndUpdate();

        StatsCollector.BeginDraw();
        draw?.Invoke();
        for (int i = 0; i < drawCalls; i++)
        {
            StatsCollector.CountDraw(spritesPerDraw);
        }

        StatsCollector.EndDraw();
    }

    private static void Publish()
    {
        WaitATick();
        StatsCollector.BeginUpdate();
    }

    private static void WaitATick()
    {
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() == start)
        {
            Thread.SpinWait(10);
        }
    }

    [Fact]
    public void Off_CollectsNothing()
    {
        Frame(drawCalls: 3, spritesPerDraw: 10);
        Frame(drawCalls: 3, spritesPerDraw: 10);
        Publish();

        Assert.Equal(default, StatsCollector.Snapshot);
    }

    [Fact]
    public void FpsOnly_MeasuresFrames_AndNothingElse()
    {
        StatsCollector.SetMode(StatsOverlayMode.FpsOnly);

        // Any framework brackets are no-ops in this mode
        Frame(drawCalls: 3, spritesPerDraw: 10, update: () =>
        {
            StatsCollector.EnterFramework();
            WaitATick();
            StatsCollector.ExitFramework();
        });
        Publish();

        var snapshot = StatsCollector.Snapshot;
        Assert.True(snapshot.FrameMs > 0f);
        Assert.Equal(1000f / snapshot.FrameMs, snapshot.Fps, snapshot.Fps * 1e-4f);
        Assert.Equal(0f, snapshot.UpdateMs);
        Assert.Equal(0f, snapshot.UpdateFrameworkMs);
        Assert.Equal(0f, snapshot.DrawMs);
        Assert.Equal(0f, snapshot.DrawCalls);
        Assert.Equal(0f, snapshot.Sprites);
    }

    [Fact]
    public void Detailed_CountsDrawsAndSprites_Exactly()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Frame(drawCalls: 3, spritesPerDraw: 10);
        Publish();

        Assert.Equal(3f, StatsCollector.Snapshot.DrawCalls);
        Assert.Equal(30f, StatsCollector.Snapshot.Sprites);
    }

    [Fact]
    public void TheFirstBeginUpdate_PublishesNothing_AsThereIsNoFrameBeforeIt()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        StatsCollector.BeginUpdate();

        Assert.Equal(default, StatsCollector.Snapshot);
    }

    [Fact]
    public void CountDraws_AddsSeveralAtOnce()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Frame(draw: () => StatsCollector.CountDraws(4, 100));
        Publish();

        Assert.Equal(4f, StatsCollector.Snapshot.DrawCalls);
        Assert.Equal(100f, StatsCollector.Snapshot.Sprites);
    }

    [Fact]
    public void ExcludeOverlayDraw_TakesTheOverlaysOwnBatchOut()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Frame(drawCalls: 3, spritesPerDraw: 10);
        StatsCollector.CountDraw(5000);
        StatsCollector.ExcludeOverlayDraw(1, 5000);
        Publish();

        Assert.Equal(3f, StatsCollector.Snapshot.DrawCalls);
        Assert.Equal(30f, StatsCollector.Snapshot.Sprites);
    }

    [Fact]
    public void Averages_CoverTheFramesSoFar_BeforeTheWindowFills()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Frame(drawCalls: 2);
        Frame(drawCalls: 4);
        Frame(drawCalls: 6);
        Publish();

        Assert.Equal(4f, StatsCollector.Snapshot.DrawCalls);
    }

    [Fact]
    public void Averages_CoverOnlyTheLastWindowOfFrames()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        // Enough old frames to wrap the ring, each differing, whose counts must have left it
        for (int i = 0; i < Window * 2; i++)
        {
            Frame(drawCalls: 1000);
        }

        for (int i = 0; i < Window; i++)
        {
            Frame(drawCalls: i);
        }

        Publish();

        Assert.Equal((Window - 1) / 2f, StatsCollector.Snapshot.DrawCalls);
    }

    [Fact]
    public void AFrameWithoutADraw_CountsAsNoDraws_InTheAverage()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Frame(drawCalls: 4);
        StatsCollector.BeginUpdate();
        StatsCollector.EndUpdate();
        Publish();

        Assert.Equal(2f, StatsCollector.Snapshot.DrawCalls);
    }

    [Fact]
    public void Detailed_SplitsAPhase_IntoFrameworkAndApp()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        void Work()
        {
            StatsCollector.EnterFramework();
            WaitATick();
            StatsCollector.ExitFramework();
            WaitATick();
        }

        Frame(update: Work, draw: Work);
        Publish();

        var snapshot = StatsCollector.Snapshot;
        Assert.True(snapshot.UpdateFrameworkMs > 0f);
        Assert.True(snapshot.UpdateMs >= snapshot.UpdateFrameworkMs);
        Assert.Equal(snapshot.UpdateMs - snapshot.UpdateFrameworkMs, snapshot.UpdateAppMs, 0.001f);
        Assert.True(snapshot.DrawFrameworkMs > 0f);
        Assert.True(snapshot.DrawMs >= snapshot.DrawFrameworkMs);
        Assert.Equal(snapshot.DrawMs - snapshot.DrawFrameworkMs, snapshot.DrawAppMs, 0.001f);
        Assert.True(snapshot.FrameMs >= snapshot.UpdateMs + snapshot.DrawMs);
    }

    [Fact]
    public void NestedFrameworkCalls_CountOnce()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Frame(update: () =>
        {
            StatsCollector.EnterFramework();
            WaitATick();
            StatsCollector.EnterFramework();
            WaitATick();
            StatsCollector.ExitFramework();
            WaitATick();
            StatsCollector.ExitFramework();
        });
        Publish();

        var snapshot = StatsCollector.Snapshot;
        Assert.True(snapshot.UpdateFrameworkMs > 0f);

        // Counted once, the framework's time can't be more than the phase it's in
        Assert.True(snapshot.UpdateFrameworkMs <= snapshot.UpdateMs);
    }

    [Fact]
    public void FrameworkTimeOutsideAPhase_CountsNowhere()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Frame();
        StatsCollector.EnterFramework();
        WaitATick();
        StatsCollector.ExitFramework();
        Publish();

        Assert.Equal(0f, StatsCollector.Snapshot.UpdateFrameworkMs);
        Assert.Equal(0f, StatsCollector.Snapshot.DrawFrameworkMs);
    }

    [Fact]
    public void Suspend_LeavesTheOverlaysOwnCallsOut()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Frame(update: () =>
        {
            StatsCollector.Suspend();
            StatsCollector.EnterFramework();
            WaitATick();
            StatsCollector.ExitFramework();
            StatsCollector.Resume();
        });
        Publish();

        Assert.Equal(0f, StatsCollector.Snapshot.UpdateFrameworkMs);
    }

    [Fact]
    public void Resume_MakesItCollectAgain()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);
        StatsCollector.Suspend();
        StatsCollector.Resume();

        Frame(update: () =>
        {
            StatsCollector.EnterFramework();
            WaitATick();
            StatsCollector.ExitFramework();
        });
        Publish();

        Assert.True(StatsCollector.Snapshot.UpdateFrameworkMs > 0f);
    }

    [Fact]
    public void ChangingMode_ClearsEverything()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);
        Frame(drawCalls: 1000);
        Publish();
        Assert.Equal(1000f, StatsCollector.Snapshot.DrawCalls);

        StatsCollector.SetMode(StatsOverlayMode.FpsOnly);
        Assert.Equal(default, StatsCollector.Snapshot);

        // No sample of the old mode's survives into the new average
        StatsCollector.SetMode(StatsOverlayMode.Detailed);
        Frame(drawCalls: 2);
        Publish();
        Assert.Equal(2f, StatsCollector.Snapshot.DrawCalls);
    }

    [Fact]
    public void ChangingMode_ToTheSameOne_KeepsWhatItHas()
    {
        StatsCollector.SetMode(StatsOverlayMode.Detailed);
        Frame(drawCalls: 7);
        Publish();

        StatsCollector.SetMode(StatsOverlayMode.Detailed);

        Assert.Equal(7f, StatsCollector.Snapshot.DrawCalls);
    }
}
