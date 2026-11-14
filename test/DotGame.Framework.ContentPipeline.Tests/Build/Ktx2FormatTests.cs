// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;
using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.BuildTool;
using DotGame.Framework.ContentPipeline.BuildTool.Graphics;
using DotGame.Framework.Platform.Common.Content;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

/// <summary>
/// The writer against the platforms' reader, whose subset of KTX2 it has to stay within, and against
/// the KTX 2.0 specification's layout.
/// </summary>
public sealed class Ktx2FormatTests
{
    private const int LevelIndexOffset = 80;

    private static byte[] WriteRgba8(RgbaImage image, bool premultiplied)
    {
        using var directory = new TestDirectory();
        string path = directory.Path_("t.ktx2");
        Ktx2Format.WriteRgba8(path, image, premultiplied);
        return File.ReadAllBytes(path);
    }

    private static byte[] WriteAstc(int width, int height, int block, bool premultiplied = true)
    {
        using var directory = new TestDirectory();
        string path = directory.Path_("t.ktx2");
        int blocks = ((width + block - 1) / block) * ((height + block - 1) / block);
        var data = new byte[blocks * 16];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 7);
        }

        Ktx2Format.WriteAstc(path, width, height, block, data, premultiplied);
        return File.ReadAllBytes(path);
    }

    private static uint Word(byte[] file, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rgba8_ReadsBackExactly(bool premultiplied)
    {
        RgbaImage image = Images.Sample();

        Ktx2Texture texture = Ktx2Texture.Read(WriteRgba8(image, premultiplied), "t");

        Assert.Equal(Ktx2PixelFormat.Rgba8, texture.Format);
        Assert.Equal(new Vector2Int(image.Width, image.Height), texture.Size);
        Assert.Equal(premultiplied, texture.Premultiplied);
        Assert.Equal(image.Pixels, texture.Data);
    }

    [Fact]
    public void Rgba8_OfOnePixel_ReadsBack()
    {
        var image = new RgbaImage(1, 1, [1, 2, 3, 4]);

        Assert.Equal(image.Pixels, Ktx2Texture.Read(WriteRgba8(image, true), "t").Data);
    }

    [Theory]
    [InlineData(4, 16, 8)]
    [InlineData(6, 16, 8)]
    [InlineData(8, 16, 8)]
    [InlineData(4, 61, 45)]
    [InlineData(6, 61, 45)]
    public void Astc_ReadsBackItsBlocks(int block, int width, int height)
    {
        Ktx2PixelFormat format = block switch
        {
            4 => Ktx2PixelFormat.Astc4x4,
            6 => Ktx2PixelFormat.Astc6x6,
            _ => Ktx2PixelFormat.Astc8x8,
        };
        byte[] file = WriteAstc(width, height, block);

        Ktx2Texture texture = Ktx2Texture.Read(file, "t");

        Assert.Equal(format, texture.Format);
        Assert.Equal(block, texture.BlockSize);
        Assert.Equal(new Vector2Int(width, height), texture.Size);
        Assert.True(texture.Premultiplied);
        Assert.Equal(((width + block - 1) / block) * ((height + block - 1) / block) * 16, texture.Data.Length);
        Assert.Equal((byte)7, texture.Data[1]);
    }

    [Fact]
    public void Astc_RejectsAnUnsupportedBlockSize()
    {
        using var directory = new TestDirectory();

        Assert.Throws<ContentBuildException>(() => Ktx2Format.WriteAstc(directory.Path_("t.ktx2"), 5, 5, 5, new byte[16], true));
    }

    [Fact]
    public void Astc_RejectsTheWrongAmountOfData()
    {
        using var directory = new TestDirectory();

        Assert.Throws<ContentBuildException>(() => Ktx2Format.WriteAstc(directory.Path_("t.ktx2"), 8, 8, 4, new byte[16], true));
    }

    [Fact]
    public void Astc_AlignsItsLevelTo16Bytes()
    {
        byte[] file = WriteAstc(8, 8, 4);

        ulong levelOffset = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(LevelIndexOffset));

        Assert.Equal(0ul, levelOffset % 16);
    }

    [Fact]
    public void Rgba8_FollowsTheSpecificationsLayout()
    {
        RgbaImage image = Images.Sample();
        byte[] file = WriteRgba8(image, true);

        Assert.Equal(Ktx2Format.Identifier.ToArray(), file[..12]);
        Assert.Equal(Ktx2Format.VkFormatR8G8B8A8Unorm, Word(file, 12));
        Assert.Equal(1u, Word(file, 16));
        Assert.Equal((uint)image.Width, Word(file, 20));
        Assert.Equal((uint)image.Height, Word(file, 24));
        Assert.Equal(0u, Word(file, 28));
        Assert.Equal(0u, Word(file, 32));
        Assert.Equal(1u, Word(file, 36));
        Assert.Equal(1u, Word(file, 40));
        Assert.Equal(Ktx2Format.SupercompressionZlib, Word(file, 44));

        uint dfdOffset = Word(file, 48);
        uint dfdLength = Word(file, 52);
        uint kvdOffset = Word(file, 56);
        uint kvdLength = Word(file, 60);
        Assert.Equal(104u, dfdOffset);
        Assert.Equal(dfdOffset + dfdLength, kvdOffset);
        Assert.Equal(0u, kvdOffset % 4);
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(64)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(72)));

        ulong levelOffset = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(LevelIndexOffset));
        ulong levelLength = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(LevelIndexOffset + 8));
        ulong uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(LevelIndexOffset + 16));
        Assert.True(levelOffset >= kvdOffset + kvdLength);
        Assert.Equal((ulong)file.Length, levelOffset + levelLength);
        Assert.Equal((ulong)(image.Width * image.Height * 4), uncompressed);

        // dfdTotalSize, then the basic block: its size in the top half of its third word
        Assert.Equal(dfdLength, Word(file, (int)dfdOffset));
        Assert.Equal(dfdLength - 4, Word(file, (int)dfdOffset + 8) >> 16);
    }

    [Fact]
    public void Rgba8_NamesItsWriterInTheKeyValueData()
    {
        byte[] file = WriteRgba8(Images.Sample(), true);
        int kvdOffset = (int)Word(file, 56);
        int entryLength = (int)Word(file, kvdOffset);

        string entry = System.Text.Encoding.UTF8.GetString(file, kvdOffset + 4, entryLength);

        Assert.StartsWith("KTXwriter\0", entry, StringComparison.Ordinal);
        Assert.EndsWith("\0", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void Rgba8_IsTheSameBytesEveryTime()
    {
        RgbaImage image = Images.Sample();

        Assert.Equal(WriteRgba8(image, true), WriteRgba8(image, true));
    }

    [Fact]
    public void Reader_RefusesATruncatedFile()
    {
        byte[] file = WriteRgba8(Images.Sample(), true);

        Assert.Throws<InvalidDataException>(() => Ktx2Texture.Read(file[..(file.Length - 4)], "t"));
        Assert.Throws<InvalidDataException>(() => Ktx2Texture.Read(file[..20], "t"));
    }
}
