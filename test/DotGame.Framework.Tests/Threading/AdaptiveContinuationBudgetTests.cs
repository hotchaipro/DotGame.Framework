// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Threading;
using Xunit;

namespace DotGame.Framework.Tests.Threading;

/// <summary>
/// The controller that decides how long each frame's continuation pump may run: it grows a little
/// on a frame with headroom, halves on an overrun, and stays between its floor and ceiling.
/// </summary>
public sealed class AdaptiveContinuationBudgetTests
{
    private const double Tolerance = 0.001;
    private const double FloorMs = 1.0;
    private const double StepMs = 0.5;

    private static readonly TimeSpan OnTime = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan Overrun = TimeSpan.FromMilliseconds(30);

    [Fact]
    public void StartsAtTheFloor()
    {
        var budget = new AdaptiveContinuationBudget(60);

        Assert.Equal(FloorMs, budget.CurrentBudget.TotalMilliseconds, Tolerance);
    }

    [Fact]
    public void AFrameWithHeadroom_GrowsItByOneStep()
    {
        var budget = new AdaptiveContinuationBudget(60);

        var next = budget.Next(OnTime, hasBacklog: true);

        Assert.Equal(FloorMs + StepMs, next.TotalMilliseconds, Tolerance);
        Assert.Equal(next, budget.CurrentBudget);
    }

    [Fact]
    public void AnOverrun_HalvesIt()
    {
        var budget = Grown(60, steps: 6);
        Assert.Equal(4.0, budget.CurrentBudget.TotalMilliseconds, Tolerance);

        Assert.Equal(2.0, budget.Next(Overrun, hasBacklog: true).TotalMilliseconds, Tolerance);
        Assert.Equal(1.0, budget.Next(Overrun, hasBacklog: true).TotalMilliseconds, Tolerance);
    }

    [Fact]
    public void Overruns_NeverTakeItBelowTheFloor()
    {
        var budget = new AdaptiveContinuationBudget(60);

        for (int i = 0; i < 10; i++)
        {
            budget.Next(Overrun, hasBacklog: true);
        }

        Assert.Equal(FloorMs, budget.CurrentBudget.TotalMilliseconds, Tolerance);
    }

    [Theory]
    [InlineData(30.0, 8.0)] // The absolute ceiling, not half of 33 ms
    [InlineData(60.0, 8.0)]
    [InlineData(90.0, 1000.0 / 90.0 / 2.0)]
    [InlineData(120.0, 1000.0 / 120.0 / 2.0)]
    [InlineData(1000.0, 1.0)] // Half the frame is below the floor, and the floor wins
    public void Headroom_NeverTakesItAboveTheCeilingForTheRefreshRate(
        double refreshHz,
        double ceilingMs)
    {
        var budget = Grown(refreshHz, steps: 100, frame: TimeSpan.FromSeconds(1.0 / refreshHz));

        Assert.Equal(ceilingMs, budget.CurrentBudget.TotalMilliseconds, Tolerance);
    }

    [Fact]
    public void WithNoBacklog_ItHoldsSteady()
    {
        var budget = Grown(60, steps: 2);
        var before = budget.CurrentBudget;

        // Neither an idle stretch nor a slow idle frame says anything about the pump
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(before, budget.Next(OnTime, hasBacklog: false));
        }

