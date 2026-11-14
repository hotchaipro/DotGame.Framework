// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Graphics;

/// <summary>
/// Goes back to the limit on drawing that was in use when disposed. See <see cref="GraphicsService.UseScissor"/>.
/// </summary>
public readonly struct ScissorScope : IDisposable
{
    private readonly GraphicsService? _graphics;
    private readonly RectangleInt? _previous;
    private readonly int _depth;

    internal ScissorScope(
        GraphicsService graphics,
        RectangleInt? previous,
        int depth)
    {
        this._graphics = graphics;
        this._previous = previous;
        this._depth = depth;
    }

    /// <summary>Goes back to the limit before this one.</summary>
    public void Dispose()
    {
        this._graphics?.RestoreScissor(this._previous, this._depth);
    }
}
