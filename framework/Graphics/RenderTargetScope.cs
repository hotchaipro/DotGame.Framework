// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Graphics;

/// <summary>
/// Goes back to the render target that was in use when disposed, and for a batch's target, to how the
/// batch was drawing to it. See <see cref="GraphicsService.UseRenderTarget"/> and
/// <see cref="SpriteBatch.UseRenderTarget"/>.
/// </summary>
public readonly struct RenderTargetScope : IDisposable
{
    private readonly GraphicsService? _graphics;
    private readonly RenderTexture? _previous;
    private readonly int _depth;
    private readonly RectangleInt? _previousScissor;
    private readonly int _scissorDepth;

    // What a batch comes back to, when this is a batch's
    private readonly SpriteBatch? _batch;

    internal RenderTargetScope(
        GraphicsService graphics,
        RenderTexture? previous,
        int depth,
        RectangleInt? previousScissor,
        int scissorDepth)
    {
        this._graphics = graphics;
        this._previous = previous;
        this._depth = depth;
        this._previousScissor = previousScissor;
        this._scissorDepth = scissorDepth;
    }

    private RenderTargetScope(
        in RenderTargetScope target,
        SpriteBatch batch,
        int batchNumber,
        int batchDepth,
        in SpriteBatchSettings settings,
        Shader? shader,
        int shaderDepth)
    {
        this = target;
        this._batch = batch;
        this.BatchNumber = batchNumber;
        this.BatchDepth = batchDepth;
        this.BatchSettings = settings;
        this.BatchShader = shader;
        this.BatchShaderDepth = shaderDepth;
    }

    internal int BatchNumber { get; }

    internal int BatchDepth { get; }

    internal SpriteBatchSettings BatchSettings { get; }

    internal Shader? BatchShader { get; }

    internal int BatchShaderDepth { get; }

    /// <summary>Goes back to the target before this one.</summary>
    public void Dispose()
    {
        if (this._batch is not null)
        {
            this._batch.RestoreRenderTarget(in this);
        }
        else
        {
            this.RestoreTarget();
        }
    }

    internal RenderTargetScope ForBatch(
        SpriteBatch batch,
        int batchNumber,
        int batchDepth,
        in SpriteBatchSettings settings,
        Shader? shader,
        int shaderDepth)
    {
        return new RenderTargetScope(in this, batch, batchNumber, batchDepth, in settings, shader, shaderDepth);
    }

    internal void RestoreTarget()
    {
        this._graphics?.RestoreRenderTarget(this._previous, this._depth, this._previousScissor, this._scissorDepth);
    }
}
