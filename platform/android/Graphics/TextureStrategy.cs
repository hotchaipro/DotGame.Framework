// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Android.Graphics;

internal sealed class TextureStrategy : ITextureStrategy, IDisposable
{
    private int _textureId;
    private readonly int _generation = GLResources.Generation;

    internal TextureStrategy(
        int textureId,
        Vector2Int size)
    {
        this._textureId = textureId;
        this.Size = size;
    }

    ~TextureStrategy()
    {
        this.Dispose(false);
    }

    internal int TextureId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => this._textureId != 0
            ? this._textureId
            : throw new ObjectDisposedException(nameof(TextureStrategy));
    }

    public Vector2Int Size { get; }

    // The service that made it from pixels; other textures can't have them replaced
    internal GraphicsServiceStrategy? Owner { get; init; }

    public void SetData(
        RectangleInt region,
        ReadOnlySpan<Color32> data)
    {
        if (this.Owner is null)
        {
            throw new InvalidOperationException("Only a texture you created with CreateTexture can have its pixels replaced.");
        }

        this.Owner.UpdateTexture(this.TextureId, region, data);
    }

    /// <summary>
    /// Releases the texture id without calling glDeleteTextures.
    /// Used during context restore when GL objects have already been
    /// invalidated by the system.
    /// </summary>
    internal void Invalidate()
    {
        this._textureId = 0;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this.Dispose(true);
    }

    private void Dispose(
        bool _)
    {
        var textureId = this._textureId;
        if (textureId != 0)
        {
            this._textureId = 0;
            GLResources.DeleteTexture(textureId, this._generation);
        }
    }
}
