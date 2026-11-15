// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DotGame.Framework.Diagnostics;

/// <summary>
/// Internal collector that captures per-frame timing for the update and
/// draw phases, splitting each phase into framework vs app time, and
/// counts the GPU draw calls and sprites each frame submits.
/// </summary>
/// <remarks>
/// Single-threaded: assumes all hooks are invoked on the game thread,
/// matching the existing framework execution model. The hooks are kept
/// tiny and aggressively inlined; their cost is dominated by a single
/// <see cref="Stopwatch.GetTimestamp"/> call, which is negligible next
/// to the framework work they bracket.
/// </remarks>
internal static class StatsCollector
{
    private const int PhaseNone = 0;
    private const int PhaseUpdate = 1;
    private const int PhaseDraw = 2;

    private const int SampleWindow = 60;

    private static readonly double s_tickToMs = 1000.0 / Stopwatch.Frequency;

    // Phase / framework-depth tracking.
    private static int s_phase;
    private static int s_frameworkDepth;

    /// <summary>
    /// When &gt; 0, <see cref="EnterFramework"/> and
    /// <see cref="ExitFramework"/> are no-ops. Used by the overlay
    /// renderer so its own framework calls don't pollute the counters
    /// it is about to display.
    /// </summary>
    private static int s_suspendDepth;

    // Per-phase timestamps (raw Stopwatch ticks).
    private static long s_phaseStartTicks;
    private static long s_frameworkStartTicks;

    // Frame boundary timestamp (anchored on BeginUpdate so the
    // measured frame includes update, draw, and the unattributed gap
    // between frames -- and is unaffected by suppressed-draw frames).
    private static long s_lastFrameStartTicks;

    // Per-frame accumulators (Stopwatch ticks).
    private static long s_updateTicks;
    private static long s_updateFrameworkTicks;
    private static long s_drawTicks;
    private static long s_drawFrameworkTicks;
    private static long s_frameTicks;

    // The time the overlay spent drawing itself this frame, left out of the dropped-frame check
    private static long s_overlayTicks;

    // Per-frame GPU work counters, reported by the platform sprite batches
    // when they issue draws.
    private static long s_drawCalls;
    private static long s_sprites;

    // Rolling buffers for smoothing.
    private static readonly long[] s_frameSamples = new long[SampleWindow];
    private static readonly long[] s_overlaySamples = new long[SampleWindow];
    private static readonly long[] s_updateSamples = new long[SampleWindow];
    private static readonly long[] s_updateFrameworkSamples = new long[SampleWindow];
    private static readonly long[] s_drawSamples = new long[SampleWindow];
    private static readonly long[] s_drawFrameworkSamples = new long[SampleWindow];
    private static readonly long[] s_drawCallSamples = new long[SampleWindow];
    private static readonly long[] s_spriteSamples = new long[SampleWindow];
    private static int s_sampleIndex;
    private static int s_sampleCount;

    // Running sums over the active sample window. Maintained
    // incrementally so FinalizeFrame stays O(1) instead of O(window).
    private static long s_frameSum;
    private static long s_updateSum;
    private static long s_updateFrameworkSum;
    private static long s_drawSum;
    private static long s_drawFrameworkSum;
    private static long s_drawCallSum;
    private static long s_spriteSum;

    /// <summary>
    /// Latest published snapshot. Written atomically (struct copy) at
    /// the end of every frame in <see cref="FinalizeFrame"/>.
    /// </summary>
    internal static StatsSnapshot Snapshot;

    // Master gate. Selects what work the hooks do:
    //   Off      -- every hook is a single predictable-branch no-op.
    //   FpsOnly  -- only BeginUpdate does work (one Stopwatch read /
    //               frame); FPS is rendered as a 1-line on-screen
    //               overlay.
    //   Detailed -- full per-phase + framework/app bracket timing.
    // Toggled when the overlay mode changes.
    private static StatsOverlayMode s_mode;

    // A frame that takes this much of a refresh interval longer than its target missed it
    private const double DroppedFrameExcess = 0.5;

    // The frame rate the game prefers, or 0 for the display's own
    private static int s_preferredFrameRate;

