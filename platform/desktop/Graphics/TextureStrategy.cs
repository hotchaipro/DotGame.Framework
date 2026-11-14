// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Desktop.Graphics;

internal sealed class TextureStrategy : ITextureStrategy, IDisposable
{
    private readonly GraphicsServiceStrategy _graphicsService;
    private nint _nativeHandle;

    internal TextureStrategy(
        nint nativeHandle,
        Vector2Int size,
        GraphicsServiceStrategy graphicsService)
    {
        if (nativeHandle == nint.Zero)
        {
            throw new ArgumentException("Native handle must not be zero.", nameof(nativeHandle));
        }

        this._nativeHandle = nativeHandle;
        this._graphicsService = graphicsService ?? throw new ArgumentNullException(nameof(graphicsService));
        this.Size = size;
    }

    ~TextureStrategy()
    {
        this.Dispose(false);
    }

    internal nint NativeHandle => this._nativeHandle != nint.Zero
        ? this._nativeHandle
        : throw new ObjectDisposedException(nameof(TextureStrategy));

    public Vector2Int Size { get; }

    public void SetData(
        RectangleInt region,
        ReadOnlySpan<Color32> data)
    {
        // NOTE: Its own command buffer, submitted now, so it runs before the frame being recorded
        this._graphicsService.UploadPixels(
            this.NativeHandle,
            new Vector2Int(region.Width, region.Height),
            MemoryMarshal.AsBytes(data),
            region.X,
            region.Y);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this.Dispose(true);
    }

    private void Dispose(
        bool _)
    {
        var nativeHandle = this._nativeHandle;
        if (nativeHandle != nint.Zero)
        {
            this._nativeHandle = nint.Zero;

            this._graphicsService.ReleaseTexture(nativeHandle);
        }
    }
}
