// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace DotGame.Framework.Platform.Common.Content;

/// <summary>
/// A texture read from a KTX2 file, as the content pipeline's <c>texture --format ktx2</c>
/// command writes them (its <c>Ktx2Format</c>; keep the two in step): one 2D image of one mip
/// level, top row first, of RGBA8 pixels (zlib supercompressed or not) or ASTC blocks. The
/// platforms' texture loaders upload its <see cref="Data"/> as it is. Other KTX2 files (arrays,
/// cube maps, mip chains, other formats or supercompression schemes) are refused.
/// </summary>
/// <remarks>
/// Textures are large objects, freed only by a full collection, so a level's worth of them
/// would cause several. The buffers come from the shared pool instead, and go back when this
/// is disposed, so dispose it after the upload.
/// </remarks>
internal sealed class Ktx2Texture : IDisposable
{
    private static ReadOnlySpan<byte> Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    // The identifier, the header's nine words, the index, and one level index entry
    private const int HeaderSize = 12 + (9 * 4) + (4 * 4) + (2 * 8) + (3 * 8);

    private const uint SupercompressionNone = 0;
    private const uint SupercompressionZlib = 3;

    // Where reading a stream of unknown length starts
    private const int InitialStreamBuffer = 256 * 1024;

    // GPU textures are at most 16384 texels a side
    private const int MaxDimension = 16384;

    private Ktx2Texture(
        Ktx2PixelFormat format,
        Vector2Int size,
        bool premultiplied,
        byte[] buffer,
        int offset,
        int length,
        bool isBufferRented)
    {
        this.Format = format;
        this.Size = size;
        this.Premultiplied = premultiplied;
        this._buffer = buffer;
        this._offset = offset;
        this._length = length;
        this._isBufferRented = isBufferRented;
    }

    private byte[]? _buffer;
    private readonly int _offset;
    private readonly int _length;
    private readonly bool _isBufferRented;

    public Ktx2PixelFormat Format { get; }

    /// <summary>
    /// The image's size in texels (not rounded up to whole blocks).
    /// </summary>
    public Vector2Int Size { get; }

    /// <summary>
    /// Whether the colors are premultiplied by alpha (the data format descriptor's flag).
    /// </summary>
    public bool Premultiplied { get; }

    /// <summary>
    /// The level's texels or blocks, uncompressed: rows of RGBA8 pixels, or rows of 16 byte
    /// blocks, top row first.
    /// </summary>
    public ReadOnlySpan<byte> Data => new(this._buffer, this._offset, this._length);

    /// <summary>
    /// Returns the buffers to the pool. <see cref="Data"/> mustn't be used after this.
    /// </summary>
    public void Dispose()
    {
        var buffer = this._buffer;
        this._buffer = null;
        if ((buffer is not null) && this._isBufferRented)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The width and height of the format's blocks: 1 for RGBA8.
    /// </summary>
    public int BlockSize => BlockSizeOf(this.Format);

    public static int BlockSizeOf(
        Ktx2PixelFormat format)
    {
        return format switch
        {
            Ktx2PixelFormat.Astc4x4 => 4,
            Ktx2PixelFormat.Astc6x6 => 6,
            Ktx2PixelFormat.Astc8x8 => 8,
            _ => 1,
        };
    }

    /// <summary>
    /// Whether <paramref name="start"/>, a file's first bytes, begins a KTX2 file.
    /// </summary>
    public static bool IsKtx2(
        ReadOnlySpan<byte> start)
    {
        return start.StartsWith(Identifier);
    }

    /// <summary>
    /// Reads a KTX2 texture from <paramref name="stream"/>, which needn't be seekable (Android's
    /// asset streams aren't). <paramref name="name"/> names it in errors.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// It isn't a KTX2 file, or not one of the kinds the content pipeline writes, or it's damaged.
    /// </exception>
    public static Ktx2Texture Read(
        Stream stream,
        string name)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var file = ArrayPool<byte>.Shared.Rent(InitialStreamBuffer);
        int length = 0;
        try
        {
            int read;
            while ((read = stream.Read(file, length, file.Length - length)) > 0)
            {
                length += read;
                if (length == file.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(file.Length * 2);
                    Buffer.BlockCopy(file, 0, larger, 0, length);
                    ArrayPool<byte>.Shared.Return(file);
                    file = larger;
                }
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(file);
            throw;
        }

        return ReadRented(file, length, name);
    }

    /// <summary>
    /// Reads a KTX2 texture from the file at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// It isn't a KTX2 file, or not one of the kinds the content pipeline writes, or it's damaged.
    /// </exception>
    public static Ktx2Texture ReadFile(
        string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);

        int length = checked((int)stream.Length);
        var file = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            stream.ReadExactly(file, 0, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(file);
            throw;
        }

        return ReadRented(file, length, path);
    }

    private static Ktx2Texture ReadRented(
        byte[] file,
        int length,
        string name)
    {
        bool keepsFile = false;
        try
        {
            var texture = Read(file, length, name, isFileRented: true);
            keepsFile = ReferenceEquals(texture._buffer, file);
            return texture;
        }
        finally
        {
            if (!keepsFile)
            {
                ArrayPool<byte>.Shared.Return(file);
            }
        }
    }

