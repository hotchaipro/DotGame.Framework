// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;
using CoreGraphics;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.iOS.Graphics;
using DotGame.Framework.Platform.Common.Content;
using DotGame.Framework.Platform.Graphics;
using Foundation;
using ImageIO;
using Metal;

namespace DotGame.Framework.iOS.Content;

/// <summary>
/// Loads iOS textures: WebP (lossless items), decoded by ImageIO, or KTX2 files of ASTC blocks
/// (lossy items), which Metal samples compressed, so they upload as they are.
/// </summary>
internal sealed class TextureLoader : IContentLoader<Texture>
{
    private readonly ContentServiceStrategy _contentService;
    private readonly GraphicsServiceStrategy _graphicsService;

    public TextureLoader(
        ContentServiceStrategy contentService,
        GraphicsServiceStrategy graphicsService)
    {
        this._contentService = contentService;
        this._graphicsService = graphicsService;
    }

    // NOTE: Metal creates and fills textures from any thread, so a load stays on the thread
    // it runs on, and a large one doesn't hold up a frame
    public Task<Texture> LoadAsync(
        string contentKey)
    {
        var fullPath = this._contentService.GetFullPath(this._contentService.ResolveContentPath(contentKey));

        Trace.Write(0x3e2f68b2, $"[TextureLoader] Loading '{fullPath}'");

        // GPU compressed textures are KTX2 files, uploaded as they are
        if (IsKtx2File(fullPath))
        {
            using var ktx2 = Ktx2Texture.ReadFile(fullPath);
            var compressed = this.CreateTexture(ktx2);
            Trace.Write(0x6b1d2f47, $"[TextureLoader] Loaded texture '{contentKey}' ({ktx2.Size.X}x{ktx2.Size.Y} {ktx2.Format})");
            return Task.FromResult(compressed);
        }

        var decoded = DecodeImage(fullPath);
        var texture = this.CreateTexture(decoded.Pixels, decoded.Width, decoded.Height);

        Trace.Write(0xc961cae9, $"[TextureLoader] Loaded texture '{contentKey}' ({decoded.Width}x{decoded.Height})");

        return Task.FromResult(texture);
    }