    // Plausible reported refresh rates, in hertz; anything else is estimated
    private const double MinRefreshHz = 20.0;
    private const double MaxRefreshHz = 500.0;

    // The percentile of the window's frames that estimates the display's interval
    private const int EstimatePercentile = 10;

    // The display's refresh rate as the platform reports it, or 0 when it can't tell
    private static double s_reportedRefreshHz;

    // The window's frames, sorted to estimate the display's interval
    private static readonly long[] s_sortedFrameSamples = new long[SampleWindow];

    // How often the display's interval is estimated from the frame timestamps, in frames, and the
    // intervals between timestamps that can be refreshes: from 2 ms (500 Hz) to 100 ms
    private const int EstimateEvery = 32;
    private static readonly long MinFrameIntervalTicks = TimeSpan.FromMilliseconds(2).Ticks;
    private static readonly long MaxFrameIntervalTicks = TimeSpan.FromMilliseconds(100).Ticks;

    // The frame source's last timestamp, the intervals between timestamps (in TimeSpan ticks),
    // and the estimate of the display's interval from them: the shortest since the display
    // changed, or 0 before the first
    private static long s_lastFrameTimestampTicks;
    private static readonly long[] s_timestampIntervals = new long[SampleWindow];
    private static readonly long[] s_sortedIntervals = new long[SampleWindow];
    private static int s_intervalCount;
    private static int s_nextInterval;
    private static int s_untilEstimate = EstimateEvery;
    private static long s_estimatedIntervalTicks;

