// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Diagnostics;

/// <summary>
/// Selects the on-screen stats overlay's content and the level of
/// measurement performed by the underlying collector.
/// </summary>
/// <remarks>
/// Higher modes carry more measurement overhead. Choose the lowest
/// mode that gives you the information you need; in shipped builds
/// the overlay is typically off entirely.
/// </remarks>
public enum StatsOverlayMode
{
    /// <summary>
    /// Overlay hidden; collector is fully suspended. Every collection
    /// hook is a single predictable branch (no Stopwatch reads, no
    /// allocations). This is the default.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Single-line on-screen overlay showing only frames per second.
    /// The collector takes exactly one
    /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reading
    /// per frame; update/draw split and framework/app breakdown are
    /// not measured.
    /// </summary>
    FpsOnly = 1,

    /// <summary>
    /// Four-line on-screen overlay showing FPS, update time
    /// (framework/app), draw time (framework/app), and GPU draw calls
    /// and sprites per frame. Carries the full bracket-based measurement
    /// cost on every framework call site.
    /// </summary>
    Detailed = 2,
}
