// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Framework.Android.Graphics;

/// <summary>
/// Decodes ASTC blocks to RGBA8 pixels on the CPU, for Android GPUs that can't sample ASTC
/// (<c>GL_KHR_texture_compression_astc_ldr</c>). It follows the LDR profile of the Khronos Data
/// Format Specification's ASTC chapter for 2D blocks: integer sequence encoding, every block
/// mode, dual planes, one to four partitions, the LDR color endpoint modes, weight infill and
/// void-extent blocks. HDR endpoints and invalid blocks decode to the LDR error color, magenta.
/// Each 8-bit value is the high byte of the 16-bit interpolated value, as Arm's astcenc writes
/// an 8-bit image (<c>astcenc -dl</c>), which the tests compare against.
/// </summary>
/// <remarks>
/// Pure C#, with no Android APIs, so the tests run it on the desktop. It's used only where the
/// GPU can't sample ASTC, which few Android devices with OpenGL ES 3 can't.
/// </remarks>
internal static class AstcDecoder
{
    private const int MaxWeights = 64;
    private const int MaxTexels = 144;

    // The ranges of the integer sequence encoding, in order: levels, and how each value is
    // stored (bits, and a trit or quint)
    private static readonly int[] RangeLevels = [2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256];
    private static readonly int[] RangeBits = [1, 0, 2, 0, 1, 3, 1, 2, 4, 2, 3, 5, 3, 4, 6, 4, 5, 7, 5, 6, 8];
    private static readonly int[] RangeTrits = [0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0];
    private static readonly int[] RangeQuints = [0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0];

    // The weight ranges a block mode selects: (R - 2) + (H ? 6 : 0) indexes this list
    private static readonly int[] WeightRanges = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[][] ColorUnquantize = CreateColorUnquantize();
    private static readonly byte[][] WeightUnquantize = CreateWeightUnquantize();

    private static readonly byte[] TritsOf = CreateTritTable();
    private static readonly byte[] QuintsOf = CreateQuintTable();