    /// <summary>
    /// Sets the active collection mode. Mode transitions clear all
    /// transient state so the next reading starts from a clean
    /// baseline rather than carrying stale samples.
    /// </summary>
    internal static void SetMode(StatsOverlayMode mode)
    {
        if (s_mode == mode)
        {
            return;
        }

        s_mode = mode;

        // Reset on every transition so the next reading is clean.
        s_phase = PhaseNone;
        s_frameworkDepth = 0;
        s_suspendDepth = 0;
        s_phaseStartTicks = 0;
        s_frameworkStartTicks = 0;
        s_lastFrameStartTicks = 0;
        s_updateTicks = 0;
        s_updateFrameworkTicks = 0;
        s_drawTicks = 0;
        s_drawFrameworkTicks = 0;
        s_frameTicks = 0;
        s_drawCalls = 0;
        s_sprites = 0;
        Array.Clear(s_frameSamples);
        Array.Clear(s_overlaySamples);
        s_overlayTicks = 0;
        Array.Clear(s_updateSamples);
        Array.Clear(s_updateFrameworkSamples);
        Array.Clear(s_drawSamples);
        Array.Clear(s_drawFrameworkSamples);
        Array.Clear(s_drawCallSamples);
        Array.Clear(s_spriteSamples);
        s_sampleIndex = 0;
        s_sampleCount = 0;
        s_frameSum = 0;
        s_updateSum = 0;
        s_updateFrameworkSum = 0;
        s_drawSum = 0;
        s_drawFrameworkSum = 0;
        s_drawCallSum = 0;
        s_spriteSum = 0;
        Snapshot = default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginUpdate()
    {
        var mode = s_mode;
        if (mode == StatsOverlayMode.Off)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();

        // Frame total = wall-clock between successive BeginUpdate
        // calls. Publishing here (rather than at EndDraw) keeps the
        // counter consistent on frames where Draw is suppressed and
        // includes the full inter-frame gap (vsync wait, host loop,
        // GC, interop return) in FrameMs.
        if (s_lastFrameStartTicks != 0)
        {
            s_frameTicks = now - s_lastFrameStartTicks;
            FinalizeFrame();
        }
        s_lastFrameStartTicks = now;

        if (mode == StatsOverlayMode.Detailed)
        {
            s_phase = PhaseUpdate;
            s_frameworkDepth = 0;
            s_phaseStartTicks = now;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndUpdate()
    {
        if (s_mode != StatsOverlayMode.Detailed)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        s_updateTicks = now - s_phaseStartTicks;
        s_phase = PhaseNone;
        s_frameworkDepth = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginDraw()
    {
        if (s_mode != StatsOverlayMode.Detailed)
        {
            return;
        }

        s_phase = PhaseDraw;
        s_frameworkDepth = 0;
        s_phaseStartTicks = Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndDraw()
    {
        if (s_mode != StatsOverlayMode.Detailed)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        s_drawTicks = now - s_phaseStartTicks;
        s_phase = PhaseNone;
        s_frameworkDepth = 0;
    }

    /// <summary>
    /// Marks entry into framework code.
    /// a depth counter; only the outermost pair contributes time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnterFramework()
    {
        if (s_mode != StatsOverlayMode.Detailed || s_suspendDepth != 0)
        {
            return;
        }

        if (s_frameworkDepth++ == 0)
        {
            s_frameworkStartTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Marks exit from framework code. The outermost exit accumulates
    /// elapsed framework time into the current phase's bucket.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExitFramework()
    {
        if (s_mode != StatsOverlayMode.Detailed || s_suspendDepth != 0)
        {
            return;
        }

        if (--s_frameworkDepth == 0)
        {
            var elapsed = Stopwatch.GetTimestamp() - s_frameworkStartTicks;
            if (s_phase == PhaseUpdate)
            {
                s_updateFrameworkTicks += elapsed;
            }
            else if (s_phase == PhaseDraw)
            {
                s_drawFrameworkTicks += elapsed;
            }
        }
    }

    /// <summary>
    /// Counts one GPU draw call and the sprites it draws. Called by the
    /// platform sprite batches where they issue draws, so the count is what
    /// the GPU actually receives after batching.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountDraw(int sprites)
    {
        if (s_mode != StatsOverlayMode.Detailed)
        {
            return;
        }

        s_drawCalls++;
        s_sprites += sprites;
    }

    /// <summary>
    /// Counts several GPU draw calls at once, for a platform that issues
    /// them outside managed code (the web's command buffer).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountDraws(int drawCalls, int sprites)
    {
        if (s_mode != StatsOverlayMode.Detailed)
        {
            return;
        }

        s_drawCalls += drawCalls;
        s_sprites += sprites;
    }

    /// <summary>
    /// Removes the overlay's own draw from the current frame's counts. The
    /// overlay draws one batch, one sprite per font pixel, which would
    /// otherwise dominate the sprite count it displays.
    /// </summary>
    internal static void ExcludeOverlayDraw(int drawCalls, int sprites)
    {
        if (s_mode != StatsOverlayMode.Detailed)
        {
            return;
        }

        s_drawCalls -= drawCalls;
        s_sprites -= sprites;
    }

    /// <summary>
    /// Takes the time the overlay spent drawing itself out of the frame's dropped-frame check, so
    /// turning it on doesn't count its own cost as frames dropped. The frame's length is left as
    /// it is: a frame waits for the display, and can't take less than its refresh.
    /// </summary>
    internal static void ExcludeOverlayTime(long ticks)
    {
        if (s_mode == StatsOverlayMode.Off)
        {
            return;
        }

        s_overlayTicks += ticks;
    }

    /// <summary>
    /// Suspends framework-time collection. Always pair with
    /// <see cref="Resume"/>. Used by the stats overlay renderer so its
    /// own SpriteBatch calls don't appear in the counters it displays.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Suspend()
    {
        s_suspendDepth++;
    }

    /// <summary>Resumes framework-time collection paused by <see cref="Suspend"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Resume()
    {
        s_suspendDepth--;
    }

    /// <summary>
    /// Tells the overlay the display's refresh rate, as the platform reports it, or 0 if it can't
    /// tell. Called whenever the platform reports its display.
    /// </summary>
    internal static void SetDisplayRefreshRate(double hz)
    {
        // NOTE: Platforms report the rate whenever they report the display, so the estimate only
        // starts over for a real change
        if (hz == s_reportedRefreshHz)
        {
            return;
        }

        s_reportedRefreshHz = hz;
        ResetDisplayEstimate();
    }

    /// <summary>Starts the estimate of the display's interval over, as for a new display.</summary>
    internal static void ResetDisplayEstimate()
    {
        s_estimatedIntervalTicks = 0;
        s_intervalCount = 0;
        s_nextInterval = 0;
        s_untilEstimate = EstimateEvery;
        s_lastFrameTimestampTicks = 0;
    }

    /// <summary>
    /// Records a frame's timestamp from the platform's frame source, to estimate the display's
    /// refresh interval where the platform can't report its rate, as on the web. Called every
    /// frame, the overlay on or off, so a game that has slowed is still judged by the display.
    /// </summary>
    /// <remarks>
    /// A frame source's timestamps follow the display's refreshes (the browser's animation frames
    /// are given the time their refresh began), so the interval between two is a whole number of
    /// refresh intervals, however long the game took. A low percentile of the intervals, every so
    /// often, is the refresh interval if a tenth of the frames were on time, and the shortest
    /// since the display changed is kept, as a game that slowed can't take less. Where the rate is
    /// reported, it does nothing.
    /// </remarks>
    internal static void RecordFrameTimestamp(TimeSpan timestamp)
    {
        if ((s_reportedRefreshHz >= MinRefreshHz) && (s_reportedRefreshHz <= MaxRefreshHz))
        {
            return;
        }

        long ticks = timestamp.Ticks;
        long interval = ticks - s_lastFrameTimestampTicks;
        bool isFirst = s_lastFrameTimestampTicks == 0;
        s_lastFrameTimestampTicks = ticks;

        // NOTE: The first frame has no interval, and one past 100 ms is a pause, not a refresh
        if (isFirst || (interval < MinFrameIntervalTicks) || (interval > MaxFrameIntervalTicks))
        {
            return;
        }

        s_timestampIntervals[s_nextInterval] = interval;
        s_nextInterval = (s_nextInterval + 1) % SampleWindow;
        if (s_intervalCount < SampleWindow)
        {
            s_intervalCount++;
        }

        if (--s_untilEstimate > 0)
        {
            return;
        }

        s_untilEstimate = EstimateEvery;
        Array.Copy(s_timestampIntervals, s_sortedIntervals, s_intervalCount);
        Array.Sort(s_sortedIntervals, 0, s_intervalCount);
        long estimate = s_sortedIntervals[s_intervalCount * EstimatePercentile / 100];
        if ((s_estimatedIntervalTicks == 0) || (estimate < s_estimatedIntervalTicks))
        {
            s_estimatedIntervalTicks = estimate;
        }
    }

    /// <summary>
    /// Tells the overlay the frame rate the game prefers (<see cref="Game.PreferredFrameRate"/>),
    /// or null for the display's own: below the display's, it's the rate the game is judged by.
    /// </summary>
    internal static void SetPreferredFrameRate(int? frameRate)
    {
        s_preferredFrameRate = frameRate ?? 0;
    }

    // The display's refresh interval: as reported, when the rate is plausible, or else estimated
    // from the frame source's timestamps (RecordFrameTimestamp), or until there's an estimate,
    // from a low percentile of the frames in the window, what the device delivers when a frame
    // isn't held up
    private static double DisplayIntervalMs()
    {
        if ((s_reportedRefreshHz >= MinRefreshHz) && (s_reportedRefreshHz <= MaxRefreshHz))
        {
            return 1000.0 / s_reportedRefreshHz;
        }

        if (s_estimatedIntervalTicks > 0)
        {
            return TimeSpan.FromTicks(s_estimatedIntervalTicks).TotalMilliseconds;
        }

        Array.Copy(s_frameSamples, s_sortedFrameSamples, s_sampleCount);
        Array.Sort(s_sortedFrameSamples, 0, s_sampleCount);
        double ms = s_sortedFrameSamples[s_sampleCount * EstimatePercentile / 100] * s_tickToMs;
        return (ms > 0.0) ? ms : 1000.0 / 60.0;
    }

    private static void FinalizeFrame()
    {
        // Slide the ring window: subtract the slot we're about to
        // overwrite, then add the new sample. Keeps each per-series
        // sum current in O(1) regardless of window size.
        var i = s_sampleIndex;

        s_frameSum += s_frameTicks - s_frameSamples[i];
        s_frameSamples[i] = s_frameTicks;
        s_overlaySamples[i] = s_overlayTicks;
        s_overlayTicks = 0;

        s_updateSum += s_updateTicks - s_updateSamples[i];
        s_updateSamples[i] = s_updateTicks;

        s_updateFrameworkSum += s_updateFrameworkTicks - s_updateFrameworkSamples[i];
        s_updateFrameworkSamples[i] = s_updateFrameworkTicks;

        s_drawSum += s_drawTicks - s_drawSamples[i];
        s_drawSamples[i] = s_drawTicks;

        s_drawFrameworkSum += s_drawFrameworkTicks - s_drawFrameworkSamples[i];
        s_drawFrameworkSamples[i] = s_drawFrameworkTicks;

        s_drawCallSum += s_drawCalls - s_drawCallSamples[i];
        s_drawCallSamples[i] = s_drawCalls;

        s_spriteSum += s_sprites - s_spriteSamples[i];
        s_spriteSamples[i] = s_sprites;

        s_sampleIndex = (i + 1) % SampleWindow;
        if (s_sampleCount < SampleWindow)
        {
            s_sampleCount++;
        }

        var scale = s_tickToMs / s_sampleCount;
        var frameAvg = s_frameSum * scale;
        var updateAvg = s_updateSum * scale;
        var updateFwAvg = s_updateFrameworkSum * scale;
        var drawAvg = s_drawSum * scale;
        var drawFwAvg = s_drawFrameworkSum * scale;

        var updateAppAvg = Math.Max(0.0, updateAvg - updateFwAvg);
        var drawAppAvg = Math.Max(0.0, drawAvg - drawFwAvg);
        var fps = frameAvg > 0.0 ? 1000.0 / frameAvg : 0.0;

        // The game's target: the display's rate, or the game's preferred rate when it's lower
        double displayMs = DisplayIntervalMs();
        double targetMs = displayMs;
        if ((s_preferredFrameRate > 0) && ((1000.0 / s_preferredFrameRate) > displayMs))
        {
            targetMs = 1000.0 / s_preferredFrameRate;
        }

        // NOTE: The mean over the window hides a hitch (100 ms moves it by under 2 ms), so the
        // frames that missed the target are counted. A frame shows for a whole number of
        // refreshes, so a target that doesn't divide into the display's rate (50 on 60 Hz) has
        // frames of one and two refreshes: a frame misses it by taking half a refresh more than
        // the refreshes the target spans.
        double refreshes = Math.Ceiling((targetMs / displayMs) - 0.01);
        long droppedTicks = (long)((refreshes + DroppedFrameExcess) * displayMs / s_tickToMs);
        long worstTicks = 0;
        int dropped = 0;
        for (int sample = 0; sample < s_sampleCount; sample++)
        {
            long ticks = s_frameSamples[sample];
            worstTicks = Math.Max(worstTicks, ticks);
            if ((ticks - s_overlaySamples[sample]) > droppedTicks)
            {
                dropped++;
            }
        }

        var cpuPercent = (updateAvg + drawAvg) * 100.0 / targetMs;

        var drawCallsAvg = Math.Max(0.0, (double)s_drawCallSum / s_sampleCount);
        var spritesAvg = Math.Max(0.0, (double)s_spriteSum / s_sampleCount);

        // Atomic publish (struct copy).
        Snapshot = new StatsSnapshot(
            (float)fps,
            (float)frameAvg,
            (float)(worstTicks * s_tickToMs),
            (float)updateAvg,
            (float)updateFwAvg,
            (float)updateAppAvg,
            (float)drawAvg,
            (float)drawFwAvg,
            (float)drawAppAvg,
            (float)drawCallsAvg,
            (float)spritesAvg,
            (float)(1000.0 / displayMs),
            (float)(1000.0 / targetMs),
            dropped,
            (float)cpuPercent);

        // Reset per-frame accumulators.
        s_updateTicks = 0;
        s_updateFrameworkTicks = 0;
        s_drawTicks = 0;
        s_drawFrameworkTicks = 0;
        s_frameTicks = 0;
        s_drawCalls = 0;
        s_sprites = 0;
    }
}
