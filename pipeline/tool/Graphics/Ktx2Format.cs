// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// Writes textures as KTX2 files (<c>.ktx2</c>, the Khronos texture container), which
/// <see cref="TextureCommand"/> writes with <c>--format ktx2</c>: one 2D image of one mip level,
/// top row first, premultiplied unless it's data, as either
/// <list type="bullet">
/// <item><c>VK_FORMAT_R8G8B8A8_UNORM</c> pixels with zlib supercompression: desktop, which
/// inflates them with .NET's own zlib rather than decoding WebP (it has no native WebP decoder,
/// and inflating is several times faster than decoding WebP in managed code, for about 1.85
/// times the size of lossless WebP); or</item>
/// <item>ASTC blocks (<c>VK_FORMAT_ASTC_{4x4,6x6,8x8}_UNORM_BLOCK</c>), not supercompressed:
/// lossy textures on Android and iOS, whose GPUs sample them compressed.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The file follows the KTX 2.0 specification
/// (https://registry.khronos.org/KTX/specs/2.0/ktxspec.v2.html), so Khronos's tools
/// (<c>ktx info</c>, <c>ktx validate</c>, <c>ktx extract</c>) read it: the identifier, header and
/// index, a level index of one level, a basic data format descriptor (RGBSDA or ASTC, BT.709
/// primaries, linear transfer, as the UNORM formats say, and the premultiplied flag), key/value
/// data with <c>KTXwriter</c>, and the level.
/// </para>
/// <para>
/// The platforms' texture loaders read exactly this subset (the framework's internal
/// <c>Ktx2Texture</c>); keep the two in step.
/// </para>
/// </remarks>
internal static class Ktx2Format
{
    public const string Extension = ".ktx2";

    public static ReadOnlySpan<byte> Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    public const uint VkFormatR8G8B8A8Unorm = 37;
    public const uint VkFormatAstc4x4Unorm = 157;
    public const uint VkFormatAstc6x6Unorm = 165;
    public const uint VkFormatAstc8x8Unorm = 171;

    public const uint SupercompressionNone = 0;
    public const uint SupercompressionZlib = 3;

    // The identifier, the header's nine words, and the index (four words and two longs)
    private const int HeaderAndIndexSize = 12 + (9 * 4) + (4 * 4) + (2 * 8);
    private const int LevelIndexEntrySize = 3 * 8;

    // A basic data format descriptor's block header is six words; each sample, four
    private const int DescriptorBlockHeaderSize = 6 * 4;
    private const int SampleSize = 4 * 4;

    private const uint KhrDfModelRgbsda = 1;
    private const uint KhrDfModelAstc = 162;
    private const uint KhrDfPrimariesBt709 = 1;
    private const uint KhrDfTransferLinear = 1;
    private const uint KhrDfFlagAlphaPremultiplied = 1;
    private const uint KhrDfVersionNumber13 = 2;
    private const uint KhrDfChannelAlpha = 15;

    private const string Writer = "DotGame content pipeline";

    /// <summary>
    /// Writes <paramref name="image"/> as a KTX2 file of RGBA8 pixels, zlib supercompressed. The
    /// pixels are written as they are: premultiply them first unless they're data, and say which
    /// with <paramref name="premultiplied"/>.
    /// </summary>
    public static void WriteRgba8(
        string path,
        RgbaImage image,
        bool premultiplied)
    {
        int uncompressedLength = image.Width * image.Height * 4;
        byte[] level;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(image.Pixels, 0, uncompressedLength);
            }

