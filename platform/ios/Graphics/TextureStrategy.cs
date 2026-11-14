// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Platform.Graphics;
using Metal;

namespace DotGame.Framework.iOS.Graphics;

internal sealed class TextureStrategy : ITextureStrategy, IDisposable
{
    private readonly GraphicsServiceStrategy _graphicsService;
    private IMTLTexture? _texture;

    internal TextureStrategy(
        IMTLTexture texture,
        Vector2Int size,
        GraphicsServiceStrategy graphicsService)
    {
        this._graphicsService = graphicsService;
        this._texture = texture ?? throw new ArgumentNullException(nameof(texture));
        this.Size = size;
    }

    ~TextureStrategy()
    {
        this.Dispose(false);
    }

    internal IMTLTexture NativeTexture => this._texture
        ?? throw new ObjectDisposedException(nameof(TextureStrategy));

    public Vector2Int Size { get; }

    public unsafe void SetData(
        RectangleInt region,
        ReadOnlySpan<Color32> data)
    {
        fixed (Color32* pointer = data)
        {
            this.NativeTexture.ReplaceRegion(
                new MTLRegion(new MTLOrigin(region.X, region.Y, 0), new MTLSize(region.Width, region.Height, 1)),
                0,
                (nint)pointer,
                (nuint)(region.Width * 4));
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this.Dispose(true);
    }

    private void Dispose(
        bool isDisposing)
    {
        if (isDisposing && (this._texture is { } texture))
        {
            // NOTE: End doesn't draw, so sprites may still be waiting on this texture
            this._graphicsService.FlushSpritesUsing(texture);
            texture.Dispose();
        }

        this._texture = null;
    }
}
