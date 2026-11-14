// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Graphics;

/// <summary>
/// Ends a sprite batch when disposed. See <see cref="SpriteBatch.Begin()"/>.
/// </summary>
public readonly struct SpriteBatchScope : IDisposable
{
    private readonly SpriteBatch? _batch;
    private readonly int _batchNumber;

    internal SpriteBatchScope(
        SpriteBatch batch,
        int batchNumber)
    {
        this._batch = batch;
        this._batchNumber = batchNumber;
    }

    /// <summary>Ends the batch.</summary>
    public void Dispose()
    {
        this._batch?.EndBatch(this._batchNumber);
    }
}