            level = compressed.ToArray();
        }

        // R, G, B and alpha: a byte each, 0 to 255
        var dfd = new DataFormatDescriptor(KhrDfModelRgbsda, 1, 1, 4, premultiplied);
        uint[] channels = [0, 1, 2, KhrDfChannelAlpha];
        for (int sample = 0; sample < channels.Length; sample++)
        {
            dfd.AddSample(bitOffset: sample * 8, bitLength: 8, channels[sample], lower: 0, upper: 255);
        }

        Write(path, VkFormatR8G8B8A8Unorm, image.Width, image.Height, dfd, SupercompressionZlib, level, uncompressedLength, alignment: 4);
    }

    /// <summary>
    /// Writes ASTC blocks (16 bytes each, a row of blocks at a time, the top row first) as a KTX2
    /// file. <paramref name="blockSize"/> is 4, 6 or 8 (square blocks).
    /// </summary>
    public static void WriteAstc(
        string path,
        int width,
        int height,
        int blockSize,
        byte[] blocks,
        bool premultiplied)
    {
        uint vkFormat = blockSize switch
        {
            4 => VkFormatAstc4x4Unorm,
            6 => VkFormatAstc6x6Unorm,
            8 => VkFormatAstc8x8Unorm,
            _ => throw new ContentBuildException($"ASTC block size {blockSize} isn't supported (4, 6 or 8)."),
        };

        int expectedLength = ((width + blockSize - 1) / blockSize) * ((height + blockSize - 1) / blockSize) * 16;
        if (blocks.Length != expectedLength)
        {
            throw new ContentBuildException($"The ASTC data is {blocks.Length} bytes; a {width} x {height} image of {blockSize} x {blockSize} blocks has {expectedLength}.");
        }

        // One sample: the 128 bit block
        var dfd = new DataFormatDescriptor(KhrDfModelAstc, blockSize, blockSize, 16, premultiplied);
        dfd.AddSample(bitOffset: 0, bitLength: 128, channelType: 0, lower: 0, upper: uint.MaxValue);

        // Uncompressed levels are aligned to lcm(texel block size, 4)
        Write(path, vkFormat, width, height, dfd, SupercompressionNone, blocks, blocks.Length, alignment: 16);
    }

    private static void Write(
        string path,
        uint vkFormat,
        int width,
        int height,
        DataFormatDescriptor dfd,
        uint supercompression,
        byte[] level,
        int uncompressedLength,
        int alignment)
    {
        byte[] dfdBytes = dfd.ToBytes();
        byte[] keyValueData = KeyValue("KTXwriter", Writer);

        // The descriptor follows the level index; the key/value data follows it, 4 byte aligned
        // (the descriptor's size is a multiple of 4); the level data comes last, aligned for its
        // format (a supercompressed level needs none, but aligning it costs nothing)
        int dfdOffset = HeaderAndIndexSize + LevelIndexEntrySize;
        int kvdOffset = dfdOffset + dfdBytes.Length;
        int levelOffset = kvdOffset + keyValueData.Length;
        levelOffset = (levelOffset + alignment - 1) / alignment * alignment;

        var head = new byte[levelOffset];
        Identifier.CopyTo(head);
        int at = Identifier.Length;
        void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(at), value);
            at += 4;
        }

        void UInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(at), value);
            at += 8;
        }

        // Header
        UInt32(vkFormat);
        UInt32(1); // typeSize: bytes (1 for block compressed formats too)
        UInt32((uint)width);
        UInt32((uint)height);
        UInt32(0); // pixelDepth: 2D
        UInt32(0); // layerCount: not an array
        UInt32(1); // faceCount
        UInt32(1); // levelCount
        UInt32(supercompression);

        // Index
        UInt32((uint)dfdOffset);
        UInt32((uint)dfdBytes.Length);
        UInt32((uint)kvdOffset);
        UInt32((uint)keyValueData.Length);
        UInt64(0); // sgdByteOffset: no supercompression global data
        UInt64(0);

        // Level index
        UInt64((ulong)levelOffset);
        UInt64((ulong)level.Length);
        UInt64((ulong)uncompressedLength);

        dfdBytes.CopyTo(head, at);
        keyValueData.CopyTo(head, kvdOffset);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        file.Write(head);
        file.Write(level);
    }

    // A key/value entry: its length, the key and the value, each NUL terminated, padded to 4 bytes
    private static byte[] KeyValue(
        string key,
        string value)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);
        int length = keyBytes.Length + 1 + valueBytes.Length + 1;
        var entry = new byte[4 + ((length + 3) & ~3)];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)length);
        keyBytes.CopyTo(entry, 4);
        valueBytes.CopyTo(entry, 4 + keyBytes.Length + 1);
        return entry;
    }

    // A basic data format descriptor (Khronos Data Format 1.3): its total size, a block header
    // (vendor and type, version and size, color model, primaries, transfer and flags, texel block
    // dimensions, bytes per plane), then a sample per channel
    private sealed class DataFormatDescriptor(
        uint colorModel,
        int blockWidth,
        int blockHeight,
        int bytesPerBlock,
        bool premultiplied)
    {
        private readonly List<uint> _samples = [];

        public void AddSample(
            int bitOffset,
            int bitLength,
            uint channelType,
            uint lower,
            uint upper)
        {
            // Bit offset, bit length less one, channel type (no qualifiers: unsigned, linear, integer)
            this._samples.Add((uint)bitOffset | ((uint)(bitLength - 1) << 16) | (channelType << 24));
            this._samples.Add(0); // samplePosition0..3
            this._samples.Add(lower);
            this._samples.Add(upper);
        }

        public byte[] ToBytes()
        {
            int blockSize = DescriptorBlockHeaderSize + ((this._samples.Count / 4) * SampleSize);
            var words = new List<uint>
            {
                (uint)(4 + blockSize), // dfdTotalSize
                0, // vendorId KHR (0), descriptorType basic (0)
                KhrDfVersionNumber13 | ((uint)blockSize << 16),
                colorModel | (KhrDfPrimariesBt709 << 8) | (KhrDfTransferLinear << 16) | ((premultiplied ? KhrDfFlagAlphaPremultiplied : 0) << 24),
                (uint)(blockWidth - 1) | ((uint)(blockHeight - 1) << 8), // texelBlockDimension0..3, each less one
                (uint)bytesPerBlock, // bytesPlane0; planes 1..3 none
                0, // bytesPlane4..7
            };
            words.AddRange(this._samples);

            var bytes = new byte[words.Count * 4];
            for (int i = 0; i < words.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
            }

            return bytes;
        }
    }
}
