// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Threading;

/// <summary>
/// Adaptively tunes the per-frame time budget used by
/// <see cref="GameSynchronizationContext.ExecutePendingContinuations()"/>
/// so that as many queued continuations as possible run each frame without
/// pushing the frame time past the display's refresh interval.
/// </summary>
/// <remarks>
/// <para>
/// The render loop is vsync-paced, so every frame has a fixed wall-clock
/// deadline (the display refresh interval). Draining the whole continuation
/// queue in a single frame can hold an engine-restricted resource (most
/// notably the GPU driver lock during cold-start shader compile/link) long
/// enough to overrun that deadline and drop a frame. A fixed budget avoids
/// the worst case but either wastes headroom on light frames or overruns on
/// heavy ones. This controller instead searches for the largest budget the
/// current workload sustains.
/// </para>
/// <para>
/// It uses an Additive-Increase / Multiplicative-Decrease (AIMD) feedback
/// loop, the same congestion-control shape TCP uses: when the previous frame
/// left headroom under the target it nudges the budget up by a small fixed
/// step; when the previous frame overran the target (beyond a small
/// tolerance) it cuts the budget in half. The budget is always clamped to
/// <c>[MinBudget, MaxBudget]</c>, where <c>MaxBudget</c> is a fraction of the
/// target frame interval so update, draw, and present still fit. Because the
/// pump always runs at least one continuation regardless of budget, forward
/// progress is guaranteed even at the floor.
/// </para>
/// <para>
/// The reported refresh rate is only a starting point, because it's often wrong: a
/// ProMotion iPhone reports 120 Hz while apps run at 60, Low Power Mode lowers the rate
/// without saying so, the web can't report one, and a game can be holding a steady 30 fps.
/// So the controller calibrates its target from a low percentile of the frame intervals it
/// measures, which is what the device delivers when a frame isn't held up.
/// </para>
/// <para>
/// Single-threaded: this controller is owned by
/// <see cref="GameSynchronizationContext"/> and driven once per frame from
/// the game thread by its steady-state continuation pump, so no locking is
/// performed.
/// </para>
/// </remarks>
internal sealed class AdaptiveContinuationBudget
{
    // Smallest budget the pump may be given. The pump still executes at least
    // one continuation per frame at this floor, so progress never stalls.
    private static readonly TimeSpan MinBudget = TimeSpan.FromMilliseconds(1.0);

    // Absolute ceiling on the budget regardless of how long the frame
    // interval is, so a very low refresh rate can't hand the pump an
    // unreasonably large slice.
    private static readonly TimeSpan MaxBudgetCap = TimeSpan.FromMilliseconds(8.0);

    // Fraction of the target frame interval the pump may consume. The rest of
    // the frame is reserved for update, draw, and buffer present. The
    // effective max budget is min(MaxBudgetCap, target * MaxBudgetFraction).
    private const double MaxBudgetFraction = 0.5;

    // Additive-increase step applied when the previous frame had headroom.
    private static readonly TimeSpan AdditiveStep = TimeSpan.FromMilliseconds(0.5);

    // Multiplicative-decrease factor applied when the previous frame overran.
    private const double BackoffFactor = 0.5;

    // How far past the target a frame may run before it counts as "dropped".
    // Absorbs vsync jitter and measurement noise so the controller doesn't
    // oscillate on frames that are only marginally over.
    private const double OverrunTolerance = 0.15;

    // Frame deltas above this are treated as outliers (first frame after a
    // reset/resume, a GC pause, a debugger break, backgrounding) and neither
    // grow nor shrink the budget, so a one-off stall can't collapse it.
    private static readonly TimeSpan OutlierFrameTime = TimeSpan.FromMilliseconds(250.0);

    // Fallback target when the display refresh rate is unknown (60 FPS).
    private static readonly TimeSpan DefaultTargetFrameTime = TimeSpan.FromSeconds(1.0 / 60.0);

    // Plausible bounds for a reported display refresh rate, in hertz. Values
    // outside this range are rejected in favor of the 60 FPS fallback.
    private const double MinRefreshHz = 1.0;
    private const double MaxRefreshHz = 1000.0;

    // Frame intervals kept for calibration, how many are needed before they're trusted over the
    // reported rate, and how often the target is recalculated from them
    private const int CalibrationWindow = 64;
    private const int CalibrationInterval = 32;

    // The share of intervals that may be shorter than the calibrated target, which ignores the
    // odd short interval after a late frame
    private const int CalibrationPercentile = 10;

    private readonly long[] _intervalTicks = new long[CalibrationWindow];
    private readonly long[] _sortedTicks = new long[CalibrationWindow];
    private int _intervalCount;
    private int _nextInterval;
    private int _untilCalibration = CalibrationInterval;

    private TimeSpan _targetFrameTime;
    private TimeSpan _maxBudget;
    private TimeSpan _currentBudget;

    /// <summary>
    /// Creates a controller targeting the supplied display refresh rate.
    /// </summary>
    /// <param name="displayRefreshHz">
    /// The display refresh rate in hertz, or a non-positive / implausible
    /// value to target 60 FPS until a real rate is supplied via
    /// <see cref="SetTargetFrameRate(double)"/>.
    /// </param>
    public AdaptiveContinuationBudget(
        double displayRefreshHz = 0.0)
    {
        this._currentBudget = MinBudget;
        this.SetTargetFrameRate(displayRefreshHz);
    }

    /// <summary>
    /// The budget most recently produced by <see cref="Next(TimeSpan, bool)"/>.
    /// </summary>
    public TimeSpan CurrentBudget => this._currentBudget;

