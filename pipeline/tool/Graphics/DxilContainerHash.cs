// Ported from DxilHash.cpp in the DirectX Shader Compiler (lib/DxilHash/DxilHash.cpp):
//
//   Copyright (C) Microsoft Corporation. All rights reserved.
//   This file is distributed under the University of Illinois Open Source
//   License. See LICENSE.TXT for details.
//
// The University of Illinois/NCSA Open Source License's text is in THIRD-PARTY-NOTICES.md.
// The port (C#, the rounds as a loop) is Copyright (c) 2026 Hot Chai Games LLC, under the same license.

using System.Buffers.Binary;
using System.Numerics;

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// Signs a DXBC container (DXIL) with the hash D3D12 checks before it accepts a
/// shader. dxc writes the hash after its validator passes; Mesa's spirv2dxil
/// leaves it zero, so the content pipeline writes it.
/// </summary>
/// <remarks>
/// The container's header holds the magic <c>DXBC</c>, the 16-byte hash at offset 4,
/// the version at 16 and the container's size at 24. The hash covers bytes 20 to the
/// size. It's dxc's <c>ComputeHashRetail</c>: MD5's rounds over a differently padded
/// final block, which isn't MD5, so <see cref="System.Security.Cryptography.MD5"/>
/// can't compute it. Not for security.
/// </remarks>
internal static class DxilContainerHash
{
    private const int HashOffset = 4;
    private const int HashSize = 16;
    private const int HashedStart = 20;
    private const int SizeOffset = 24;
    private const int HeaderSize = 32;

    /// <summary>Writes the hash into the DXBC container at <paramref name="path"/>.</summary>
    public static void Sign(string path)
    {
        byte[] container = File.ReadAllBytes(path);
        Sign(container, path);
        File.WriteAllBytes(path, container);
    }

    /// <summary>Writes the hash into <paramref name="container"/>, in place.</summary>
    internal static void Sign(byte[] container, string name)
    {
        if (container.Length < HeaderSize || !container.AsSpan(0, 4).SequenceEqual("DXBC"u8))
        {
            throw new ContentBuildException($"'{name}' isn't a DXIL container (no DXBC header).");
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(SizeOffset));
        if (size != container.Length)
        {
            throw new ContentBuildException($"'{name}' is {container.Length} bytes, but its DXBC header says {size}.");
        }

        ComputeHashRetail(container.AsSpan(HashedStart, (int)size - HashedStart), container.AsSpan(HashOffset, HashSize));
    }

    // MD5's per-round shifts and sine constants
    private static ReadOnlySpan<byte> Shifts =>
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    private static ReadOnlySpan<uint> Constants =>
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    /// <summary>dxc's <c>ComputeHashRetail</c> of <paramref name="data"/>, into 16 bytes.</summary>
    internal static void ComputeHashRetail(ReadOnlySpan<byte> data, Span<byte> hash)
    {
        uint byteCount = (uint)data.Length;
        int leftOver = data.Length & 0x3f;
        Span<uint> state = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476];
        Span<byte> block = stackalloc byte[64];

        // Whole blocks of the data
        int offset = 0;
        for (; offset + 64 <= data.Length; offset += 64)
        {
            Transform(state, data.Slice(offset, 64));
        }

        // The rest, padded with 0x80 and zeros. The final block starts with the bit count and
        // ends with 1 | (byte count << 1), where MD5 ends with the bit count; when the rest
        // leaves no room for both (56 bytes or more), they go in a block of their own.
        ReadOnlySpan<byte> rest = data[offset..];
        block.Clear();
        if (leftOver < 56)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block, byteCount << 3);
            rest.CopyTo(block[4..]);
            block[4 + leftOver] = 0x80;
        }
        else
        {
            rest.CopyTo(block);
            block[leftOver] = 0x80;
            Transform(state, block);
            block.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(block, byteCount << 3);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(block[60..], 1 | (byteCount << 1));
        Transform(state, block);

        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(hash[(i * 4)..], state[i]);
        }
    }

    // MD5's compression function, the 64 steps of DxilHash.cpp's FF, GG, HH and II as a loop
    private static void Transform(Span<uint> state, ReadOnlySpan<byte> block)
    {
        Span<uint> x = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
        {
            x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);
        }

        uint a = state[0], b = state[1], c = state[2], d = state[3];
        for (int step = 0; step < 64; step++)
        {
            uint f;
            int index;
            switch (step >> 4)
            {
                case 0:
                    f = (b & c) | (~b & d);
                    index = step;
                    break;
                case 1:
                    f = (b & d) | (c & ~d);
                    index = ((5 * step) + 1) & 15;
                    break;
                case 2:
                    f = b ^ c ^ d;
                    index = ((3 * step) + 5) & 15;
                    break;
                default:
                    f = c ^ (b | ~d);
                    index = (7 * step) & 15;
                    break;
            }

            uint rotated = BitOperations.RotateLeft(a + f + x[index] + Constants[step], Shifts[step]) + b;
            a = d;
            d = c;
            c = b;
            b = rotated;
        }

        state[0] += a;
        state[1] += b;
        state[2] += c;
        state[3] += d;
    }
}
