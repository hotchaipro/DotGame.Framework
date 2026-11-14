// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.Graphics;
using Android.Opengl;
using DotGame.Framework.Android.Content;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Common.Content;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Threading;

namespace DotGame.Framework.Android.Graphics;

/// <summary>
/// Loads Android textures: WebP (lossless items), decoded by <c>BitmapFactory</c>, or KTX2 files
/// of ASTC blocks (lossy items), which the GPU samples compressed where it has
/// <c>GL_KHR_texture_compression_astc_ldr</c>, as nearly all do. Where it hasn't, the blocks
/// are decoded on the CPU (<see cref="AstcDecoder"/>) and uploaded as RGBA8.
/// </summary>
internal sealed class TextureLoader : IContentLoader<Texture>
{
    // GL_COMPRESSED_RGBA_ASTC_{4x4,6x6,8x8}_KHR
    private const int GlCompressedRgbaAstc4x4 = 0x93B0;
    private const int GlCompressedRgbaAstc6x6 = 0x93B4;
    private const int GlCompressedRgbaAstc8x8 = 0x93B7;

    // Whether the GPU samples ASTC, asked of the GL context once
    private bool? _astcSupported;

    private readonly ContentServiceStrategy _contentService;
    private readonly GraphicsServiceStrategy _graphicsService;
    private readonly List<(string ContentPath, WeakReference<Texture> WeakRef)> _trackedTextures = new();

    private const int PruneInterval = 64;

    // Guards _trackedTextures: a load can finish on any thread, and a reload runs on the GL thread
    private readonly System.Threading.Lock _trackedTexturesLock = new();

    public TextureLoader(
        ContentServiceStrategy contentService,
        GraphicsServiceStrategy graphicsService)
    {
        this._contentService = contentService;
        this._graphicsService = graphicsService;
    }

    public async Task<Texture> LoadAsync(
        string contentKey)
    {
        var textureStrategy = await this.LoadTextureStrategy(contentKey).ConfigureAwait(false);

        var texture = new Texture(new Strategy<ITextureStrategy>(textureStrategy));
        lock (this._trackedTexturesLock)
        {
            // Now and then, so the list doesn't grow with every load of a long session
            if (((this._trackedTextures.Count + 1) % PruneInterval) == 0)
            {
                this._trackedTextures.RemoveAll(static entry => !entry.WeakRef.TryGetTarget(out var target) || target.IsDisposed);
            }

            this._trackedTextures.Add((contentKey, new WeakReference<Texture>(texture)));
        }

        return texture;
    }

    internal async Task ReloadAll()
    {
        (string ContentPath, WeakReference<Texture> WeakRef)[] tracked;
        lock (this._trackedTexturesLock)
        {
            tracked = [.. this._trackedTextures];
        }

        for (int i = tracked.Length - 1; i >= 0; i--)
        {
            var (contentKey, weakRef) = tracked[i];

            // NOTE: An unloaded texture can still be here until it's collected
            if (weakRef.TryGetTarget(out var texture) && !texture.IsDisposed)
            {
                texture.Strategy.ToConcrete<TextureStrategy>().Invalidate();

                var newStrategy = await this.LoadTextureStrategy(contentKey).ConfigureAwait(false);
                texture.Strategy.Reset(newStrategy);
            }
            else
            {
                lock (this._trackedTexturesLock)
                {
                    this._trackedTextures.Remove(tracked[i]);
                }
            }
        }
    }

    private async Task<TextureStrategy> LoadTextureStrategy(
        string contentKey)
    {
        var fullPath = this._contentService.GetFullPath(this._contentService.ResolveContentPath(contentKey));

        var context = global::Android.App.Application.Context;
        var assetManager = context.Assets
            ?? throw new InvalidOperationException("Android AssetManager is not available.");

        // GPU compressed textures are KTX2 files
        if (fullPath.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase))
        {
            Ktx2Texture ktx2;
            using (var stream = assetManager.Open(fullPath))
            {
                ktx2 = Ktx2Texture.Read(stream, fullPath);
            }

            using (ktx2)
            {
                return await this.LoadKtx2(ktx2).ConfigureAwait(false);
            }
        }

        // Background-safe phase: decode runs inline on whatever thread invoked
        // the loader (game thread for preload, worker thread for postload).
        // Decode without premultiplying, so the texture holds exactly the values
        // the content pipeline stored: textures that should be premultiplied were
        // premultiplied at build time, and data textures such as MSDF font atlases
        // must not be altered. Skipping the premultiply costs nothing.
        using var options = new BitmapFactory.Options
        {
            InPreferredConfig = Bitmap.Config.Argb8888,
            InPremultiplied = false,
            InScaled = false,
        };

        Bitmap bitmap;
        using (var stream = assetManager.Open(fullPath))
        {
            bitmap = BitmapFactory.DecodeStream(stream, null, options)
                ?? throw new InvalidOperationException($"Failed to decode texture: '{fullPath}'");
        }

