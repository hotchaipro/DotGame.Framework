// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO.Compression;
using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.BuildTool;
using DotGame.Framework.ContentPipeline.BuildTool.Graphics;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

/// <summary>
/// Premultiplication, and the PNGs that carry images between the pipeline's steps.
/// </summary>
public sealed class RgbaImageTests
{
    [Fact]
    public void PremultiplyAlpha_RoundsToNearest_ForEveryColorAndAlpha()
    {
        var image = new RgbaImage(256, 256);
        for (int a = 0; a < 256; a++)
        {
            for (int c = 0; c < 256; c++)
            {
                int at = ((a * 256) + c) * 4;
                image.Pixels[at] = (byte)c;
                image.Pixels[at + 1] = (byte)(255 - c);
                image.Pixels[at + 2] = (byte)c;
                image.Pixels[at + 3] = (byte)a;
            }
        }

        image.PremultiplyAlpha();

        for (int a = 0; a < 256; a++)
        {
            for (int c = 0; c < 256; c++)
            {
                int at = ((a * 256) + c) * 4;
                Assert.Equal(Expected(c, a), image.Pixels[at]);
                Assert.Equal(Expected(255 - c, a), image.Pixels[at + 1]);
                Assert.Equal(Expected(c, a), image.Pixels[at + 2]);
                Assert.Equal(a, image.Pixels[at + 3]);
            }
        }

        static byte Expected(int color, int alpha) => (byte)Math.Round(color * alpha / 255.0, MidpointRounding.AwayFromZero);
    }

    [Fact]
    public void PremultiplyAlpha_NeverMakesColorExceedAlpha()
    {
        var image = new RgbaImage(256, 1);
        for (int a = 0; a < 256; a++)
        {
            image.Pixels[(a * 4) + 0] = 255;
            image.Pixels[(a * 4) + 1] = 255;
            image.Pixels[(a * 4) + 2] = 255;
            image.Pixels[(a * 4) + 3] = (byte)a;
        }

        image.PremultiplyAlpha();

        for (int a = 0; a < 256; a++)
        {
            Assert.Equal(a, image.Pixels[a * 4]);
        }
    }

    [Fact]
    public void PremultiplyAlpha_MakesTransparentPixelsBlack()
    {
        var image = new RgbaImage(1, 1, [200, 100, 50, 0]);

        image.PremultiplyAlpha();

        Assert.Equal([0, 0, 0, 0], image.Pixels);
    }

    [Fact]
    public void PremultiplyAlpha_LeavesOpaquePixelsAlone()
    {
        var image = new RgbaImage(1, 1, [200, 100, 50, 255]);

        image.PremultiplyAlpha();

        Assert.Equal([200, 100, 50, 255], image.Pixels);
    }

    [Fact]
    public void Constructor_RejectsABadSizeOrBuffer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbaImage(0, 1, []));
        Assert.Throws<ArgumentException>(() => new RgbaImage(2, 2, new byte[15]));
    }

    [Fact]
    public void SavePngThenLoad_KeepsEveryPixel_EvenWhereAlphaIsZero()
    {
        using var directory = new TestDirectory();
        string path = directory.Path_("image.png");
        RgbaImage image = Images.Sample();
        image.Pixels[0] = 77;
        image.Pixels[3] = 0;

        image.SavePng(path);
        RgbaImage loaded = RgbaImage.Load(path);

        Assert.Equal(image.Width, loaded.Width);
        Assert.Equal(image.Height, loaded.Height);
        Assert.Equal(image.Pixels, loaded.Pixels);
    }

    // A 37x21 JPEG at 4:1:1, made by libjpeg-turbo's cjpeg
    private const string Jpeg37x21 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAAVACUDAUEAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDxeno2DU6kMMinCvnJH7RQfQUUUUVB0lKinqxU5FTowYcVpLY4qTtIdRRRWZ2lOigU5WKnIrZnnJ2ZZByAaKKKxPQP/9k=";

    [Fact]
    public void Load_ReadsAJpeg()
    {
        using var directory = new TestDirectory();
        string path = directory.Path_("photo.jpg");
        File.WriteAllBytes(path, Convert.FromBase64String(Jpeg37x21));

        RgbaImage loaded = RgbaImage.Load(path);

        Assert.Equal(37, loaded.Width);
        Assert.Equal(21, loaded.Height);
    }

    [Fact]
    public void Load_NamesAFormatItDoesntRead()
    {
        using var directory = new TestDirectory();
        string path = directory.Path_("animation.gif");
        File.WriteAllBytes(path, [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0, 1, 0, 0, 0, 0]);

        var error = Assert.Throws<ContentBuildException>(() => RgbaImage.Load(path));

        Assert.Contains("a GIF", error.Message, StringComparison.Ordinal);
        Assert.Contains("PNG, JPEG or WebP", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ReadsAWebP()
    {
        // A 3 x 2 lossless WebP, as an artist's tool would write one
        byte[] rgba = new byte[3 * 2 * 4];
        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)((i * 37) | 3);
        }

        var webp = new System.Buffers.ArrayBufferWriter<byte>();
        DotGame.Imaging.WebP.WebPCodec.Encode(webp, 3, 2, rgba, DotGame.Imaging.PixelLayout.Rgba32, unpremultiplyAlpha: false, new DotGame.Imaging.WebP.WebPEncoderOptions { Lossless = true, Exact = true });
        using var directory = new TestDirectory();
        string path = directory.Path_("art.webp");
        File.WriteAllBytes(path, webp.WrittenSpan.ToArray());

        RgbaImage loaded = RgbaImage.Load(path);

        Assert.Equal(3, loaded.Width);
        Assert.Equal(2, loaded.Height);
        Assert.Equal(rgba, loaded.Pixels);
    }

    [Fact]
    public void Load_ReadsAPalettePng()
    {
        using var directory = new TestDirectory();
        string path = directory.Path_("palette.png");
        File.WriteAllBytes(path, PalettePng());

        RgbaImage loaded = RgbaImage.Load(path);

        Assert.Equal([255, 0, 0, 255, 0, 0, 255, 255], loaded.Pixels);
    }

    [Fact]
    public void Load_ReportsADamagedImageAsABuildError()
    {
        using var directory = new TestDirectory();

        var error = Assert.Throws<ContentBuildException>(() => RgbaImage.Load(directory.WriteFile("bad.png", "not an image")));

        Assert.Contains("bad.png", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SavePng_WritesValidChunkChecksums()
    {
        using var directory = new TestDirectory();
        string path = directory.Path_("image.png");
        Images.Sample().SavePng(path);

        byte[] png = File.ReadAllBytes(path);
        int at = 8;
        var types = new List<string>();
        while (at < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            types.Add(System.Text.Encoding.ASCII.GetString(png, at + 4, 4));
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            Assert.Equal(Crc32(png.AsSpan(at + 4, length + 4)), stored);
            at += 12 + length;
        }

        Assert.Equal(["IHDR", "IDAT", "IEND"], types);
    }

    // A 2 x 1 PNG of palette indices 0 and 1: red and blue
    private static byte[] PalettePng()
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write([0, 0, 1]);
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", [0, 0, 0, 2, 0, 0, 0, 1, 8, 3, 0, 0, 0]);
        WriteChunk(png, "PLTE", [255, 0, 0, 0, 0, 255]);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var chunk = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, chunk);
        data.CopyTo(chunk, 4);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        stream.Write(chunk);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(chunk));
        stream.Write(number);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