    private static bool IsKtx2File(
        string fullPath)
    {
        Span<byte> start = stackalloc byte[12];
        using var file = File.OpenRead(fullPath);
        return (file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) == start.Length) && Ktx2Texture.IsKtx2(start);
    }

    // A KTX2 texture as a Metal texture of its format: ASTC blocks (rows of 16 byte blocks) or
    // RGBA8 pixels, uploaded as they are
    private Texture CreateTexture(
        Ktx2Texture ktx2)
    {
        var pixelFormat = ktx2.Format switch
        {
            Ktx2PixelFormat.Astc4x4 => MTLPixelFormat.ASTC_4x4_LDR,
            Ktx2PixelFormat.Astc6x6 => MTLPixelFormat.ASTC_6x6_LDR,
            Ktx2PixelFormat.Astc8x8 => MTLPixelFormat.ASTC_8x8_LDR,
            _ => MTLPixelFormat.RGBA8Unorm,
        };

        int width = ktx2.Size.X;
        int height = ktx2.Size.Y;
        int block = ktx2.BlockSize;
        int bytesPerRow = (block == 1) ? width * 4 : ((width + block - 1) / block) * 16;

        using var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(pixelFormat, (nuint)width, (nuint)height, false);
        desc.Usage = MTLTextureUsage.ShaderRead;

        var metalTexture = this._graphicsService.Device.CreateTexture(desc)
            ?? throw new InvalidOperationException($"Failed to create a {pixelFormat} Metal texture");

        unsafe
        {
            fixed (byte* ptr = ktx2.Data)
            {
                // The region is in texels; edge blocks that overhang the image are uploaded whole
                metalTexture.ReplaceRegion(
                    new MTLRegion(
                        new MTLOrigin(0, 0, 0),
                        new MTLSize((nint)width, (nint)height, 1)),
                    0,
                    (nint)ptr,
                    (nuint)bytesPerRow);
            }
        }

        var textureStrategy = new TextureStrategy(metalTexture, ktx2.Size, this._graphicsService);
        return new Texture(new Strategy<ITextureStrategy>(textureStrategy));
    }

    // The image's pixels as the content pipeline stored them: RGBA, premultiplied already (or
    // data, such as an MSDF font atlas's distance fields, which mustn't be). ImageIO decodes a
    // WebP to straight RGBA, which is copied as it is. Drawing it into a bitmap context instead
    // would premultiply it again (Core Graphics' 8-bit RGBA contexts are premultiplied only),
    // darkening translucent edges twice over and corrupting distance fields.
    private static (byte[] Pixels, int Width, int Height) DecodeImage(
        string fullPath)
    {
        using var url = NSUrl.FromFilename(fullPath);
        using var imageSource = CGImageSource.FromUrl(url);

        if (imageSource is null)
        {
            throw new InvalidOperationException($"Failed to create image source: '{fullPath}'");
        }

        using var cgImage = imageSource.CreateImage(0, new CGImageOptions());

        if (cgImage is null)
        {
            throw new InvalidOperationException($"Failed to decode image: '{fullPath}'");
        }

        int width = (int)cgImage.Width;
        int height = (int)cgImage.Height;

        // Four bytes a pixel, in R, G, B, A order (the default or big-endian byte order)
        var byteOrder = cgImage.BitmapInfo & CGBitmapFlags.ByteOrderMask;
        bool rgbaBytes = (cgImage.BitsPerComponent == 8) && (cgImage.BitsPerPixel == 32)
            && ((byteOrder == CGBitmapFlags.ByteOrderDefault) || (byteOrder == CGBitmapFlags.ByteOrder32Big))
            && ((cgImage.BitmapInfo & CGBitmapFlags.FloatComponents) == 0);
        var alphaInfo = cgImage.AlphaInfo;
        if (rgbaBytes && ((alphaInfo == CGImageAlphaInfo.Last) || (alphaInfo == CGImageAlphaInfo.PremultipliedLast) || (alphaInfo == CGImageAlphaInfo.NoneSkipLast)))
        {
            var pixels = CopyPixels(cgImage, width, height);
            if (alphaInfo == CGImageAlphaInfo.NoneSkipLast)
            {
                // The fourth byte is padding: the image is opaque
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 0xFF;
                }
            }
            else if (alphaInfo == CGImageAlphaInfo.PremultipliedLast)
            {
                // ImageIO premultiplied what was already premultiplied: divide it out again,
                // which restores the colors to within rounding (exact data can't be restored)
                Trace.Write(0x5a0c3e81, $"[TextureLoader] '{fullPath}' decoded premultiplied: dividing out the extra alpha");
                Unpremultiply(pixels);
            }

            return (pixels, width, height);
        }

        // Any other layout (opaque RGB, for one, which drawing doesn't change): drawn into RGBA
        if (alphaInfo != CGImageAlphaInfo.None)
        {
            Trace.Write(0x2b7f9d14, $"[TextureLoader] '{fullPath}' decoded as {cgImage.BitsPerPixel} bits a pixel, {alphaInfo}, {byteOrder}: drawing it premultiplies its colors again");
        }

        var rgbaData = new byte[width * height * 4];
        using var colorSpace = CGColorSpace.CreateDeviceRGB();

        unsafe
        {
            fixed (byte* ptr = rgbaData)
            {
                using var context = new CGBitmapContext(
                    (nint)ptr,
                    width,
                    height,
                    8,
                    width * 4,
                    colorSpace,
                    CGImageAlphaInfo.PremultipliedLast);

                context.DrawImage(
                    new CGRect(0, 0, width, height),
                    cgImage);
            }
        }

        return (rgbaData, width, height);
    }

    // The image's bytes, a tightly packed row at a time (its rows may be padded)
    private static byte[] CopyPixels(
        CGImage cgImage,
        int width,
        int height)
    {
        using var data = cgImage.DataProvider?.CopyData()
            ?? throw new InvalidOperationException("The decoded image has no pixel data.");

        int rowLength = width * 4;
        int stride = (int)cgImage.BytesPerRow;
        if ((stride < rowLength) || (((long)stride * (height - 1)) + rowLength > (long)data.Length))
        {
            throw new InvalidOperationException($"The decoded image's rows ({stride} bytes) don't hold {width} pixels.");
        }

        var pixels = GC.AllocateUninitializedArray<byte>(rowLength * height);
        unsafe
        {
            var source = new ReadOnlySpan<byte>((void*)data.Bytes, (int)data.Length);
            for (int y = 0; y < height; y++)
            {
                source.Slice(y * stride, rowLength).CopyTo(pixels.AsSpan(y * rowLength, rowLength));
            }
        }

        return pixels;
    }

    // Divides each translucent pixel's colors by its alpha, rounding to the nearest
    private static void Unpremultiply(
        byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if ((a == 0) || (a == 255))
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                pixels[i + c] = (byte)Math.Min(255, ((pixels[i + c] * 255) + (a / 2)) / a);
            }
        }
    }

    private Texture CreateTexture(
        byte[] rgbaData,
        int width,
        int height)
    {
        var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(
            MTLPixelFormat.RGBA8Unorm, (nuint)width, (nuint)height, false);
        desc.Usage = MTLTextureUsage.ShaderRead;

        var metalTexture = this._graphicsService.Device.CreateTexture(desc)
            ?? throw new InvalidOperationException("Failed to create Metal texture");

        unsafe
        {
            fixed (byte* ptr = rgbaData)
            {
                metalTexture.ReplaceRegion(
                    new MTLRegion(
                        new MTLOrigin(0, 0, 0),
                        new MTLSize((nint)width, (nint)height, 1)),
                    0,
                    (nint)ptr,
                    (nuint)(width * 4));
            }
        }

        desc.Dispose();

        var size = new Vector2Int(width, height);
        var textureStrategy = new TextureStrategy(metalTexture, size, this._graphicsService);
        return new Texture(new Strategy<ITextureStrategy>(textureStrategy));
    }
}
