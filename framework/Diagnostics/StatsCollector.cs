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

    // Per-frame GPU work counters, reported by the platform sprite batches
    // when they issue draws.
    private static long s_drawCalls;
    private static long s_sprites;

    // Rolling buffers for smoothing.
    private static readonly long[] s_frameSamples = new long[SampleWindow];
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
    internal static void ExcludeOverlayDraw(int sprites)
    {
        if (s_mode != StatsOverlayMode.Detailed)
        {
            return;
        }

        s_drawCalls--;
        s_sprites -= sprites;
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

    private static void FinalizeFrame()
    {
        // Slide the ring window: subtract the slot we're about to
        // overwrite, then add the new sample. Keeps each per-series
        // sum current in O(1) regardless of window size.
        var i = s_sampleIndex;

        s_frameSum += s_frameTicks - s_frameSamples[i];
        s_frameSamples[i] = s_frameTicks;

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
        // NOTE: The mean over the window hides a hitch: 100 ms moves it by under 2 ms
        long worstTicks = 0;
        for (int sample = 0; sample < s_sampleCount; sample++)
        {
            worstTicks = Math.Max(worstTicks, s_frameSamples[sample]);
        }

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
            (float)spritesAvg);

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