        Assert.Equal(before, budget.Next(Overrun, hasBacklog: false));
    }

    [Theory]
    [InlineData(0.0)] // The first pump has nothing to measure from
    [InlineData(-5.0)]
    [InlineData(250.001)] // A breakpoint, a long GC, a trip to the background
    [InlineData(10000.0)]
    public void AnImplausibleFrame_LeavesItUnchanged(
        double frameMs)
    {
        var budget = Grown(60, steps: 4);
        var before = budget.CurrentBudget;

        Assert.Equal(before, budget.Next(TimeSpan.FromMilliseconds(frameMs), hasBacklog: true));
    }

    [Fact]
    public void AFrameAtTheOutlierLimit_StillCountsAsAnOverrun()
    {
        var budget = Grown(60, steps: 4);
        var before = budget.CurrentBudget;

        var next = budget.Next(TimeSpan.FromMilliseconds(250), hasBacklog: true);

        Assert.Equal(before.TotalMilliseconds / 2, next.TotalMilliseconds, Tolerance);
    }

    [Theory]
    [InlineData(1.149, true)]
    [InlineData(1.151, false)]
    public void AFrameJustOverTheTarget_IsWithinTheTolerance(
        double frameInTargets,
        bool isHeadroom)
    {
        var budget = Grown(60, steps: 4);
        var before = budget.CurrentBudget;

        var next = budget.Next(TimeSpan.FromSeconds(frameInTargets / 60.0), hasBacklog: true);

        Assert.Equal(isHeadroom, next > before);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-60.0)]
    [InlineData(0.5)]
    [InlineData(1000.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnImplausibleRefreshRate_TargetsSixtyHertz(
        double refreshHz)
    {
        var budget = new AdaptiveContinuationBudget(refreshHz);

        Assert.Equal(1000.0 / 60.0, budget.TargetFrameTime.TotalMilliseconds, Tolerance);
    }

    [Fact]
    public void ANewRefreshRate_ReclampsTheCurrentBudget()
    {
        var budget = Grown(60, steps: 100);
        Assert.Equal(8.0, budget.CurrentBudget.TotalMilliseconds, Tolerance);

        budget.SetTargetFrameRate(240);

        Assert.Equal(1000.0 / 240.0, budget.TargetFrameTime.TotalMilliseconds, Tolerance);
        Assert.Equal(1000.0 / 240.0 / 2.0, budget.CurrentBudget.TotalMilliseconds, Tolerance);

        // A slower display raises the ceiling, without raising the budget until it's earned
        var before = budget.CurrentBudget;
        budget.SetTargetFrameRate(60);
        Assert.Equal(before, budget.CurrentBudget);
    }

    [Fact]
    public void UnderSteadyOverload_ItSawtoothsAboveTheFloor()
    {
        var budget = Grown(60, steps: 100);

        for (int i = 0; i < 50; i++)
        {
            var frame = (i % 4 == 3) ? Overrun : OnTime;
            var next = budget.Next(frame, hasBacklog: true);

            Assert.InRange(next.TotalMilliseconds, FloorMs, 8.0);
        }
    }

    [Fact]
    public void AReportedRateFasterThanTheRealOne_DoesNotPinItAtTheFloor()
    {
        var budget = new AdaptiveContinuationBudget(120);

        for (int i = 0; i < 100; i++)
        {
            budget.Next(TimeSpan.FromSeconds(1.0 / 60.0), hasBacklog: true);
        }

        Assert.True(budget.CurrentBudget.TotalMilliseconds > FloorMs);
    }

    [Fact]
    public void AReportedRateSlowerThanTheRealOne_LowersTheCeiling()
    {
        // The web reports nothing, which means 60 Hz, on a 144 Hz display
        var budget = new AdaptiveContinuationBudget(0);

        for (int i = 0; i < 100; i++)
        {
            budget.Next(TimeSpan.FromSeconds(1.0 / 144.0), hasBacklog: true);
        }

        Assert.InRange(budget.CurrentBudget.TotalMilliseconds, FloorMs, (1000.0 / 144.0 / 2.0) + Tolerance);
    }

    private static AdaptiveContinuationBudget Grown(
        double refreshHz,
        int steps,
        TimeSpan? frame = null)
    {
        var budget = new AdaptiveContinuationBudget(refreshHz);
        for (int i = 0; i < steps; i++)
        {
            budget.Next(frame ?? OnTime, hasBacklog: true);
        }

        return budget;
    }
}
