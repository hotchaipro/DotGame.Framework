// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Puts an Ogg Opus file's packets into a Core Audio Format file, for iOS: AVAudioFile reads Opus
/// in CAF (not in Ogg) and decodes it with the system's own decoder, trimming the encoder's
/// priming and the padding from the packet table, so a sound or a track has exactly the source's
/// length and a sound bank's offsets land on the source's samples.
/// </summary>
/// <remarks>
/// The layout is afconvert's (macOS 27): a <c>desc</c> chunk for 48 kHz Opus of 960-frame packets,
/// the decoder's <c>kuki</c> cookie, the channel layout, the packet table (the pre-skip as priming,
/// the padding past the last granule position as remainder) and the packets. Every packet must be
/// 20 ms, as the content pipeline encodes them.
/// </remarks>
public static class CafOpusWriter
{
    private const int FramesPerPacket = 960;

    /// <summary>Writes <paramref name="oggOpus"/>'s audio to <paramref name="outputFile"/> as CAF.</summary>
    public static void Write(string outputFile, byte[] oggOpus)
    {
        File.WriteAllBytes(outputFile, Convert(oggOpus));
    }

    /// <summary><paramref name="oggOpus"/>'s audio as a CAF file.</summary>
    public static byte[] Convert(byte[] oggOpus)
    {
        var packets = new OggOpusPackets(oggOpus);
        for (int i = 0; i < packets.Count; i++)
        {
            if (packets.Start(i) != (long)i * FramesPerPacket)
            {
                throw new ContentBuildException("An Opus file for iOS must have 20 ms packets only.");
            }
        }

        long padded = (long)packets.Count * FramesPerPacket;
        long remainder = padded - packets.PreSkip - packets.TotalFrames;

        var output = new MemoryStream();
        output.Write("caff"u8);
        WriteUInt16(output, 1);
        WriteUInt16(output, 0);

        // The stream's format (an AudioStreamBasicDescription)
        Chunk(output, "desc", 32);
        WriteDouble(output, SampleRate);
        output.Write("opus"u8);
        WriteUInt32(output, 0);                  // format flags
        WriteUInt32(output, 0);                  // bytes per packet: variable
        WriteUInt32(output, FramesPerPacket);
        WriteUInt32(output, (uint)packets.Channels);
        WriteUInt32(output, 0);                  // bits per channel

        // The decoder's magic cookie, as afconvert writes it
        Chunk(output, "kuki", 28);
        WriteUInt32(output, 0x800);
        WriteUInt32(output, SampleRate);
        WriteUInt32(output, FramesPerPacket);
        WriteUInt32(output, 0xfffffc18);
        WriteUInt32(output, (uint)packets.Channels);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);

        // The channel layout: kAudioChannelLayoutTag_Mono or _Stereo
        Chunk(output, "chan", 12);
        WriteUInt32(output, packets.Channels == 1 ? 0x00640001u : 0x00650002u);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);

        // The packet table: counts, then each packet's size as a variable-length integer
        var table = new MemoryStream();
        for (int i = 0; i < packets.Count; i++)
        {
            WriteVariableLength(table, packets.Packet(i).Length);
        }
        Chunk(output, "pakt", 24 + table.Length);
        WriteInt64(output, packets.Count);
        WriteInt64(output, packets.TotalFrames);
        WriteUInt32(output, (uint)packets.PreSkip);
        WriteUInt32(output, (uint)remainder);
        table.WriteTo(output);

        // The packets, after the edit count
        long data = 4;
        for (int i = 0; i < packets.Count; i++)
        {
            data += packets.Packet(i).Length;
        }
        Chunk(output, "data", data);
        WriteUInt32(output, 1);
        for (int i = 0; i < packets.Count; i++)
        {
            output.Write(packets.Packet(i));
        }

        return output.ToArray();
    }

    private const int SampleRate = 48000;

    private static void Chunk(Stream output, string type, long size)
    {
        output.Write(System.Text.Encoding.ASCII.GetBytes(type));
        WriteInt64(output, size);
    }

    private static void WriteVariableLength(Stream output, int value)
    {
        // Seven bits a byte, most significant first, the high bit set on all but the last
        Span<byte> bytes = stackalloc byte[5];
        int count = 0;
        do
        {
            bytes[count++] = (byte)(value & 0x7f);
            value >>= 7;
        }
        while (value != 0);
        for (int i = count - 1; i >= 0; i--)
        {
            output.WriteByte((byte)(bytes[i] | (i > 0 ? 0x80 : 0)));
        }
    }

    private static void WriteUInt16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteUInt32(Stream output, int value) => WriteUInt32(output, (uint)value);

    private static void WriteInt64(Stream output, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteDouble(Stream output, double value) => WriteInt64(output, BitConverter.DoubleToInt64Bits(value));
}