    /// <summary>
    /// Reads a KTX2 texture from a file's bytes. <paramref name="name"/> names it in errors.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// It isn't a KTX2 file, or not one of the kinds the content pipeline writes, or it's damaged.
    /// </exception>
    public static Ktx2Texture Read(
        byte[] file,
        string name)
    {
        ArgumentNullException.ThrowIfNull(file);

        return Read(file, file.Length, name, isFileRented: false);
    }

    private static Ktx2Texture Read(
        byte[] file,
        int fileLength,
        string name,
        bool isFileRented)
    {
        if ((fileLength < HeaderSize) || !IsKtx2(file))
        {
            throw new InvalidDataException($"'{name}' isn't a KTX2 texture. Rebuild the content: textures before October 2026 were WebP everywhere.");
        }

        var fields = file.AsSpan(Identifier.Length, fileLength - Identifier.Length);
        uint vkFormat = BinaryPrimitives.ReadUInt32LittleEndian(fields);
        int width = BinaryPrimitives.ReadInt32LittleEndian(fields.Slice(8));
        int height = BinaryPrimitives.ReadInt32LittleEndian(fields.Slice(12));
        uint pixelDepth = BinaryPrimitives.ReadUInt32LittleEndian(fields.Slice(16));
        uint layerCount = BinaryPrimitives.ReadUInt32LittleEndian(fields.Slice(20));
        uint faceCount = BinaryPrimitives.ReadUInt32LittleEndian(fields.Slice(24));
        uint levelCount = BinaryPrimitives.ReadUInt32LittleEndian(fields.Slice(28));
        uint supercompression = BinaryPrimitives.ReadUInt32LittleEndian(fields.Slice(32));
        uint dfdOffset = BinaryPrimitives.ReadUInt32LittleEndian(fields.Slice(36));
        uint dfdLength = BinaryPrimitives.ReadUInt32LittleEndian(fields.Slice(40));

        Ktx2PixelFormat? format = vkFormat switch
        {
            37 => Ktx2PixelFormat.Rgba8,
            157 => Ktx2PixelFormat.Astc4x4,
            165 => Ktx2PixelFormat.Astc6x6,
            171 => Ktx2PixelFormat.Astc8x8,
            _ => null,
        };

        if ((format is null) || (pixelDepth != 0) || (layerCount > 1) || (faceCount != 1) || (levelCount != 1)
            || ((supercompression != SupercompressionNone) && (supercompression != SupercompressionZlib)))
        {
            throw new InvalidDataException($"'{name}' is a KTX2 texture this build can't read (format {vkFormat}, depth {pixelDepth}, layers {layerCount}, faces {faceCount}, levels {levelCount}, supercompression {supercompression}).");
        }

        if ((width <= 0) || (height <= 0) || (width > MaxDimension) || (height > MaxDimension))
        {
            throw new InvalidDataException($"'{name}' has an invalid size ({width} x {height}).");
        }

        // The level's uncompressed size: its pixels, or its blocks of 16 bytes
        int block = BlockSizeOf(format.Value);
        int dataLength = (block == 1)
            ? width * height * 4
            : ((width + block - 1) / block) * ((height + block - 1) / block) * 16;

        // The level index's one entry follows the header and the index
        var levelEntry = fields.Slice((9 * 4) + (4 * 4) + (2 * 8));
        ulong levelOffset = BinaryPrimitives.ReadUInt64LittleEndian(levelEntry);
        ulong levelLength = BinaryPrimitives.ReadUInt64LittleEndian(levelEntry.Slice(8));
        ulong uncompressedLength = BinaryPrimitives.ReadUInt64LittleEndian(levelEntry.Slice(16));
        if ((levelOffset < HeaderSize) || (levelLength == 0) || (levelOffset + levelLength > (ulong)fileLength)
            || (uncompressedLength != (ulong)dataLength)
            || ((supercompression == SupercompressionNone) && (levelLength != (ulong)dataLength)))
        {
            throw new InvalidDataException($"'{name}' is damaged (its level is out of place).");
        }

        // The premultiplied flag: the top byte of the descriptor block's third word
        bool premultiplied = false;
        if ((dfdLength >= 16) && (dfdOffset >= HeaderSize) && ((ulong)dfdOffset + dfdLength <= (ulong)fileLength))
        {
            premultiplied = (file[dfdOffset + 15] & 1) != 0;
        }

        // NOTE: An uncompressed level is uploaded from the file's own buffer, with no copy
        if (supercompression != SupercompressionZlib)
        {
            return new Ktx2Texture(format.Value, new Vector2Int(width, height), premultiplied, file, (int)levelOffset, dataLength, isFileRented);
        }

        var data = ArrayPool<byte>.Shared.Rent(dataLength);
        try
        {
            using var compressed = new MemoryStream(file, (int)levelOffset, (int)levelLength, writable: false);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            if (zlib.ReadAtLeast(data.AsSpan(0, dataLength), dataLength, throwOnEndOfStream: false) < dataLength)
            {
                throw new InvalidDataException($"'{name}' is cut short.");
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(data);
            throw;
        }

        return new Ktx2Texture(format.Value, new Vector2Int(width, height), premultiplied, data, 0, dataLength, isBufferRented: true);
    }
}
