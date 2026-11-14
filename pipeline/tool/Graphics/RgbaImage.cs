// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers;
using DotGame.Imaging;
using DotGame.Imaging.Png;

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// An 8-bit straight-alpha RGBA image, row-major with no row padding.
/// </summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
        : this(width, height, new byte[checked(width * height * 4)])
    {
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid image size {width}x{height}.");
        }
        if (pixels.Length != (long)width * height * 4)
        {
            throw new ArgumentException("Pixel buffer size does not match the image size.", nameof(pixels));
        }
        this.Width = width;
        this.Height = height;
        this.Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public int Stride => this.Width * 4;

    // A PNG's colors are used as they are, so one in a wider space (Display P3) looks different
    private static void WarnOfColorProfile(string path, byte[] data)
    {
        if (HasOtherColorProfile(data))
        {
            ContentBuildOutput.Warning(path, "IMG0001", $"The image '{path}' has a color profile other than sRGB, which isn't applied: its colors are used as they are.");
        }
    }

    internal static bool HasOtherColorProfile(byte[] data)
    {
        ReadOnlySpan<byte> file = data;
        if (!file.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return false;
        }

        int position = 8;
        while ((position + 8) <= file.Length)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.Slice(position, 4));
            var type = file.Slice(position + 4, 4);
            if (type.SequenceEqual("IDAT"u8) || (length < 0) || (length > (file.Length - position - 8)))
            {
                return false;
            }

            if (type.SequenceEqual("iCCP"u8))
            {
                return !IsSrgbProfile(data, position + 8, length);
            }

            position += 12 + length;
        }

        return false;
    }

    // NOTE: The chunk's name is whatever the tool chose ("Photoshop ICC profile"), so the profile's
    // own text says what it is: ASCII in a version 2 profile, UTF-16 in a version 4 one. One that
    // can't be read counts as sRGB, as nothing can be said about it.
    private static bool IsSrgbProfile(byte[] data, int offset, int length)
    {
        const int MaxProfileLength = 16 * 1024 * 1024;

        int nameEnd = Array.IndexOf(data, (byte)0, offset, length);
        int compressed = nameEnd + 2;
        if ((nameEnd < 0) || (compressed >= (offset + length)))
        {
            return true;
        }

        try
        {
            using var input = new MemoryStream(data, compressed, offset + length - compressed);
            using var inflate = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var profile = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while (((read = inflate.Read(buffer)) > 0) && (profile.Length < MaxProfileLength))
            {
                profile.Write(buffer, 0, read);
            }

            ReadOnlySpan<byte> bytes = profile.GetBuffer().AsSpan(0, (int)profile.Length);
            return (bytes.IndexOf("sRGB"u8) >= 0) || (bytes.IndexOf((ReadOnlySpan<byte>)[0, (byte)'s', 0, (byte)'R', 0, (byte)'G', 0, (byte)'B']) >= 0);
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    /// <summary>
    /// Decodes an image to 8-bit RGBA, with no color or gamma conversion, with the imaging library games
    /// read images with (DotGame.Imaging's ImageDecoder): any format it reads, which is PNG of every color
    /// type and bit depth (16-bit samples keep their high byte), JPEG as libjpeg-turbo decodes it, and WebP
    /// as libwebp does. Other formats fail with an error naming the format.
    /// </summary>
    public static RgbaImage Load(string path)
    {
        byte[] data = ContentPaths.RetryWhileLocked(() => File.ReadAllBytes(path), $"Reading '{path}'");
        WarnOfColorProfile(path, data);
        if (!ImageDecoder.TryGetDecodedSize(data, PixelLayout.Rgba32, out int width, out int height, out int byteCount))
        {
            throw new ContentBuildException($"The image '{path}' is {Describe(data)}: texture sources are PNG, JPEG or WebP.");
        }

        var pixels = new byte[byteCount];
        try
        {
            ImageDecoder.Decode(data, pixels, PixelLayout.Rgba32, premultiplyAlpha: false);
        }
        catch (InvalidDataException ex)
        {
            throw new ContentBuildException($"Failed to decode image '{path}': {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new ContentBuildException($"Can't decode image '{path}': {ex.Message}");
        }

        return new RgbaImage(width, height, pixels);
    }

    // What an image the decoder doesn't read is, for the error
    internal static string Describe(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "a PNG image whose header is damaged, or one too large to hold";
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "a JPEG image whose frame header is damaged or missing";
        }

        if (data.StartsWith("GIF8"u8))
        {
            return "a GIF";
        }

        if (data.StartsWith("BM"u8))
        {
            return "a BMP";
        }

        if (data.StartsWith("II*\0"u8) || data.StartsWith("MM\0*"u8))
        {
            return "a TIFF";
        }

        if ((data.Length >= 12) && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "a WebP image whose header is damaged";
        }

        if (data.StartsWith("8BPS"u8))
        {
            return "a Photoshop document";
        }

        if ((data.Length >= 12) && data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return "an HEIF or AVIF image";
        }

        return "not a PNG, JPEG or WebP image";
    }

    /// <summary>
    /// Writes the image as an RGBA PNG, as fast as it can be (an intermediate file, read straight back
    /// by a texture encoder).
    /// </summary>
    public void SavePng(string path)
    {
        var png = new ArrayBufferWriter<byte>((this.Pixels.Length / 2) + 1024);
        PngCodec.Encode(png, this.Width, this.Height, this.Pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false, PngCompressionLevel.Fastest);
        File.WriteAllBytes(path, png.WrittenSpan);
    }

    /// <summary>
    /// Premultiplies the color channels by alpha in place, rounding to nearest:
    /// C' = (C * A + 127) / 255.  Fully transparent pixels become transparent black.
    /// </summary>
    public void PremultiplyAlpha()
    {
        Span<byte> pixels = this.Pixels;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a == 255)
            {
                continue;
            }
            if (a == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                continue;
            }
            pixels[i] = (byte)(((pixels[i] * a) + 127) / 255);
            pixels[i + 1] = (byte)(((pixels[i + 1] * a) + 127) / 255);
            pixels[i + 2] = (byte)(((pixels[i + 2] * a) + 127) / 255);
        }
    }
}