    /// <summary>
    /// The target frame interval the controller is tuning against.
    /// </summary>
    public TimeSpan TargetFrameTime => this._targetFrameTime;

    /// <summary>
    /// Starts again from a display refresh rate, when the surface or display mode changes:
    /// sets the target frame interval, recomputes the maximum budget (a fraction of the
    /// interval) and re-clamps the current budget. The same rate again changes nothing.
    /// </summary>
    /// <param name="displayRefreshHz">
    /// The display refresh rate in hertz. Non-positive or implausible values
    /// fall back to 60 FPS.
    /// </param>
    public void SetTargetFrameRate(
        double displayRefreshHz)
    {
        var reported =
            (displayRefreshHz >= MinRefreshHz) && (displayRefreshHz <= MaxRefreshHz)
                ? TimeSpan.FromSeconds(1.0 / displayRefreshHz)
                : DefaultTargetFrameTime;

        // NOTE: Platforms report the rate whenever they report the display, so calibration
        // only starts over for a real change
        if (reported == this._reportedFrameTime)
        {
            return;
        }

        this._reportedFrameTime = reported;
        this._intervalCount = 0;
        this._nextInterval = 0;
        this._untilCalibration = CalibrationInterval;

        this.SetTarget(reported);
    }

    private TimeSpan _reportedFrameTime;

    private void SetTarget(
        TimeSpan targetFrameTime)
    {
        this._targetFrameTime = targetFrameTime;

        double maxBudgetMs = Math.Min(
            MaxBudgetCap.TotalMilliseconds,
            this._targetFrameTime.TotalMilliseconds * MaxBudgetFraction);
        maxBudgetMs = Math.Max(MinBudget.TotalMilliseconds, maxBudgetMs);
        this._maxBudget = TimeSpan.FromMilliseconds(maxBudgetMs);

        // Keep the current budget inside the (possibly changed) range.
        if (this._currentBudget > this._maxBudget)
        {
            this._currentBudget = this._maxBudget;
        }
        else if (this._currentBudget < MinBudget)
        {
            this._currentBudget = MinBudget;
        }
    }

    /// <summary>
    /// Advances the controller using the previous frame's duration and returns
    /// the budget to use for the upcoming continuation pump. Called once per
    /// frame by <see cref="GameSynchronizationContext.ExecutePendingContinuations()"/>.
    /// </summary>
    /// <param name="lastFrameTime">
    /// The wall-clock duration since the previous pump (self-measured by the
    /// pump). Non-positive or outlier values leave the budget unchanged.
    /// </param>
    /// <param name="hasBacklog">
    /// Whether continuations are currently queued. When <see langword="false"/>
    /// the budget is held steady rather than drifting toward the ceiling during
    /// idle periods.
    /// </param>
    /// <returns>The budget to hand the next pump.</returns>
    public TimeSpan Next(
        TimeSpan lastFrameTime,
        bool hasBacklog)
    {
        this.Calibrate(lastFrameTime);

        // Nothing queued: the budget is irrelevant this frame. Hold steady so
        // an idle stretch doesn't ramp the budget to the ceiling and overrun
        // on the first frame of the next burst.
        if (!hasBacklog)
        {
            return this._currentBudget;
        }

        // Ignore implausible deltas (first frame after reset/resume, GC pause,
        // debugger break) so a single outlier neither collapses nor inflates
        // the budget.
        if ((lastFrameTime <= TimeSpan.Zero) || (lastFrameTime > OutlierFrameTime))
        {
            return this._currentBudget;
        }

        double overrunThresholdMs =
            this._targetFrameTime.TotalMilliseconds * (1.0 + OverrunTolerance);

        if (lastFrameTime.TotalMilliseconds > overrunThresholdMs)
        {
            // Frame overran the target: back off multiplicatively.
            double nextMs = this._currentBudget.TotalMilliseconds * BackoffFactor;
            nextMs = Math.Max(MinBudget.TotalMilliseconds, nextMs);
            this._currentBudget = TimeSpan.FromMilliseconds(nextMs);
        }
        else
        {
            // Frame had headroom: increase additively toward the max.
            double nextMs = this._currentBudget.TotalMilliseconds + AdditiveStep.TotalMilliseconds;
            nextMs = Math.Min(this._maxBudget.TotalMilliseconds, nextMs);
            this._currentBudget = TimeSpan.FromMilliseconds(nextMs);
        }

        return this._currentBudget;
    }

    // Records a frame's interval, and every so often moves the target to what the device is
    // delivering. No allocation, and the sort only runs a couple of times a second.
    private void Calibrate(
        TimeSpan lastFrameTime)
    {
        if ((lastFrameTime <= TimeSpan.Zero) || (lastFrameTime > OutlierFrameTime))
        {
            return;
        }

        this._intervalTicks[this._nextInterval] = lastFrameTime.Ticks;
        this._nextInterval = (this._nextInterval + 1) % CalibrationWindow;
        if (this._intervalCount < CalibrationWindow)
        {
            this._intervalCount++;
        }

        if (--this._untilCalibration > 0)
        {
            return;
        }

        this._untilCalibration = CalibrationInterval;

        Array.Copy(this._intervalTicks, this._sortedTicks, this._intervalCount);
        Array.Sort(this._sortedTicks, 0, this._intervalCount);

        this.SetTarget(TimeSpan.FromTicks(
            this._sortedTicks[(this._intervalCount * CalibrationPercentile) / 100]));
    }
}
