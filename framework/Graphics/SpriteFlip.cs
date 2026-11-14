// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Graphics;

/// <summary>
/// Mirrors a sprite's image inside the rectangle it's drawn in, without moving the rectangle: the
/// sprite flips in place whatever its origin, rotation and scale.
/// </summary>
/// <remarks>
/// A negative scale mirrors a sprite about its origin instead, so it moves unless the origin is at
/// its center.
/// </remarks>
[Flags]
public enum SpriteFlip
{
    /// <summary>The image as it is.</summary>
    None = 0,

    /// <summary>Left and right swapped.</summary>
    Horizontal = 1,

    /// <summary>Top and bottom swapped.</summary>
    Vertical = 2,

    /// <summary>Both: the image turned half a turn.</summary>
    Both = Horizontal | Vertical,
}