        // An ARGB_8888 bitmap stores each pixel as R, G, B, A bytes, the layout that
        // GL_RGBA / GL_UNSIGNED_BYTE expects, in tightly packed rows (GLES 2 cannot
        // skip row padding).
        if ((bitmap.GetConfig() != Bitmap.Config.Argb8888)
            || (bitmap.RowBytes != bitmap.Width * 4))
        {
            bitmap.Recycle();
            throw new InvalidOperationException($"Texture '{fullPath}' did not decode to tightly packed ARGB_8888.");
        }

        // Engine-restricted phase: GL texture creation must run on the GL
        // context (game) thread. Synchronous fast path when already on it.
        return await GameThread.InvokeAsync(() =>
        {
            int[] texIds = new int[1];
            GLES20.GlGenTextures(1, texIds, 0);
            int texId = texIds[0];

            GLES20.GlBindTexture(GLES20.GlTexture2d, texId);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);

            var size = new Vector2Int(bitmap.Width, bitmap.Height);

            // Upload straight from the bitmap's own memory. Unlike GLUtils.TexImage2D,
            // this uploads a non-premultiplied bitmap as is, with no extra copy.
            var pixels = bitmap.LockPixels();
            try
            {
                GLBindings.glTexImage2D(
                    GLES20.GlTexture2d,
                    0,
                    GLES20.GlRgba,
                    size.X,
                    size.Y,
                    0,
                    GLES20.GlRgba,
                    GLES20.GlUnsignedByte,
                    pixels);
            }
            finally
            {
                bitmap.UnlockPixels();
                bitmap.Recycle();
                bitmap.Dispose();
            }

            GLES20.GlBindTexture(GLES20.GlTexture2d, 0);

            return new TextureStrategy(texId, size);
        });
    }

    private async Task<TextureStrategy> LoadKtx2(
        Ktx2Texture ktx2)
    {
        var size = ktx2.Size;
        if (ktx2.Format == Ktx2PixelFormat.Rgba8)
        {
            return await GameThread.InvokeAsync(() => CreateTexture(size, ktx2.Data, compressedFormat: 0)).ConfigureAwait(false);
        }

        int compressedFormat = ktx2.Format switch
        {
            Ktx2PixelFormat.Astc4x4 => GlCompressedRgbaAstc4x4,
            Ktx2PixelFormat.Astc6x6 => GlCompressedRgbaAstc6x6,
            _ => GlCompressedRgbaAstc8x8,
        };

        // The GL context says whether it samples ASTC; ask it once, on its thread
        this._astcSupported ??= await GameThread.InvokeAsync(static () =>
        {
            string extensions = GLES20.GlGetString(GLES20.GlExtensions) ?? string.Empty;
            bool supported = extensions.Contains("GL_KHR_texture_compression_astc_ldr", StringComparison.Ordinal);
            Trace.Write(0x1f6a2c3e, $"[TextureLoader] ASTC {(supported ? "sampled by the GPU" : "not supported by the GPU: decoding on the CPU")}");
            return supported;
        }).ConfigureAwait(false);

        if (this._astcSupported == true)
        {
            return await GameThread.InvokeAsync(() => CreateTexture(size, ktx2.Data, compressedFormat)).ConfigureAwait(false);
        }

        // No ASTC on this GPU: decode the blocks off the GL thread, even during startup, as it can
        // take seconds on the devices least able to afford it, and upload RGBA8
        byte[] pixels = await Task.Run(() => AstcDecoder.Decode(ktx2.Data, size.X, size.Y, ktx2.BlockSize)).ConfigureAwait(false);
        return await GameThread.InvokeAsync(() => CreateTexture(size, pixels, compressedFormat: 0)).ConfigureAwait(false);
    }

    // A GL texture of RGBA8 pixels, or of compressed blocks when compressedFormat isn't 0, with
    // the same sampling as the WebP textures'. Runs on the GL thread.
    private static unsafe TextureStrategy CreateTexture(
        Vector2Int size,
        ReadOnlySpan<byte> data,
        int compressedFormat)
    {
        int[] texIds = new int[1];
        GLES20.GlGenTextures(1, texIds, 0);
        int texId = texIds[0];

        GLES20.GlBindTexture(GLES20.GlTexture2d, texId);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);

        fixed (byte* pointer = data)
        {
            if (compressedFormat == 0)
            {
                GLBindings.glTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, size.X, size.Y, 0, GLES20.GlRgba, GLES20.GlUnsignedByte, (nint)pointer);
            }
            else
            {
                GLBindings.glCompressedTexImage2D(GLES20.GlTexture2d, 0, compressedFormat, size.X, size.Y, 0, data.Length, (nint)pointer);
            }
        }

        GLES20.GlBindTexture(GLES20.GlTexture2d, 0);

        return new TextureStrategy(texId, size);
    }
}
