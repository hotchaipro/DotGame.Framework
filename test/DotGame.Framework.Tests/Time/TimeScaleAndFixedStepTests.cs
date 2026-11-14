// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Time;
using Xunit;

namespace DotGame.Framework.Tests.Time;

public sealed class TimeScaleAndFixedStepTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(10);

    private static TimeService Started()
    {
        var time = new TimeService();
        time.Update(TimeSpan.Zero);
        return time;
    }

    private static int TakeSteps(
        TimeService time)
    {
        int steps = 0;
        while (time.TryTakeFixedStep())
        {
            steps++;
        }

        return steps;
    }

    [Fact]
    public void Scale_applies_to_game_time_and_not_to_unscaled_time()
    {
        var time = Started();
        time.Scale = 0.5f;

        time.Update(Frame);

        Assert.Equal(TimeSpan.FromMilliseconds(5), time.Delta);
        Assert.Equal(TimeSpan.FromMilliseconds(5), time.Total);
        Assert.Equal(Frame, time.UnscaledDelta);
    }

    [Fact]
    public void A_scale_of_zero_stops_delays_and_fixed_steps()
    {
        var time = Started();
        time.FixedStep = Frame;
        bool ran = false;
        time.Delay(Frame, () => ran = true);
        time.Scale = 0;

        time.Update(Frame * 2);

        Assert.False(ran);
        Assert.Equal(0, TakeSteps(time));

        time.Scale = 1;
        time.Update(Frame * 3);

        Assert.True(ran);
        Assert.Equal(1, TakeSteps(time));
    }

    [Fact]
    public void A_negative_scale_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Started().Scale = -1);
    }

    [Fact]
    public void Fixed_steps_follow_the_time_that_has_passed()
    {
        var time = Started();
        time.FixedStep = Frame;

        time.Update(TimeSpan.FromMilliseconds(5));
        Assert.Equal(0, TakeSteps(time));
        Assert.Equal(0.5f, time.FixedAlpha, 3);

        time.Update(TimeSpan.FromMilliseconds(30));
        Assert.Equal(3, TakeSteps(time));
        Assert.Equal(0f, time.FixedAlpha, 3);
    }

    [Fact]
    public void A_frame_runs_at_most_its_limit_and_drops_the_rest()
    {
        var time = Started();
        time.FixedStep = TimeSpan.FromMilliseconds(1);

        time.Update(TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeService.MaxFixedStepsPerFrame, TakeSteps(time));

        time.Update(TimeSpan.FromMilliseconds(101));
        Assert.Equal(1, TakeSteps(time));
    }

    [Fact]
    public void In_a_fixed_step_delta_is_the_step_and_afterwards_the_frame_again()
    {
        var time = Started();
        time.FixedStep = TimeSpan.FromMilliseconds(4);

        time.Update(Frame);

        Assert.True(time.TryTakeFixedStep());
        Assert.Equal(TimeSpan.FromMilliseconds(4), time.Delta);
        Assert.Equal(0.004f, time.DeltaSeconds, 4);
        Assert.Equal(Frame, time.UnscaledDelta);
        Assert.Equal(Frame, time.Total);

        Assert.True(time.TryTakeFixedStep());
        Assert.False(time.TryTakeFixedStep());
        Assert.Equal(Frame, time.Delta);
        Assert.Equal(0.01f, time.DeltaSeconds, 4);
    }

    [Fact]
    public void Off_by_default_with_no_steps_and_no_alpha()
    {
        var time = Started();

        time.Update(Frame);

        Assert.Null(time.FixedStep);
        Assert.False(time.TryTakeFixedStep());
        Assert.Throws<InvalidOperationException>(() => time.FixedAlpha);
        Assert.Equal(Frame, time.Delta);
    }

    [Fact]
    public void Time_while_off_is_not_owed_once_on()
    {
        var time = Started();
        time.Update(Frame);

        time.FixedStep = Frame;

        Assert.Equal(0f, time.FixedAlpha);
        Assert.False(time.TryTakeFixedStep());
    }

    [Fact]
    public void Turning_off_inside_a_step_gives_the_frame_delta_back()
    {
        var time = Started();
        time.FixedStep = TimeSpan.FromMilliseconds(4);
        time.Update(Frame);
        Assert.True(time.TryTakeFixedStep());

        time.FixedStep = null;

        Assert.Equal(Frame, time.Delta);
        Assert.False(time.TryTakeFixedStep());
    }

    [Fact]
    public void Shortening_the_step_keeps_the_alpha_at_most_one()
    {
        var time = Started();
        time.FixedStep = Frame;
        time.Update(TimeSpan.FromMilliseconds(9));

        time.FixedStep = TimeSpan.FromMilliseconds(1);

        Assert.Equal(1f, time.FixedAlpha);
    }

    [Fact]
    public void Resetting_forgets_the_unscaled_time()
    {
        var time = Started();
        time.Update(Frame);

        time.Reset();

        Assert.Equal(TimeSpan.Zero, time.UnscaledDelta);
        Assert.Equal(TimeSpan.Zero, time.UnscaledTotal);
    }

    [Fact]
    public void The_step_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Started().FixedStep = TimeSpan.Zero);
    }
}
