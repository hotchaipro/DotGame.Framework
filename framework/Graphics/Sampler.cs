// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Graphics;

/// <summary>
/// How a sprite batch reads its textures: blending neighbouring pixels or taking the nearest (for
/// pixel art), and clamping at the edges or repeating (for tiled backgrounds).
/// </summary>
public enum Sampler
{
    /// <summary>Blends neighbouring pixels, and stops at the texture's edges.</summary>
    LinearClamp,

    /// <summary>Takes the nearest pixel, and stops at the texture's edges.</summary>
    PointClamp,

    /// <summary>Blends neighbouring pixels, and repeats past the texture's edges.</summary>
    LinearRepeat,

    /// <summary>Takes the nearest pixel, and repeats past the texture's edges.</summary>
    PointRepeat,
}
