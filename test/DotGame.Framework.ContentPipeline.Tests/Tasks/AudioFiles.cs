// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Text;
using DotGame.Framework.ContentPipeline.Tasks;
using Microsoft.Build.Framework;

namespace DotGame.ContentPipeline.Testing;

/// <summary>
/// Audio files with just the headers the pipeline reads, and the pipeline's reading of a file.
/// </summary>
internal static class AudioFiles
{
    /// <summary>What <see cref="_ProbeMediaDurations"/> makes of a file, and its warnings.</summary>
    public static (string Duration, string SampleRate, string Channels, List<string> Warnings) Read(string path)
    {
        var engine = new RecordingBuildEngine();
        var task = new _ProbeMediaDurations
        {
            BuildEngine = engine,
            InputItems = [TaskItems.Item("item", ("_ProbeFile", path))],
            ProbeFileMetadata = "_ProbeFile",
        };
        if (!task.Execute())
        {
            throw new InvalidOperationException("The probe failed the build.");
        }

        ITaskItem item = task.OutputItems[0];
        return (item.GetMetadata("_DurationSeconds"), item.GetMetadata("_SampleRate"), item.GetMetadata("_Channels"), engine.Warnings);
    }

    public static byte[] Wav(int sampleRate, int channels, int frames, bool listChunk = false)
    {
        var file = new MemoryStream();
        var writer = new BinaryWriter(file);
        int blockAlign = channels * 2;
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        if (listChunk)
        {
            // A chunk before the format, with an odd size and its pad byte
            writer.Write("LIST"u8);
            writer.Write(5);
            writer.Write("INFOx\0"u8);
        }
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * blockAlign);
        writer.Write(new byte[frames * blockAlign]);
        writer.Flush();
        return file.ToArray();
    }

    public static byte[] Flac(int sampleRate, int channels, long samples)
    {
        var info = new byte[34];
        info[10] = (byte)(sampleRate >> 12);
        info[11] = (byte)(sampleRate >> 4);
        info[12] = (byte)(((sampleRate & 0xF) << 4) | ((channels - 1) << 1));
        info[13] = (byte)((15 << 4) | (int)((samples >> 32) & 0xF));
        info[14] = (byte)(samples >> 24);
        info[15] = (byte)(samples >> 16);
        info[16] = (byte)(samples >> 8);
        info[17] = (byte)samples;
        return [.. "fLaC"u8, 0x80, 0, 0, 34, .. info, .. new byte[100]];
    }

    public static byte[] OggVorbis(int sampleRate, int channels, long lastGranule)
    {
        var header = new byte[30];
        header[0] = 1;
        Encoding.ASCII.GetBytes("vorbis").CopyTo(header, 1);
        header[11] = (byte)channels;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), sampleRate);
        return Ogg(header, lastGranule);
    }

    public static byte[] OggOpus(int channels, int preSkip, long lastGranule)
    {
        var header = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(header, 0);
        header[8] = 1;
        header[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)preSkip);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 44100);
        return Ogg(header, lastGranule);
    }

    // The identification page, a page of no length (-1), a page of another stream, then the last
    // page; no checksums, which the pipeline doesn't check
    private static byte[] Ogg(byte[] identification, long lastGranule)
    {
        var file = new MemoryStream();
        Page(file, 0x02, 0, 7, identification);
        Page(file, 0, 4000, 7, new byte[3000]);
        Page(file, 0, -1, 7, new byte[200]);
        Page(file, 0x04, lastGranule, 7, new byte[50]);
        Page(file, 0, 999999, 8, new byte[20]);
        return file.ToArray();
    }

    private static void Page(Stream file, byte type, long granule, uint serial, byte[] packet)
    {
        var lacing = new List<byte>();
        int left = packet.Length;
        while (left >= 255)
        {
            lacing.Add(255);
            left -= 255;
        }
        lacing.Add((byte)left);

        var header = new byte[27];
        Encoding.ASCII.GetBytes("OggS").CopyTo(header, 0);
        header[5] = type;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), serial);
        header[26] = (byte)lacing.Count;
        file.Write(header);
        file.Write(lacing.ToArray());
        file.Write(packet);
    }

    /// <summary>An .m4a's boxes: a movie, one audio track and its edit list, if given.</summary>
    public static byte[] M4a(int movieTimescale, int movieDuration, int mediaTimescale, int mediaDuration, int sampleRate, int channels, (int Duration, int MediaTime)[]? edits, string? gapless = null, int configChannels = 0)
    {
        byte[] mvhd = Box("mvhd", [.. new byte[12], .. Big(movieTimescale), .. Big(movieDuration), .. new byte[80]]);
        byte[] tkhd = Box("tkhd", new byte[84]);
        byte[] edts = edits is null ? [] : Box("edts", Box("elst", Elst(edits)));
        byte[] mdhd = Box("mdhd", [.. new byte[12], .. Big(mediaTimescale), .. Big(mediaDuration), .. new byte[4]]);
        byte[] hdlr = Box("hdlr", [.. new byte[8], .. "soun"u8, .. new byte[13]]);
        // An ES descriptor holding a decoder config holding the AudioSpecificConfig: AAC LC at 44.1 kHz
        byte[] esds = configChannels == 0
            ? new byte[30]
            : [0, 0, 0, 0, 3, 0x80, 0x80, 0x80, 0x22, 0, 0, 0, 4, 0x80, 0x80, 0x80, 0x14, 0x40, 0x15, .. new byte[11], 5, 0x80, 0x80, 0x80, 2, 0x12, (byte)(configChannels << 3), 6, 1, 2];
        byte[] mp4a = Box("mp4a", [.. new byte[7], 1, .. new byte[8], (byte)(channels >> 8), (byte)channels, 0, 16, .. new byte[4], .. Big(sampleRate << 16), .. Box("esds", esds)]);
        byte[] stsd = Box("stsd", [.. new byte[4], .. Big(1), .. mp4a]);
        byte[] stbl = Box("stbl", [.. stsd, .. Box("stts", new byte[8])]);
        byte[] minf = Box("minf", [.. Box("smhd", new byte[8]), .. stbl]);
        byte[] mdia = Box("mdia", [.. mdhd, .. hdlr, .. minf]);
        byte[] trak = Box("trak", [.. tkhd, .. edts, .. mdia]);
        byte[] udta = gapless is null
            ? []
            : Box("udta", Box("meta", [.. new byte[4], .. Box("hdlr", new byte[25]), .. Box("ilst", Box("----", [
                .. Box("mean", [.. new byte[4], .. "com.apple.iTunes"u8]),
                .. Box("name", [.. new byte[4], .. "iTunSMPB"u8]),
                .. Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. Encoding.ASCII.GetBytes(gapless)])]))]));
        byte[] moov = Box("moov", [.. mvhd, .. trak, .. udta]);
        return [.. Box("ftyp", [.. "M4A "u8, 0, 0, 2, 0]), .. Box("mdat", new byte[500]), .. moov];
    }

    private static byte[] Elst((int Duration, int MediaTime)[] edits)
    {
        var body = new List<byte>(new byte[4]);
        body.AddRange(Big(edits.Length));
        foreach ((int duration, int mediaTime) in edits)
        {
            body.AddRange(Big(duration));
            body.AddRange(Big(mediaTime));
            body.AddRange(Big(0x00010000));
        }
        return [.. body];
    }

    private static byte[] Box(string type, byte[] body) => [.. Big(body.Length + 8), .. Encoding.ASCII.GetBytes(type), .. body];

    private static byte[] Big(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
