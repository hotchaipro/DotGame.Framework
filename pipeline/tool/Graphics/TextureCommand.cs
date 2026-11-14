// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers;
using DotGame.Imaging;
using DotGame.Imaging.WebP;

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// Encodes a source image to a platform's texture format, chosen by <c>--format</c>: WebP
/// (<c>webp</c>, the default), encoded by DotGame.Imaging (cwebp's bytes), for the web, Android and iOS, which decode it
/// natively; or KTX2 (<c>ktx2</c>, see <see cref="Ktx2Format"/>): RGBA8, zlib supercompressed, for
/// desktop, or, with <c>--gpu-compression astc</c>, ASTC blocks (encoded by Arm's astcenc) for lossy
/// items on Android and iOS. Any
/// PNG, JPEG or WebP image is accepted (see <see cref="RgbaImage.Load"/>).
/// </summary>
/// <remarks>
/// <para>
/// Texture alpha is premultiplied at build time, deterministically, here rather
/// than with an encoder filter.  Textures whose channels are data rather than
/// color (for example the distance fields of an MSDF font atlas) opt out with
/// '--premultiply-alpha false', so their channels are stored exactly as in the
/// source.
/// </para>
/// <para>
/// <c>--compression</c> picks the codec, <c>Lossless</c> or <c>Lossy</c>, and <c>--quality</c>
/// (1 to 10) how close to the source it stays. Lossless at 10, its default, is exact; lower lets
/// colors change a little where that makes the file smaller, never further than the quality asked
/// for allows (<see cref="NearLosslessLevel"/>). Lossy's default is 8. KTX2 RGBA8 is always exact,
/// so items are stored exactly there where no GPU compression is chosen. ASTC is only for lossy items; its quality picks the
/// block size (8 to 10: 4x4, 8 bits a pixel; 5 to 7: 6x6, 3.56; 1 to 4: 8x8, 2), and astcenc
/// always searches thoroughly, since encoding is cached.
/// </para>
/// <para>
/// WebP is encoded by DotGame.Imaging's port of libwebp, with what were cwebp's settings, so a texture
/// is the file cwebp 1.6.0 wrote: <c>-lossless -exact -m 6 -q 100</c>, plus
/// <c>-near_lossless</c> (<see cref="NearLosslessLevel"/>) at lossless qualities below 9, or
/// <c>-q (quality x 10) -exact -m 6 -alpha_q 100</c>, all <c>-mt</c>. Exact keeps the RGB of fully
/// transparent pixels, which premultiplied-alpha blending (no color fringes at transparent edges) and
/// data textures need; without it the encoder rewrites them to compress better.
/// </para>
/// </remarks>
internal static class TextureCommand
{
    public const string Name = "texture";
    public const string ErrorCode = "TEX0001";

    public static void Run(CommandLineOptions options)
    {
        string format = options.GetChoice("format", "webp", "webp", "ktx2");
        string gpuCompression = options.GetChoice("gpu-compression", "none", "none", "astc");
        string inputFile = options.GetPath("input");
        string outputFile = options.GetPath("output");
        string compression = options.GetChoice("compression", null, "Lossless", "Lossy");
        int quality = options.GetInt32("quality", (compression == "Lossless") ? 10 : 8, 1, 10);
        bool premultiply = options.GetChoice("premultiply-alpha", "true", "true", "false") == "true";

        if ((gpuCompression != "none") && ((format != "ktx2") || (compression != "Lossy")))
        {
            throw new ContentBuildException($"--gpu-compression {gpuCompression} needs --format ktx2 and --compression Lossy: GPU compression is lossy, and only KTX2 holds it.");
        }

        // astcenc needs a place for its intermediate PNG
        string? astcenc = null;
        string? intermediateDir = null;
        if (gpuCompression == "astc")
        {
            astcenc = options.GetToolPath("astcenc", "astcenc");
            intermediateDir = options.GetPath("intermediate-dir");
        }

        options.ThrowIfUnrecognized();

        if (!File.Exists(inputFile))
        {
            throw new ContentBuildException($"Source image file not found: '{inputFile}'");
        }

        var image = RgbaImage.Load(inputFile);
        if (premultiply)
        {
            image.PremultiplyAlpha();
        }

        ContentPaths.CreateParentDirectory(outputFile);

        if (astcenc is not null)
        {
            EncodeAstc(image, inputFile, outputFile, intermediateDir!, astcenc, quality, premultiply);
        }
        else if (format == "ktx2")
        {
            ContentPaths.RetryWhileLocked(() => Ktx2Format.WriteRgba8(outputFile, image, premultiply), $"Writing '{outputFile}'");
        }
        else
        {
            EncodeWebP(image, outputFile, compression, quality);
        }
    }

