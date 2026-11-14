// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Security.Cryptography;
using DotGame.Framework.Android.Graphics;
using Xunit;

namespace DotGame.Framework.Tests.Graphics;

/// <summary>
/// Android's ASTC decoder against Arm's: each expected hash is the SHA-256 of astcenc 5.7.0's
/// own decode of the same blocks (<c>astcenc -dl</c>, as an 8-bit RGBA image). The real encodes
/// are of three procedural 61 x 45 images, a size that isn't a multiple of the blocks: a shape
/// with alpha (antialiased edges, a translucent shadow), flat art, and a painterly noise field;
/// the random blocks reach every block mode, endpoint mode, partition count, void extent and
/// error case.
/// </summary>
public sealed class AstcDecoderTests
{
    // .astc files astcenc encoded (-cl, -thorough), and the hashes of its decodes of them
    public static readonly TheoryData<string, string> Encodes = new()
    {
        { "shape_4x4.astc", "857c7688db289be0da8ce3a55aa378bf79bbce558c92bfd966bcd22d5daca017" },
        { "shape_6x6.astc", "eed406248cf28ac94aa735a93c717310abcb050fa92cf29fcc00c604b6e14e23" },
        { "shape_8x8.astc", "70d1a6d536d1865fb1a6e0078685420c22ce7d0bcbeb83f75d9556105e762d34" },
        { "flat_4x4.astc", "e20e9f21eb15c95f5e9b8a2566167f2a2dac3a90b10e8ca383e6d2163587fcda" },
        { "flat_6x6.astc", "10f9ea5bf887b480836da32677ce2baacb289ebcbb4c421bcf0aabefe1b55e1e" },
        { "flat_8x8.astc", "b3c271f60e3aca7221f4f4436ee1b1de19a31979247e34191950e59b4037a1fd" },
        { "painting_4x4.astc", "8cff5320aa175d9776e2390e1c3b61aba173249aec54c6fdc8d42ef3af018f23" },
        { "painting_6x6.astc", "1f95dfbbde38cde7d11ed0067c6a65de0c0ad70abaf18f5045aa8eb96afd4c88" },
        { "painting_8x8.astc", "2e091e1ee72abc91da0551446f272a5ec3b608645c90c1a0fc5f0dde4921a74f" },
    };

    // For random blocks of each block size: the hash of astcenc's decode of 64 x 64 of them
    public static readonly TheoryData<int, string> RandomBlocks = new()
    {
        { 4, "3084a364ca4b82f5235f3c21b003aeb10a3bf2a7ebcbb698da29f957665979be" },
        { 5, "ed3365b90642aaf9ad00b9bbdba7d59a244d611629125e90aa472d4ec5b587bc" },
        { 6, "8552420a512fd0b7a3ea06a6749441954b2ee3eefa66bed4bf358542c9016c14" },
        { 8, "4ac64c7e3575025c31f6f2214927f0bec3124c54a0f53a27565446b043fdc25f" },
        { 10, "6572ae841765a9205c91fd42f0024dc589e26d7f7019c9f2601a8211e47e4a26" },
        { 12, "84f428fa91366b7ddb0e399a19689f33e3b97cc289ab3bff2007bf8493ebec76" },
    };

    [Theory]
    [MemberData(nameof(Encodes))]
    public void Decode_MatchesAstcencOnRealEncodes(
        string fileName,
        string expectedHash)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Astc", fileName));

        // The .astc header: magic, the block's three dimensions, the image's three (24 bits each)
        int block = file[4];
        int width = file[7] | (file[8] << 8) | (file[9] << 16);
        int height = file[10] | (file[11] << 8) | (file[12] << 16);

        byte[] pixels = AstcDecoder.Decode(file.AsSpan(16), width, height, block);

        Assert.Equal(width * height * 4, pixels.Length);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(pixels)));
    }

    [Theory]
    [MemberData(nameof(RandomBlocks))]
    public void Decode_MatchesAstcencOnRandomBlocks(
        int blockSize,
        string expectedHash)
    {
        byte[] blocks = RandomBytes(0x9E3779B97F4A7C15UL ^ (ulong)blockSize, 64 * 64 * 16);

        byte[] pixels = AstcDecoder.Decode(blocks, 64 * blockSize, 64 * blockSize, blockSize);

        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(pixels)));
    }

    [Fact]
    public void Decode_ClipsEdgeBlocksToTheImage()
    {
        // A 5 x 3 image of 4 x 4 blocks: two blocks, the second mostly outside it
        byte[] blocks = RandomBytes(42, 2 * 16);

        byte[] pixels = AstcDecoder.Decode(blocks, 5, 3, 4);
        var whole = new byte[4 * 4 * 4];
        AstcDecoder.DecodeBlock(blocks.AsSpan(0, 16), 4, 4, whole);

        Assert.Equal(5 * 3 * 4, pixels.Length);
        Assert.Equal(whole.AsSpan(0, 16).ToArray(), pixels.AsSpan(0, 16).ToArray());
    }

    [Fact]
    public void Decode_RejectsTooFewBlocks()
    {
        Assert.Throws<ArgumentException>(() => AstcDecoder.Decode(new byte[16], 8, 4, 4));
    }

    // xorshift64*, as the script that made the expected hashes generated them
    private static byte[] RandomBytes(
        ulong seed,
        int count)
    {
        var bytes = new byte[count];
        ulong x = seed;
        for (int i = 0; i < count; i += 8)
        {
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            BitConverter.TryWriteBytes(bytes.AsSpan(i), x * 0x2545F4914F6CDD1DUL);
        }

        return bytes;
    }
}