    /// <summary>
    /// Decodes a <paramref name="width"/> by <paramref name="height"/> image of square
    /// <paramref name="blockSize"/> blocks (16 bytes each, rows of blocks, top first) into RGBA8
    /// pixels, top row first.
    /// </summary>
    public static byte[] Decode(
        ReadOnlySpan<byte> blocks,
        int width,
        int height,
        int blockSize)
    {
        if ((blockSize < 4) || (blockSize > 12) || (width <= 0) || (height <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Can't decode {blockSize} x {blockSize} blocks of a {width} x {height} image.");
        }

        int blocksWide = (width + blockSize - 1) / blockSize;
        int blocksHigh = (height + blockSize - 1) / blockSize;
        if (blocks.Length < blocksWide * blocksHigh * 16)
        {
            throw new ArgumentException($"A {width} x {height} image of {blockSize} x {blockSize} blocks needs {blocksWide * blocksHigh * 16} bytes, not {blocks.Length}.", nameof(blocks));
        }

        var pixels = new byte[width * height * 4];
        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                // Into the image: the texels inside it (edge blocks overhang it)
                int rows = Math.Min(blockSize, height - (by * blockSize));
                int columns = Math.Min(blockSize, width - (bx * blockSize));
                var target = new Target(pixels.AsSpan((((by * blockSize) * width) + (bx * blockSize)) * 4), width * 4, rows, columns);
                DecodeBlock(blocks.Slice(((by * blocksWide) + bx) * 16, 16), blockSize, blockSize, target);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Decodes one block into <paramref name="texels"/>: RGBA8, a row of <paramref name="blockWidth"/>
    /// texels at a time.
    /// </summary>
    internal static void DecodeBlock(
        ReadOnlySpan<byte> block,
        int blockWidth,
        int blockHeight,
        Span<byte> texels)
    {
        DecodeBlock(block, blockWidth, blockHeight, new Target(texels, blockWidth * 4, blockHeight, blockWidth));
    }

    // Where a block's texels go: rows of a stride's bytes, of which the first rows and columns are
    // written (the rest of an edge block is outside the image)
    private readonly ref struct Target(
        Span<byte> bytes,
        int stride,
        int rows,
        int columns)
    {
        public readonly Span<byte> Bytes = bytes;
        public readonly int Stride = stride;
        public readonly int Rows = rows;
        public readonly int Columns = columns;
    }

    private static void DecodeBlock(
        ReadOnlySpan<byte> block,
        int blockWidth,
        int blockHeight,
        Target texels)
    {
        int texelCount = blockWidth * blockHeight;
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(block);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(8));
        var bits = new BlockBits(low, high);

        // A void-extent block: one color for the whole block
        if ((bits.Read(0, 9)) == 0x1FC)
        {
            DecodeVoidExtent(bits, texels);
            return;
        }

        if (!DecodeBlockMode((int)bits.Read(0, 11), out int gridWidth, out int gridHeight, out int weightRange, out bool dualPlane)
            || (gridWidth > blockWidth) || (gridHeight > blockHeight))
        {
            Fill(texels, ErrorColor);
            return;
        }

        int partitionCount = (int)bits.Read(11, 2) + 1;
        int weightCount = gridWidth * gridHeight * (dualPlane ? 2 : 1);
        int weightBits = SequenceBits(weightRange, weightCount);
        if ((weightCount > MaxWeights) || (weightBits < 24) || (weightBits > 96) || ((partitionCount == 4) && dualPlane))
        {
            Fill(texels, ErrorColor);
            return;
        }

        // Each partition's color endpoint mode
        Span<int> modes = stackalloc int[4];
        int belowWeights = 128 - weightBits;
        int colorStart;
        int partitionIndex = 0;
        if (partitionCount == 1)
        {
            modes[0] = (int)bits.Read(13, 4);
            colorStart = 17;
        }
        else
        {
            partitionIndex = (int)bits.Read(13, 10);
            int encoded = (int)bits.Read(23, 6);
            colorStart = 29;
            if ((encoded & 3) == 0)
            {
                // Every partition has the same mode
                for (int i = 0; i < partitionCount; i++)
                {
                    modes[i] = encoded >> 2;
                }
            }
            else
            {
                // A base class and, for each partition, a class step (C) and a mode within the
                // class (M); the bits past the first six are stored below the weights
                int extraBits = (3 * partitionCount) - 4;
                belowWeights -= extraBits;
                encoded |= (int)bits.Read(belowWeights, extraBits) << 6;
                int baseClass = (encoded & 3) - 1;
                for (int i = 0; i < partitionCount; i++)
                {
                    int step = (encoded >> (2 + i)) & 1;
                    int mode = (encoded >> (2 + partitionCount + (2 * i))) & 3;
                    modes[i] = ((baseClass + step) << 2) | mode;
                }
            }
        }

        // The color component the second plane of weights applies to
        int planeComponent = -1;
        if (dualPlane)
        {
            belowWeights -= 2;
            planeComponent = (int)bits.Read(belowWeights, 2);
        }

        // The color endpoints: as many values as the modes need, in the largest range that fits
        int colorValueCount = 0;
        for (int i = 0; i < partitionCount; i++)
        {
            colorValueCount += ((modes[i] >> 2) + 1) * 2;
        }

        int colorBits = belowWeights - colorStart;
        if ((colorValueCount > 18) || (colorBits < 0))
        {
            Fill(texels, ErrorColor);
            return;
        }

        int colorRange = -1;
        for (int range = RangeLevels.Length - 1; range >= 0; range--)
        {
            if (SequenceBits(range, colorValueCount) <= colorBits)
            {
                colorRange = range;
                break;
            }
        }

        // The smallest range color endpoints may use has 6 levels
        if (colorRange < 4)
        {
            Fill(texels, ErrorColor);
            return;
        }

        Span<int> colorValues = stackalloc int[18];
        DecodeSequence(bits, colorStart, colorRange, colorValueCount, colorValues);
        var colorTable = ColorUnquantize[colorRange];
        for (int i = 0; i < colorValueCount; i++)
        {
            colorValues[i] = colorTable[colorValues[i]];
        }

        // Each partition's endpoints, RGBA 0 to 255
        Span<int> endpoints = stackalloc int[4 * 8];
        int valueIndex = 0;
        for (int i = 0; i < partitionCount; i++)
        {
            if (!DecodeEndpoints(modes[i], colorValues.Slice(valueIndex), endpoints.Slice(i * 8, 8)))
            {
                // An HDR endpoint mode, which the LDR profile doesn't have: this partition's
                // texels are the error color (the others decode as usual)
                Set(endpoints, i * 8, ErrorColor[0], ErrorColor[1], ErrorColor[2], ErrorColor[3]);
                Set(endpoints, (i * 8) + 4, ErrorColor[0], ErrorColor[1], ErrorColor[2], ErrorColor[3]);
            }

            valueIndex += ((modes[i] >> 2) + 1) * 2;
        }

        // The weights, stored from the top of the block down, bit reversed
        var reversed = bits.Reversed();
        Span<int> gridWeights = stackalloc int[MaxWeights];
        DecodeSequence(reversed, 0, weightRange, weightCount, gridWeights);
        var weightTable = WeightUnquantize[weightRange];
        for (int i = 0; i < weightCount; i++)
        {
            gridWeights[i] = weightTable[gridWeights[i]];
        }

        // Each texel's weights, infilled from the grid, then its color
        bool smallBlock = texelCount < 31;
        int planes = dualPlane ? 2 : 1;
        int[] infill = Infill.For(blockWidth, blockHeight, gridWidth, gridHeight);
        ref int grid = ref MemoryMarshal.GetReference(gridWeights);
        ref int fill = ref MemoryMarshal.GetArrayDataReference(infill);

        // Each partition's endpoints expanded to 16 bits, RGBA in a vector's lanes; the lane the
        // second plane's weight applies to
        Span<Vector128<int>> first = stackalloc Vector128<int>[4];
        Span<Vector128<int>> second = stackalloc Vector128<int>[4];
        for (int p = 0; p < partitionCount; p++)
        {
            first[p] = Vector128.Create(endpoints[p * 8], endpoints[(p * 8) + 1], endpoints[(p * 8) + 2], endpoints[(p * 8) + 3]) * 257;
            second[p] = Vector128.Create(endpoints[(p * 8) + 4], endpoints[(p * 8) + 5], endpoints[(p * 8) + 6], endpoints[(p * 8) + 7]) * 257;
        }

        var secondPlane = Vector128.Equals(Vector128.Create(0, 1, 2, 3), Vector128.Create(planeComponent));
        var sixtyFour = Vector128.Create(64);
        var half = Vector128.Create(32);
        for (int t = 0; t < texels.Rows; t++)
        {
            Span<byte> row = texels.Bytes.Slice(t * texels.Stride, texels.Columns * 4);
            for (int s = 0; s < texels.Columns; s++)
            {
                // The four grid points around the texel, and their factors (summing to 16)
                ref int at = ref Unsafe.Add(ref fill, ((t * blockWidth) + s) * 8);
                int i0 = at * planes;
                int i1 = Unsafe.Add(ref at, 1) * planes;
                int i2 = Unsafe.Add(ref at, 2) * planes;
                int i3 = Unsafe.Add(ref at, 3) * planes;
                int f0 = Unsafe.Add(ref at, 4);
                int f1 = Unsafe.Add(ref at, 5);
                int f2 = Unsafe.Add(ref at, 6);
                int f3 = Unsafe.Add(ref at, 7);
                int w0 = (8 + (Unsafe.Add(ref grid, i0) * f0) + (Unsafe.Add(ref grid, i1) * f1) + (Unsafe.Add(ref grid, i2) * f2) + (Unsafe.Add(ref grid, i3) * f3)) >> 4;
                var w = Vector128.Create(w0);
                if (dualPlane)
                {
                    int w1 = (8 + (Unsafe.Add(ref grid, i0 + 1) * f0) + (Unsafe.Add(ref grid, i1 + 1) * f1) + (Unsafe.Add(ref grid, i2 + 1) * f2) + (Unsafe.Add(ref grid, i3 + 1) * f3)) >> 4;
                    w = Vector128.ConditionalSelect(secondPlane, Vector128.Create(w1), w);
                }

                int partition = (partitionCount == 1) ? 0 : SelectPartition(partitionIndex, s, t, partitionCount, smallBlock);

                // The endpoints interpolated in 16 bits, and the result's high byte, as astcenc
                // writes an 8-bit image: ((c0 * (64 - w) + c1 * w + 32) >> 6) >> 8
                var value = ((first[partition] * (sixtyFour - w)) + (second[partition] * w) + half) >> 14;
                var bytes = Vector128.Narrow(Vector128.Narrow(value.AsUInt32(), value.AsUInt32()), Vector128<ushort>.Zero);
                Unsafe.WriteUnaligned(ref row[s * 4], bytes.AsUInt32().ToScalar());
            }
        }
    }

    private static readonly byte[] ErrorColor = [0xFF, 0x00, 0xFF, 0xFF];

    private static void Fill(
        Target texels,
        ReadOnlySpan<byte> color)
    {
        uint pixel = BinaryPrimitives.ReadUInt32LittleEndian(color);
        for (int t = 0; t < texels.Rows; t++)
        {
            MemoryMarshal.Cast<byte, uint>(texels.Bytes.Slice(t * texels.Stride, texels.Columns * 4)).Fill(pixel);
        }
    }

    private static void DecodeVoidExtent(
        BlockBits bits,
        Target texels)
    {
        // Bit 9 marks an HDR color, which the LDR profile doesn't have; bits 10 and 11 are
        // reserved, and must be ones; the extent's coordinates, unless all ones, must each
        // start before they end
        bool hdr = bits.Read(9, 1) != 0;
        bool reserved = bits.Read(10, 2) != 3;
        uint sMin = (uint)bits.Read(12, 13);
        uint sMax = (uint)bits.Read(25, 13);
        uint tMin = (uint)bits.Read(38, 13);
        uint tMax = (uint)bits.Read(51, 13);
        bool allOnes = (sMin == 0x1FFF) && (sMax == 0x1FFF) && (tMin == 0x1FFF) && (tMax == 0x1FFF);
        if (hdr || reserved || (!allOnes && ((sMin >= sMax) || (tMin >= tMax))))
        {
            Fill(texels, ErrorColor);
            return;
        }

        // The color as 16-bit UNORM values: their high bytes
        Span<byte> color = stackalloc byte[4];
        for (int c = 0; c < 4; c++)
        {
            color[c] = (byte)(bits.Read(64 + (c * 16), 16) >> 8);
        }

        Fill(texels, color);
    }

    // The block mode (the block's low 11 bits): the weight grid's size, the weights' range,
    // and whether there are two planes of weights. False for reserved modes.
    private static bool DecodeBlockMode(
        int mode,
        out int width,
        out int height,
        out int weightRange,
        out bool dualPlane)
    {
        width = 0;
        height = 0;
        weightRange = 0;
        dualPlane = false;

        int r;
        bool highPrecision = ((mode >> 9) & 1) != 0;
        dualPlane = ((mode >> 10) & 1) != 0;
        int a = (mode >> 5) & 3;
        if ((mode & 3) != 0)
        {
            r = ((mode >> 4) & 1) | ((mode & 3) << 1);
            int b = (mode >> 7) & 3;
            switch ((mode >> 2) & 3)
            {
                case 0:
                    width = b + 4;
                    height = a + 2;
                    break;

                case 1:
                    width = b + 8;
                    height = a + 2;
                    break;

                case 2:
                    width = a + 2;
                    height = b + 8;
                    break;

                default:
                    if (((mode >> 8) & 1) == 0)
                    {
                        width = a + 2;
                        height = (b & 1) + 6;
                    }
                    else
                    {
                        width = (b & 1) + 2;
                        height = a + 2;
                    }

                    break;
            }
        }
        else
        {
            r = ((mode >> 4) & 1) | (((mode >> 2) & 3) << 1);
            if ((mode & 0xF) == 0)
            {
                // Reserved (R would be 0)
                return false;
            }

            switch ((mode >> 7) & 3)
            {
                case 0:
                    width = 12;
                    height = a + 2;
                    break;

                case 1:
                    width = a + 2;
                    height = 12;
                    break;

                case 2:
                    // No dual plane or high precision here: those bits are B
                    width = a + 6;
                    height = ((mode >> 9) & 3) + 6;
                    highPrecision = false;
                    dualPlane = false;
                    break;

                default:
                    if (a == 0)
                    {
                        width = 6;
                        height = 10;
                    }
                    else if (a == 1)
                    {
                        width = 10;
                        height = 6;
                    }
                    else
                    {
                        // Reserved, or the void extent (handled before)
                        return false;
                    }

                    break;
            }
        }

        if (r < 2)
        {
            return false;
        }

        weightRange = WeightRanges[(r - 2) + (highPrecision ? 6 : 0)];
        return true;
    }

    // The bits the integer sequence encoding takes for count values of a range
    private static int SequenceBits(
        int range,
        int count)
    {
        int bits = RangeBits[range] * count;
        if (RangeTrits[range] != 0)
        {
            bits += ((count * 8) + 4) / 5;
        }
        else if (RangeQuints[range] != 0)
        {
            bits += ((count * 7) + 2) / 3;
        }

        return bits;
    }

    // The bits of a trit block's trits after each of its five values, and of a quint block's
    // quints after each of its three
    private static readonly int[] TritBitsAfter = [2, 2, 1, 2, 1];
    private static readonly int[] QuintBitsAfter = [3, 2, 2];

    // Reads count values of a range's integer sequence encoding, from a bit offset up. Each value
    // is the trit or quint (if any) times 2^bits, plus the low bits. A last group of values may be
    // cut short: its bits past the sequence's end read as zeros.
    private static void DecodeSequence(
        BlockBits bits,
        int offset,
        int range,
        int count,
        Span<int> values)
    {
        int end = offset + SequenceBits(range, count);
        int lowBits = RangeBits[range];
        Span<int> low = stackalloc int[5];
        if (RangeTrits[range] != 0)
        {
            // Blocks of five values: their low bits, with the eight bits of their trits between them
            for (int first = 0; first < count; first += 5)
            {
                int t = 0;
                int tritShift = 0;
                for (int i = 0; i < 5; i++)
                {
                    low[i] = (int)bits.Read(offset, lowBits, end);
                    offset += lowBits;
                    t |= (int)bits.Read(offset, TritBitsAfter[i], end) << tritShift;
                    offset += TritBitsAfter[i];
                    tritShift += TritBitsAfter[i];
                }

                for (int i = 0; (i < 5) && (first + i < count); i++)
                {
                    values[first + i] = (TritsOf[(t * 5) + i] << lowBits) | low[i];
                }
            }
        }
        else if (RangeQuints[range] != 0)
        {
            // Blocks of three values, with the seven bits of their quints between them
            for (int first = 0; first < count; first += 3)
            {
                int q = 0;
                int quintShift = 0;
                for (int i = 0; i < 3; i++)
                {
                    low[i] = (int)bits.Read(offset, lowBits, end);
                    offset += lowBits;
                    q |= (int)bits.Read(offset, QuintBitsAfter[i], end) << quintShift;
                    offset += QuintBitsAfter[i];
                    quintShift += QuintBitsAfter[i];
                }

                for (int i = 0; (i < 3) && (first + i < count); i++)
                {
                    values[first + i] = (QuintsOf[(q * 3) + i] << lowBits) | low[i];
                }
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                values[i] = (int)bits.Read(offset, lowBits);
                offset += lowBits;
            }
        }
    }

    // A partition's two endpoints, RGBA 0 to 255, from its mode's unquantized values. False for
    // the HDR modes.
    private static bool DecodeEndpoints(
        int mode,
        ReadOnlySpan<int> v,
        Span<int> e)
    {
        switch (mode)
        {
            case 0: // Luminance, direct
                Set(e, 0, v[0], v[0], v[0], 0xFF);
                Set(e, 4, v[1], v[1], v[1], 0xFF);
                return true;

            case 1: // Luminance, base and offset
                {
                    int l0 = (v[0] >> 2) | (v[1] & 0xC0);
                    int l1 = Math.Min(l0 + (v[1] & 0x3F), 0xFF);
                    Set(e, 0, l0, l0, l0, 0xFF);
                    Set(e, 4, l1, l1, l1, 0xFF);
                    return true;
                }

            case 4: // Luminance and alpha, direct
                Set(e, 0, v[0], v[0], v[0], v[2]);
                Set(e, 4, v[1], v[1], v[1], v[3]);
                return true;

            case 5: // Luminance and alpha, base and offset
                {
                    (int l, int dl) = BitTransferSigned(v[1], v[0]);
                    (int a, int da) = BitTransferSigned(v[3], v[2]);
                    Set(e, 0, l, l, l, a);
                    int l1 = Clamp(l + dl);
                    Set(e, 4, l1, l1, l1, Clamp(a + da));
                    return true;
                }

            case 6: // RGB and a scale
                Set(e, 0, (v[0] * v[3]) >> 8, (v[1] * v[3]) >> 8, (v[2] * v[3]) >> 8, 0xFF);
                Set(e, 4, v[0], v[1], v[2], 0xFF);
                return true;

            case 8: // RGB, direct
                Direct(v, e, v[0], v[2], v[4], 0xFF, v[1], v[3], v[5], 0xFF);
                return true;

            case 9: // RGB, base and offset
                BaseAndOffset(v, e, hasAlpha: false);
                return true;

            case 10: // RGB, a scale, and two alphas
                Set(e, 0, (v[0] * v[3]) >> 8, (v[1] * v[3]) >> 8, (v[2] * v[3]) >> 8, v[4]);
                Set(e, 4, v[0], v[1], v[2], v[5]);
                return true;

            case 12: // RGBA, direct
                Direct(v, e, v[0], v[2], v[4], v[6], v[1], v[3], v[5], v[7]);
                return true;

            case 13: // RGBA, base and offset
                BaseAndOffset(v, e, hasAlpha: true);
                return true;

            default: // 2, 3, 7, 11, 14 and 15: HDR
                return false;
        }
    }

    // Direct endpoints, swapped and blue contracted when the second's sum is the smaller
    private static void Direct(
        ReadOnlySpan<int> v,
        Span<int> e,
        int r0,
        int g0,
        int b0,
        int a0,
        int r1,
        int g1,
        int b1,
        int a1)
    {
        if (r1 + g1 + b1 >= r0 + g0 + b0)
        {
            Set(e, 0, r0, g0, b0, a0);
            Set(e, 4, r1, g1, b1, a1);
        }
        else
        {
            Set(e, 0, (r1 + b1) >> 1, (g1 + b1) >> 1, b1, a1);
            Set(e, 4, (r0 + b0) >> 1, (g0 + b0) >> 1, b0, a0);
        }
    }

    // A base and signed offsets: blue contracted and swapped when the offsets' sum is negative
    private static void BaseAndOffset(
        ReadOnlySpan<int> v,
        Span<int> e,
        bool hasAlpha)
    {
        (int r, int dr) = BitTransferSigned(v[1], v[0]);
        (int g, int dg) = BitTransferSigned(v[3], v[2]);
        (int b, int db) = BitTransferSigned(v[5], v[4]);
        int a = 0xFF;
        int da = 0;
        if (hasAlpha)
        {
            (a, da) = BitTransferSigned(v[7], v[6]);
        }

        if (dr + dg + db >= 0)
        {
            Set(e, 0, Clamp(r), Clamp(g), Clamp(b), Clamp(a));
            Set(e, 4, Clamp(r + dr), Clamp(g + dg), Clamp(b + db), Clamp(a + da));
        }
        else
        {
            int r1 = r + dr;
            int g1 = g + dg;
            int b1 = b + db;
            Set(e, 0, Clamp((r1 + b1) >> 1), Clamp((g1 + b1) >> 1), Clamp(b1), Clamp(a + da));
            Set(e, 4, Clamp((r + b) >> 1), Clamp((g + b) >> 1), Clamp(b), Clamp(a));
        }
    }

    // Moves the offset's top bit into the base, and makes the offset a signed 6-bit number:
    // returns the base and the offset
    private static (int Base, int Offset) BitTransferSigned(
        int a,
        int b)
    {
        b >>= 1;
        b |= a & 0x80;
        a >>= 1;
        a &= 0x3F;
        if ((a & 0x20) != 0)
        {
            a -= 0x40;
        }

        return (b, a);
    }

    private static int Clamp(
        int value)
    {
        return Math.Clamp(value, 0, 0xFF);
    }

    private static void Set(
        Span<int> e,
        int at,
        int r,
        int g,
        int b,
        int a)
    {
        e[at] = r;
        e[at + 1] = g;
        e[at + 2] = b;
        e[at + 3] = a;
    }

    // The partition a texel belongs to: the specification's hash of the partition index
    private static int SelectPartition(
        int seed,
        int x,
        int y,
        int partitionCount,
        bool smallBlock)
    {
        if (smallBlock)
        {
            x <<= 1;
            y <<= 1;
        }

        seed += (partitionCount - 1) * 1024;
        uint rnum = Hash52((uint)seed);
        int seed1 = (int)(rnum & 0xF);
        int seed2 = (int)((rnum >> 4) & 0xF);
        int seed3 = (int)((rnum >> 8) & 0xF);
        int seed4 = (int)((rnum >> 12) & 0xF);
        int seed5 = (int)((rnum >> 16) & 0xF);
        int seed6 = (int)((rnum >> 20) & 0xF);
        int seed7 = (int)((rnum >> 24) & 0xF);
        int seed8 = (int)((rnum >> 28) & 0xF);
        int seed9 = (int)((rnum >> 18) & 0xF);
        int seed10 = (int)((rnum >> 22) & 0xF);
        int seed11 = (int)((rnum >> 26) & 0xF);
        int seed12 = (int)(((rnum >> 30) | (rnum << 2)) & 0xF);

        seed1 *= seed1;
        seed2 *= seed2;
        seed3 *= seed3;
        seed4 *= seed4;
        seed5 *= seed5;
        seed6 *= seed6;
        seed7 *= seed7;
        seed8 *= seed8;
        seed9 *= seed9;
        seed10 *= seed10;
        seed11 *= seed11;
        seed12 *= seed12;

        int sh1;
        int sh2;
        if ((seed & 1) != 0)
        {
            sh1 = ((seed & 2) != 0) ? 4 : 5;
            sh2 = (partitionCount == 3) ? 6 : 5;
        }
        else
        {
            sh1 = (partitionCount == 3) ? 6 : 5;
            sh2 = ((seed & 2) != 0) ? 4 : 5;
        }

        int sh3 = ((seed & 0x10) != 0) ? sh1 : sh2;

        seed1 >>= sh1;
        seed2 >>= sh2;
        seed3 >>= sh1;
        seed4 >>= sh2;
        seed5 >>= sh1;
        seed6 >>= sh2;
        seed7 >>= sh1;
        seed8 >>= sh2;
        seed9 >>= sh3;
        seed10 >>= sh3;
        seed11 >>= sh3;
        seed12 >>= sh3;

        // z is 0 for 2D blocks, so seed9 to seed12 multiply nothing
        int a = (int)(((seed1 * x) + (seed2 * y) + (rnum >> 14)) & 0x3F);
        int b = (int)(((seed3 * x) + (seed4 * y) + (rnum >> 10)) & 0x3F);
        int c = (int)(((seed5 * x) + (seed6 * y) + (rnum >> 6)) & 0x3F);
        int d = (int)(((seed7 * x) + (seed8 * y) + (rnum >> 2)) & 0x3F);
        _ = seed9 + seed10 + seed11 + seed12;

        if (partitionCount < 4)
        {
            d = 0;
        }

        if (partitionCount < 3)
        {
            c = 0;
        }

        if ((a >= b) && (a >= c) && (a >= d))
        {
            return 0;
        }

        if ((b >= c) && (b >= d))
        {
            return 1;
        }

        return (c >= d) ? 2 : 3;
    }

    private static uint Hash52(
        uint p)
    {
        p ^= p >> 15;
        p -= p << 17;
        p += p << 7;
        p += p << 4;
        p ^= p >> 5;
        p += p << 16;
        p ^= p >> 7;
        p ^= p >> 3;
        p ^= p << 6;
        p ^= p >> 17;
        return p;
    }

    // Each trit block's five trits, from its eight bits (the specification's decoding)
    private static byte[] CreateTritTable()
    {
        var table = new byte[256 * 5];
        for (int t = 0; t < 256; t++)
        {
            int c;
            int t4;
            int t3;
            if (((t >> 2) & 7) == 7)
            {
                c = (((t >> 5) & 7) << 2) | (t & 3);
                t4 = 2;
                t3 = 2;
            }
            else
            {
                c = t & 0x1F;
                if (((t >> 5) & 3) == 3)
                {
                    t4 = 2;
                    t3 = (t >> 7) & 1;
                }
                else
                {
                    t4 = (t >> 7) & 1;
                    t3 = (t >> 5) & 3;
                }
            }

            int t2;
            int t1;
            int t0;
            if ((c & 3) == 3)
            {
                t2 = 2;
                t1 = (c >> 4) & 1;
                t0 = (((c >> 3) & 1) << 1) | (((c >> 2) & 1) & ~((c >> 3) & 1) & 1);
            }
            else if (((c >> 2) & 3) == 3)
            {
                t2 = 2;
                t1 = 2;
                t0 = c & 3;
            }
            else
            {
                t2 = (c >> 4) & 1;
                t1 = (c >> 2) & 3;
                t0 = (((c >> 1) & 1) << 1) | ((c & 1) & ~((c >> 1) & 1) & 1);
            }

            table[(t * 5) + 0] = (byte)t0;
            table[(t * 5) + 1] = (byte)t1;
            table[(t * 5) + 2] = (byte)t2;
            table[(t * 5) + 3] = (byte)t3;
            table[(t * 5) + 4] = (byte)t4;
        }

        return table;
    }

    // Each quint block's three quints, from its seven bits (the specification's decoding)
    private static byte[] CreateQuintTable()
    {
        var table = new byte[128 * 3];
        for (int q = 0; q < 128; q++)
        {
            int q0;
            int q1;
            int q2;
            if ((((q >> 1) & 3) == 3) && (((q >> 5) & 3) == 0))
            {
                int bit0 = q & 1;
                int notBit0 = ~bit0 & 1;
                q2 = (bit0 << 2) | ((((q >> 4) & 1) & notBit0) << 1) | (((q >> 3) & 1) & notBit0);
                q1 = 4;
                q0 = 4;
            }
            else
            {
                int c;
                if (((q >> 1) & 3) == 3)
                {
                    q2 = 4;
                    c = (((q >> 3) & 3) << 3) | ((~(q >> 5) & 3) << 1) | (q & 1);
                }
                else
                {
                    q2 = (q >> 5) & 3;
                    c = q & 0x1F;
                }

                if ((c & 7) == 5)
                {
                    q1 = 4;
                    q0 = (c >> 3) & 3;
                }
                else
                {
                    q1 = (c >> 3) & 3;
                    q0 = c & 7;
                }
            }

            table[(q * 3) + 0] = (byte)q0;
            table[(q * 3) + 1] = (byte)q1;
            table[(q * 3) + 2] = (byte)q2;
        }

        return table;
    }

    // The unquantized color endpoint value (0 to 255) of each value of each range
    private static byte[][] CreateColorUnquantize()
    {
        var tables = new byte[RangeLevels.Length][];
        for (int range = 0; range < RangeLevels.Length; range++)
        {
            var table = new byte[RangeLevels[range]];
            int bitCount = RangeBits[range];
            for (int value = 0; value < table.Length; value++)
            {
                if ((RangeTrits[range] == 0) && (RangeQuints[range] == 0))
                {
                    table[value] = (byte)ReplicateBits(value, bitCount, 8);
                    continue;
                }

                int d = value >> bitCount;
                int m = value & ((1 << bitCount) - 1);
                int a = ((m & 1) != 0) ? 0x1FF : 0;
                int b = (m >> 1) & 1;
                int c2 = (m >> 2) & 1;
                int d2 = (m >> 3) & 1;
                int e = (m >> 4) & 1;
                int f = (m >> 5) & 1;
                int bigB;
                int bigC;
                if (RangeTrits[range] != 0)
                {
                    (bigB, bigC) = bitCount switch
                    {
                        1 => (0, 204),
                        2 => ((b << 8) | (b << 4) | (b << 2) | (b << 1), 93),
                        3 => ((c2 << 8) | (b << 7) | (c2 << 3) | (b << 2) | (c2 << 1) | b, 44),
                        4 => ((d2 << 8) | (c2 << 7) | (b << 6) | (d2 << 2) | (c2 << 1) | b, 22),
                        5 => ((e << 8) | (d2 << 7) | (c2 << 6) | (b << 5) | (e << 1) | d2, 11),
                        _ => ((f << 8) | (e << 7) | (d2 << 6) | (c2 << 5) | (b << 4) | f, 5),
                    };
                }
                else
                {
                    (bigB, bigC) = bitCount switch
                    {
                        1 => (0, 113),
                        2 => ((b << 8) | (b << 3) | (b << 2), 54),
                        3 => ((c2 << 8) | (b << 7) | (c2 << 2) | (b << 1) | c2, 26),
                        4 => ((d2 << 8) | (c2 << 7) | (b << 6) | (d2 << 1) | c2, 13),
                        _ => ((e << 8) | (d2 << 7) | (c2 << 6) | (b << 5) | e, 6),
                    };
                }

                int t = (d * bigC) + bigB;
                t ^= a;
                table[value] = (byte)((a & 0x80) | (t >> 2));
            }

            tables[range] = table;
        }

        return tables;
    }

    // The unquantized weight (0 to 64) of each value of each weight range (the first twelve)
    private static byte[][] CreateWeightUnquantize()
    {
        var tables = new byte[12][];
        for (int range = 0; range < 12; range++)
        {
            var table = new byte[RangeLevels[range]];
            int bitCount = RangeBits[range];
            for (int value = 0; value < table.Length; value++)
            {
                int result;
                if ((RangeTrits[range] == 0) && (RangeQuints[range] == 0))
                {
                    result = ReplicateBits(value, bitCount, 6);
                }
                else if (bitCount == 0)
                {
                    // Trits alone: 0, 32, 64; quints alone: 0, 16, 32, 48, 64. These are the
                    // weights as they are, with no step above 32.
                    table[value] = (byte)((RangeTrits[range] != 0) ? value * 32 : value * 16);
                    continue;
                }
                else
                {
                    int d = value >> bitCount;
                    int m = value & ((1 << bitCount) - 1);
                    int a = ((m & 1) != 0) ? 0x7F : 0;
                    int b = (m >> 1) & 1;
                    int c2 = (m >> 2) & 1;
                    int bigB;
                    int bigC;
                    if (RangeTrits[range] != 0)
                    {
                        (bigB, bigC) = bitCount switch
                        {
                            1 => (0, 50),
                            2 => ((b << 6) | (b << 2) | b, 23),
                            _ => ((c2 << 6) | (b << 5) | (c2 << 1) | b, 11),
                        };
                    }
                    else
                    {
                        (bigB, bigC) = bitCount switch
                        {
                            1 => (0, 28),
                            _ => ((b << 6) | (b << 1), 13),
                        };
                    }

                    int t = (d * bigC) + bigB;
                    t ^= a;
                    result = (a & 0x20) | (t >> 2);
                }

                // Weights above 32 are stepped up, so 64 is the top
                if (result > 32)
                {
                    result++;
                }

                table[value] = (byte)result;
            }

            tables[range] = table;
        }

        return tables;
    }

    // A value of bitCount bits repeated to fill outBits bits
    private static int ReplicateBits(
        int value,
        int bitCount,
        int outBits)
    {
        int result = 0;
        int filled = 0;
        while (filled < outBits)
        {
            result = (result << bitCount) | value;
            filled += bitCount;
        }

        return result >> (filled - outBits);
    }

    // Each texel's four grid weights and their factors for the bilinear infill of a weight grid
    // (the specification's): eight ints a texel, the grid indices and then the factors (summing
    // to 16). Grid points past its edge only ever have a factor of 0, so their indices are
    // clamped to the grid. Built once per block and grid size, and shared between threads (two
    // threads may build the same table; either is right).
    private static class Infill
    {
        private static readonly int[]?[] Tables = new int[]?[13 * 13 * 13 * 13];

        public static int[] For(
            int blockWidth,
            int blockHeight,
            int gridWidth,
            int gridHeight)
        {
            int key = (((((blockWidth * 13) + blockHeight) * 13) + gridWidth) * 13) + gridHeight;
            return System.Threading.Volatile.Read(ref Tables[key]) ?? Create(key, blockWidth, blockHeight, gridWidth, gridHeight);
        }

        private static int[] Create(
            int key,
            int blockWidth,
            int blockHeight,
            int gridWidth,
            int gridHeight)
        {
            var table = new int[blockWidth * blockHeight * 8];
            int ds = (1024 + (blockWidth / 2)) / (blockWidth - 1);
            int dt = (1024 + (blockHeight / 2)) / (blockHeight - 1);
            for (int t = 0; t < blockHeight; t++)
            {
                for (int s = 0; s < blockWidth; s++)
                {
                    int gs = ((ds * s * (gridWidth - 1)) + 32) >> 6;
                    int gt = ((dt * t * (gridHeight - 1)) + 32) >> 6;
                    int js = gs >> 4;
                    int fs = gs & 0xF;
                    int jt = gt >> 4;
                    int ft = gt & 0xF;
                    int w11 = ((fs * ft) + 8) >> 4;

                    int at = ((t * blockWidth) + s) * 8;
                    int x1 = Math.Min(js + 1, gridWidth - 1);
                    int y1 = Math.Min(jt + 1, gridHeight - 1);
                    table[at] = (jt * gridWidth) + js;
                    table[at + 1] = (jt * gridWidth) + x1;
                    table[at + 2] = (y1 * gridWidth) + js;
                    table[at + 3] = (y1 * gridWidth) + x1;
                    table[at + 4] = 16 - fs - ft + w11;
                    table[at + 5] = fs - w11;
                    table[at + 6] = ft - w11;
                    table[at + 7] = w11;
                }
            }

            System.Threading.Volatile.Write(ref Tables[key], table);
            return table;
        }
    }

    // A block's 128 bits, read from bit 0 (the first byte's lowest) up
    private readonly struct BlockBits(
        ulong low,
        ulong high)
    {
        // Bits from offset up, reading zeros at end and past it
        public ulong Read(
            int offset,
            int count,
            int end)
        {
            return this.Read(offset, Math.Clamp(end - offset, 0, count));
        }

        public ulong Read(
            int offset,
            int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            // The 64 bits from offset, from the two halves (C#'s shifts take their count mod 64)
            ulong value;
            if (offset >= 128)
            {
                return 0;
            }
            else if (offset >= 64)
            {
                value = high >> (offset - 64);
            }
            else if (offset == 0)
            {
                value = low;
            }
            else
            {
                value = (low >> offset) | (high << (64 - offset));
            }

            return (count >= 64) ? value : (value & ((1UL << count) - 1));
        }

        // The bits in reverse order: bit 127 becomes bit 0
        public BlockBits Reversed()
        {
            return new BlockBits(ReverseBits(high), ReverseBits(low));
        }

        // Each byte's bits reversed, then the bytes' order
        private static ulong ReverseBits(
            ulong value)
        {
            ulong result = 0;
            for (int i = 0; i < 8; i++)
            {
                result = (result << 8) | ReversedBytes[(int)(value & 0xFF)];
                value >>= 8;
            }

            return result;
        }

        private static readonly byte[] ReversedBytes = CreateReversedBytes();

        private static byte[] CreateReversedBytes()
        {
            var table = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int reversed = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    reversed |= ((i >> bit) & 1) << (7 - bit);
                }

                table[i] = (byte)reversed;
            }

            return table;
        }
    }
}
