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
    }

    public void Dispose()
    {
        StatsCollector.SetMode(StatsOverlayMode.Off);
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
        StatsCollector.ExcludeOverlayDraw(5000);
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
