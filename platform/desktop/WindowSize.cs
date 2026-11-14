// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Globalization;

namespace DotGame.Framework.Desktop;

/// <summary>
/// How big the window opens when it's a window, rather than maximized or full screen. Players can
/// resize it, so a game should handle any size.
/// </summary>
public sealed class WindowSize
{
    private WindowSize(
        float aspectRatio,
        int maxScreenPercent,
        Vector2Int? maxSize)
    {
        this.AspectRatio = aspectRatio;
        this.MaxScreenPercent = maxScreenPercent;
        this.MaxSize = maxSize;
    }

    /// <summary>
    /// What a window opens at when the game doesn't say: 16:9, leaving some room around it. It's
    /// <c>WindowSize.Fit(16f / 9f, maxScreenPercent: 85)</c>.
    /// </summary>
    public static WindowSize Default { get; } = Fit(16f / 9f, maxScreenPercent: 85);

    /// <summary>
    /// The largest window of this shape that fits on the screen, within any limits given.
    /// </summary>
    /// <param name="aspectRatio">
    /// The shape of the window's content, as its width over its height, such as <c>16f / 9f</c> for
    /// landscape or <c>9f / 16f</c> for portrait. It must be positive and finite.
    /// </param>
    /// <param name="maxScreenPercent">
    /// The most of the screen's width and height the content may take, from 1 to 100. The screen
    /// here is its usable area, without the taskbar or dock, and the title bar always fits too.
    /// </param>
    /// <param name="maxSize">
    /// The largest content size, in device-independent pixels (96 per inch), so it's scaled for the
    /// display. Both sides must be positive. Nothing limits it if it's left out.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of its range.</exception>
    public static WindowSize Fit(
        float aspectRatio,
        int maxScreenPercent = 100,
        Vector2Int? maxSize = null)
    {
        if (!float.IsFinite(aspectRatio) || (aspectRatio <= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(aspectRatio), aspectRatio, "The aspect ratio must be positive and finite.");
        }

        if ((maxScreenPercent < 1) || (maxScreenPercent > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(maxScreenPercent), maxScreenPercent, "The percent of the screen must be from 1 to 100.");
        }

        if ((maxSize is { } size) && ((size.X <= 0) || (size.Y <= 0)))
        {
            throw new ArgumentOutOfRangeException(nameof(maxSize), size, "Both sides of the largest size must be positive.");
        }

        return new WindowSize(aspectRatio, maxScreenPercent, maxSize);
    }

    internal float AspectRatio { get; }

    internal int MaxScreenPercent { get; }

    internal Vector2Int? MaxSize { get; }

    /// <summary>Identifies these settings in the saved window state.</summary>
    internal string ToSettingsKey()
    {
        string maxSize = (this.MaxSize is { } size)
            ? string.Create(CultureInfo.InvariantCulture, $"{size.X}x{size.Y}")
            : "none";
        return string.Create(CultureInfo.InvariantCulture, $"fit:{this.AspectRatio}:{this.MaxScreenPercent}:{maxSize}");
    }
}
