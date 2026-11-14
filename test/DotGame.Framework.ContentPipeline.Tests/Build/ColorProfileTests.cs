// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DotGame.Framework.ContentPipeline.BuildTool.Graphics;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

public sealed class ColorProfileTests
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static void WriteChunk(MemoryStream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        png.Write(Encoding.ASCII.GetBytes(type));
        png.Write(data);

        // The check doesn't read the checksum
        png.Write(new byte[4]);
    }

    private static byte[] PngWithProfile(string chunkName, byte[] profile)
    {
        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(profile);
        }

        using var chunk = new MemoryStream();
        chunk.Write(Encoding.Latin1.GetBytes(chunkName));
        chunk.WriteByte(0);
        chunk.WriteByte(0);
        chunk.Write(compressed.ToArray());

        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", new byte[13]);
        WriteChunk(png, "iCCP", chunk.ToArray());
        WriteChunk(png, "IDAT", new byte[4]);
        return png.ToArray();
    }

    [Theory]
    [InlineData("sRGB IEC61966-2.1")]
    [InlineData("Photoshop ICC profile")]
    [InlineData("ICC Profile")]
    public void AnSrgbProfile_IsFine_WhateverItsChunkIsCalled(string chunkName)
    {
        Assert.False(RgbaImage.HasOtherColorProfile(PngWithProfile(chunkName, Encoding.ASCII.GetBytes("...desc sRGB IEC61966-2.1..."))));
    }

    [Fact]
    public void AVersion4SrgbProfile_IsFine()
    {
        Assert.False(RgbaImage.HasOtherColorProfile(PngWithProfile("ICC Profile", Encoding.BigEndianUnicode.GetBytes("mluc sRGB"))));
    }

    [Theory]
    [InlineData("Adobe RGB (1998)", "Adobe RGB (1998)")]
    [InlineData("ICC Profile", "Display P3")]
    public void AnotherProfile_IsReported(string chunkName, string description)
    {
        Assert.True(RgbaImage.HasOtherColorProfile(PngWithProfile(chunkName, Encoding.ASCII.GetBytes(description))));
    }

    [Fact]
    public void NoProfile_OrAnUnreadableOne_OrNotAPng_IsFine()
    {
        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", new byte[13]);
        WriteChunk(png, "IDAT", new byte[4]);
        Assert.False(RgbaImage.HasOtherColorProfile(png.ToArray()));

        using var unreadable = new MemoryStream();
        unreadable.Write(Signature);
        WriteChunk(unreadable, "iCCP", [(byte)'x', 0, 0, 1, 2, 3, 4]);
        Assert.False(RgbaImage.HasOtherColorProfile(unreadable.ToArray()));

        Assert.False(RgbaImage.HasOtherColorProfile([1, 2, 3]));
    }
}