    // The ASTC block size an item's quality asks for
    internal static int AstcBlockSize(
        int quality)
    {
        return (quality >= 8) ? 4 : (quality >= 5) ? 6 : 8;
    }

    private static void EncodeAstc(
        RgbaImage image,
        string inputFile,
        string outputFile,
        string intermediateDir,
        string astcenc,
        int quality,
        bool premultiply)
    {
        string baseName = Path.GetFileNameWithoutExtension(inputFile);
        string pngFile = Path.Combine(intermediateDir, $"{baseName}.{(premultiply ? "premul" : "straight")}.png");
        string astcFile = Path.Combine(intermediateDir, $"{baseName}.astc");
        Directory.CreateDirectory(intermediateDir);
        ContentPaths.RetryWhileLocked(() => image.SavePng(pngFile), $"Writing '{pngFile}'");

        // -cl        LDR, linear: the pixels are sampled as UNORM, as the framework's textures are.
        // -thorough  a near exhaustive search (quality 98); encoding is cached.
        int block = AstcBlockSize(quality);
        string blockSize = $"{block}x{block}";
        ExternalProcess.Run(astcenc, ["-cl", pngFile, astcFile, blockSize, "-thorough", "-silent"], $"astcenc encode of '{inputFile}'");

        byte[] blocks = ReadAstcFile(astcFile, image.Width, image.Height, block);
        ContentPaths.RetryWhileLocked(() => Ktx2Format.WriteAstc(outputFile, image.Width, image.Height, block, blocks, premultiply), $"Writing '{outputFile}'");
    }

    // An .astc file's blocks: after its 16 byte header (magic 0x5CA1AB13, the block's three
    // dimensions, then the image's three, 24 bits each, all little endian)
    private static byte[] ReadAstcFile(
        string path,
        int width,
        int height,
        int block)
    {
        byte[] data = File.ReadAllBytes(path);
        if ((data.Length < 16) || (BitConverter.ToUInt32(data, 0) != 0x5CA1AB13))
        {
            throw new ContentBuildException($"astcenc didn't write an ASTC file: '{path}'");
        }

        int xSize = data[7] | (data[8] << 8) | (data[9] << 16);
        int ySize = data[10] | (data[11] << 8) | (data[12] << 16);
        if ((data[4] != block) || (data[5] != block) || (data[6] != 1) || (xSize != width) || (ySize != height))
        {
            throw new ContentBuildException($"astcenc wrote {data[4]}x{data[5]}x{data[6]} blocks of a {xSize} x {ySize} image, not {block}x{block} blocks of {width} x {height}: '{path}'");
        }

        return data.AsSpan(16).ToArray();
    }

    // libwebp's near-lossless level (cwebp's -near_lossless) for a lossless quality: the nearest of its
    // levels (0, 20, ... 100, where 100 is exact) at or above quality x 10, so a texture is never further
    // from its source than the quality asked for allows. Each level below 100 doubles how far a channel
    // may move: 9 and 10 are exact, 7 and 8 move by 1 level at most, 5 and 6 by 2, 3 and 4 by 4, 1 and 2 by 8.
    internal static int NearLosslessLevel(
        int quality)
    {
        return Math.Min(100, ((quality * 10) + 19) / 20 * 20);
    }

    // The pixels as they are (premultiplied or not, as the texture stores them), with what were cwebp's
    // settings: the same file
    internal static void EncodeWebP(
        RgbaImage image,
        string outputFile,
        string compression,
        int quality)
    {
        int nearLossless = NearLosslessLevel(quality);
        var options = compression switch
        {
            "Lossless" when nearLossless == 100 => new WebPEncoderOptions { Lossless = true, Exact = true, Method = 6, Quality = 100, UseThreads = true },
            "Lossless" => new WebPEncoderOptions { Lossless = true, NearLossless = nearLossless, Exact = true, Method = 6, Quality = 100, UseThreads = true },
            _ => new WebPEncoderOptions { Quality = quality * 10, Exact = true, Method = 6, AlphaQuality = 100, UseThreads = true },
        };
        var webp = new ArrayBufferWriter<byte>();
        WebPCodec.Encode(webp, image.Width, image.Height, image.Pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false, options);
        ContentPaths.RetryWhileLocked(() => File.WriteAllBytes(outputFile, webp.WrittenSpan), $"Writing '{outputFile}'");
    }
}
