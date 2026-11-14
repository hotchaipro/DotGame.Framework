// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Graphics;

/// <summary>
/// Goes back to the shader that was in use when disposed. See <see cref="SpriteBatch.UseShader"/>.
/// </summary>
public readonly struct ShaderScope : IDisposable
{
    private readonly SpriteBatch? _batch;
    private readonly Shader? _previous;
    private readonly int _depth;

    internal ShaderScope(
        SpriteBatch batch,
        Shader? previous,
        int depth)
    {
        this._batch = batch;
        this._previous = previous;
        this._depth = depth;
    }

    /// <summary>Goes back to the shader before this one.</summary>
    public void Dispose()
    {
        this._batch?.RestoreShader(this._previous, this._depth);
    }
}
