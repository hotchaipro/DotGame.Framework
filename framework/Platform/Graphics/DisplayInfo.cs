// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Platform.Graphics;

/// <summary>
/// The display a game draws to, as a platform host reports it to
/// <see cref="GameHost.SetDisplay"/>: all of it, every time any of it changes.
/// </summary>
/// <param name="ClientBounds">The window's client area, in pixels.</param>
/// <param name="SafeAreaBounds">The part of the client area clear of system bars, cutouts, rounded
/// corners and the soft keyboard, in pixels.</param>
/// <param name="Dpi">The display's physical density, in dots per inch.</param>
/// <param name="RefreshRateHz">The display's refresh rate, or 0 if the platform can't tell (the
/// continuation budget then assumes 60).</param>
internal readonly record struct DisplayInfo(
    RectangleInt ClientBounds,
    RectangleInt SafeAreaBounds,
    float Dpi,
    double RefreshRateHz);
