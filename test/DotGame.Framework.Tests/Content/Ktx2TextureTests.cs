// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Security.Cryptography;
using DotGame.Framework.Android.Graphics;
using DotGame.Framework.Platform.Common.Content;
using Xunit;

namespace DotGame.Framework.Tests.Content;

/// <summary>
/// The KTX2 reader the platforms' texture loaders share, on files the content pipeline wrote
/// (<c>texture --format ktx2</c>) from a procedural 61 x 45 image with alpha (<c>TestData/Astc</c>'s shape).
/// </summary>
public sealed class Ktx2TextureTests
{
    // The image's pixels, premultiplied as the pipeline premultiplies them
    private const string PremultipliedCropHash = "f479cbde935e026c84c8d42863951e8d4c9562eb71a0612baeb20e93ce5a4c1f";

    [Fact]
    public void Read_InflatesRgba8()
    {
        var texture = Ktx2Texture.Read(ReadFixture("shape_rgba8.ktx2"), "shape_rgba8.ktx2");

        Assert.Equal(Ktx2PixelFormat.Rgba8, texture.Format);
        Assert.Equal(new Vector2Int(61, 45), texture.Size);
        Assert.True(texture.Premultiplied);
        Assert.Equal(1, texture.BlockSize);
        Assert.Equal(PremultipliedCropHash, Convert.ToHexStringLower(SHA256.HashData(texture.Data)));
    }

    [Fact]
    public void Read_KeepsAstcBlocksAsTheyAre()
    {
        var texture = Ktx2Texture.Read(ReadFixture("shape_astc6x6.ktx2"), "shape_astc6x6.ktx2");

        Assert.Equal(Ktx2PixelFormat.Astc6x6, texture.Format);
        Assert.Equal(new Vector2Int(61, 45), texture.Size);
        Assert.True(texture.Premultiplied);
        Assert.Equal(6, texture.BlockSize);

        // 11 x 8 blocks of 16 bytes, which decode to an image the size of the crop
        Assert.Equal(11 * 8 * 16, texture.Data.Length);
        Assert.Equal(61 * 45 * 4, AstcDecoder.Decode(texture.Data, 61, 45, 6).Length);
    }

    [Fact]
    public void Read_ReadsAStream()
    {
        using var stream = new MemoryStream(ReadFixture("shape_rgba8.ktx2"));

        var texture = Ktx2Texture.Read(stream, "shape_rgba8.ktx2");

        Assert.Equal(PremultipliedCropHash, Convert.ToHexStringLower(SHA256.HashData(texture.Data)));
    }

    [Fact]
    public void IsKtx2_KnowsTheIdentifier()
    {
        Assert.True(Ktx2Texture.IsKtx2(ReadFixture("shape_rgba8.ktx2")));
        Assert.False(Ktx2Texture.IsKtx2("RIFF\0\0\0\0WEBPVP8L"u8));
    }

    [Fact]
    public void Read_RejectsOtherFiles()
    {
        Assert.Throws<InvalidDataException>(() => Ktx2Texture.Read("RIFF\0\0\0\0WEBPVP8L"u8.ToArray(), "a WebP"));
    }

    [Fact]
    public void Read_RejectsAFormatItCantRead()
    {
        byte[] file = ReadFixture("shape_rgba8.ktx2");
        BitConverter.TryWriteBytes(file.AsSpan(12), 145u); // VK_FORMAT_BC7_UNORM_BLOCK

        Assert.Throws<InvalidDataException>(() => Ktx2Texture.Read(file, "bc7"));
    }

    [Fact]
    public void Read_RejectsALevelPastTheEnd()
    {
        byte[] file = ReadFixture("shape_astc6x6.ktx2");

        Assert.Throws<InvalidDataException>(() => Ktx2Texture.Read(file.AsSpan(0, file.Length - 16).ToArray(), "cut short"));
    }

    private static byte[] ReadFixture(
        string name)
    {
        return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Ktx2", name));
    }
}
