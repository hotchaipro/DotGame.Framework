// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Numerics;

namespace DotGame.Framework.Graphics;

/// <summary>
/// How a sprite batch draws. Its defaults are the plain case: no transform,
/// <see cref="BlendMode.Alpha"/> and <see cref="Sampler.LinearClamp"/>.
/// </summary>
public readonly record struct SpriteBatchSettings
{
    /// <summary>
    /// Gets the transform applied to every sprite, such as a camera's, or <c>null</c> for none.
    /// </summary>
    public Matrix3x2? Transform { get; init; }

    /// <summary>
    /// Gets how sprites combine with what's behind them.
    /// </summary>
    public BlendMode Blend { get; init; }

    /// <summary>
    /// Gets how textures are read.
    /// </summary>
    public Sampler Sampler { get; init; }
}
