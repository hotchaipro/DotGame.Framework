// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Diagnostics;

/// <summary>
/// Immutable, allocation-free snapshot of the most recent per-frame
/// performance counters published by <see cref="DiagnosticsService"/>.
/// </summary>
/// <remarks>
/// Values are smoothed over a short rolling window. A snapshot is
/// produced at the end of every frame; consumers can copy it for HUDs,
/// telemetry, or custom overlays without taking a dependency on the
/// service instance.
/// </remarks>
internal readonly struct StatsSnapshot
{
    public StatsSnapshot(
        float fps,
        float frameMs,
        float worstFrameMs,
        float updateMs,
        float updateFrameworkMs,
        float updateAppMs,
        float drawMs,
        float drawFrameworkMs,
        float drawAppMs,
        float drawCalls,
        float sprites)
    {
        this.Fps = fps;
        this.FrameMs = frameMs;
        this.WorstFrameMs = worstFrameMs;
        this.UpdateMs = updateMs;
        this.UpdateFrameworkMs = updateFrameworkMs;
        this.UpdateAppMs = updateAppMs;
        this.DrawMs = drawMs;
        this.DrawFrameworkMs = drawFrameworkMs;
        this.DrawAppMs = drawAppMs;
        this.DrawCalls = drawCalls;
        this.Sprites = sprites;
    }

    /// <summary>Smoothed frames per second.</summary>
    public float Fps { get; }

    /// <summary>Smoothed wall-clock milliseconds per frame.</summary>
    public float FrameMs { get; }

    /// <summary>The slowest frame in the window, in milliseconds.</summary>
    public float WorstFrameMs { get; }

    /// <summary>Smoothed milliseconds spent in the update phase.</summary>
    public float UpdateMs { get; }

    /// <summary>Portion of <see cref="UpdateMs"/> spent in framework code.</summary>
    public float UpdateFrameworkMs { get; }

    /// <summary>Portion of <see cref="UpdateMs"/> spent in app code.</summary>
    public float UpdateAppMs { get; }

    /// <summary>Smoothed milliseconds spent in the draw phase.</summary>
    public float DrawMs { get; }

    /// <summary>Portion of <see cref="DrawMs"/> spent in framework code.</summary>
    public float DrawFrameworkMs { get; }

    /// <summary>Portion of <see cref="DrawMs"/> spent in app code.</summary>
    public float DrawAppMs { get; }

    /// <summary>Smoothed GPU draw calls per frame, after batching.</summary>
    public float DrawCalls { get; }

    /// <summary>Smoothed sprites drawn per frame.</summary>
    public float Sprites { get; }
}
