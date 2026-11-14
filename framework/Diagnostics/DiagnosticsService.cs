// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Graphics;

namespace DotGame.Framework.Diagnostics;

/// <summary>Game service for finding out how the game is running, such as its performance and its startup.</summary>
#pragma warning disable CA1001 // The game releases what it owns, as games don't dispose services
public sealed class DiagnosticsService
#pragma warning restore CA1001
{
    private readonly GraphicsService _graphics;
    private StatsOverlayRenderer? _overlay;
    private StatsOverlayMode _overlayMode;

    internal DiagnosticsService(
        GraphicsService graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        this._graphics = graphics;
    }

    /// <summary>
    /// Shows or hides the on-screen stats overlay (FPS, frame time, update/draw split between
    /// framework and app code), and selects its content and the level of measurement performed
    /// by the underlying collector. Defaults to <see cref="StatsOverlayMode.Off"/>; in shipped
    /// builds leave it off. <see cref="StatsOverlayMode.FpsOnly"/> measures only one timestamp per
    /// frame; <see cref="StatsOverlayMode.Detailed"/> activates the full bracket-based timing on
    /// every framework call site.
    /// </summary>
    /// <remarks>
    /// Familiar in spirit to Unreal's <c>stat unit</c>, Unity's Diagnostics panel, and Godot's
    /// monitor. Collection is fully gated by mode, so the steady-state cost when
    /// <see cref="StatsOverlayMode.Off"/> is a single predictable branch per hook (no Stopwatch
    /// reads, no allocations).
    /// </remarks>
    public StatsOverlayMode StatsOverlayMode
    {
        get => this._overlayMode;
        set
        {
            if (this._overlayMode == value)
            {
                return;
            }

            this._overlayMode = value;
            StatsCollector.SetMode(value);
        }
    }

    internal void DrawOverlay(
        RectangleInt safeBounds)
    {
        var mode = this._overlayMode;
        if (mode == StatsOverlayMode.Off)
        {
            return;
        }

        if (this._overlay is null)
        {
            this._overlay = new StatsOverlayRenderer(this._graphics);
        }

        this._overlay.Draw(StatsCollector.Snapshot, safeBounds, mode);
    }

    /// <summary>
    /// Marks a milestone of the game's startup, so startup can be measured the same way in every
    /// build, including a published one. It's traced with the time since the process started, and
    /// on the web it's also a performance mark, which the page's startup report collects. Only the
    /// first mark of each name counts, so a path that runs again can mark freely.
    /// </summary>
    /// <param name="name">The milestone, in kebab case, such as <c>"content-loaded"</c>.</param>
    /// <param name="detail">
    /// Optional context recorded with it, such as the scene shown or the number of assets.
    /// </param>
    public void MarkStartupMilestone(
        string name,
        string? detail = null)
    {
        Platform.StartupTimeline.Mark(name, detail);
    }

    // Releases the overlay's resources, as the game ends
    internal void Release()
    {
        this._overlay?.Dispose();
        this._overlay = null;
    }
}
