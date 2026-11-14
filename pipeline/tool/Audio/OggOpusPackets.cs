// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// The audio packets of an Ogg Opus file the build's own encoder wrote (<see cref="OpusFileEncoder"/>),
/// copied out of its pages, with what its headers say: what <see cref="CafOpusWriter"/> needs to put
/// the packets into CAF for iOS.
/// </summary>
/// <remarks>
/// Not a general Ogg reader: one logical stream, its pages in order, no checks a file of the build's own
/// doesn't need. Samples are the stream's own, counted from its first packet, so the audio is
/// <see cref="PreSkip"/> to <see cref="PreSkip"/> + <see cref="TotalFrames"/>.
/// </remarks>
internal sealed class OggOpusPackets
{
    private readonly List<byte[]> _packets = [];
    private readonly List<long> _starts = [];

    /// <summary>Reads <paramref name="file"/>'s headers and packets.</summary>
    public OggOpusPackets(byte[] file)
    {
        var all = new List<byte[]>();
        var packet = new List<byte>();
        long lastGranule = 0;
        int at = 0;
        while (at + 27 <= file.Length)
        {
            if (!file.AsSpan(at, 4).SequenceEqual("OggS"u8))
            {
                throw new InvalidDataException("The Opus file has a damaged Ogg page.");
            }

            long granule = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(at + 6));
            int segments = file[at + 26];
            int body = at + 27 + segments;
            for (int s = 0; s < segments; s++)
            {
                int lace = file[at + 27 + s];
                packet.AddRange(file.AsSpan(body, lace));
                body += lace;
                if (lace < 255)
                {
                    all.Add([.. packet]);
                    packet.Clear();
                }
            }
            if (granule != -1)
            {
                lastGranule = granule;
            }
            at = body;
        }

        if ((all.Count < 2) || !all[0].AsSpan().StartsWith("OpusHead"u8) || (all[0].Length < 19))
        {
            throw new InvalidDataException("The file isn't Ogg Opus.");
        }

        this.Channels = all[0][9];
        this.PreSkip = BinaryPrimitives.ReadUInt16LittleEndian(all[0].AsSpan(10));

        long sample = 0;
        for (int i = 2; i < all.Count; i++)
        {
            byte[] audio = all[i];
            if (audio.Length == 0)
            {
                continue;
            }

            this._packets.Add(audio);
            this._starts.Add(sample);
            sample += Frames(audio);
        }

        this.TotalFrames = Math.Max(0, Math.Min(lastGranule, sample) - this.PreSkip);
    }

    /// <summary>The number of packets.</summary>
    public int Count => this._packets.Count;

    /// <summary>The channel count.</summary>
    public int Channels { get; }

    /// <summary>The samples to drop from the start of the stream.</summary>
    public int PreSkip { get; }

    /// <summary>The file's length in frames, with the pre-skip dropped and the end trimmed.</summary>
    public long TotalFrames { get; }

    /// <summary>Packet <paramref name="index"/>'s bytes.</summary>
    public ReadOnlySpan<byte> Packet(int index) => this._packets[index];

    /// <summary>Packet <paramref name="index"/>'s first sample.</summary>
    public long Start(int index) => this._starts[index];

    // A packet's samples at 48 kHz, from its TOC byte (RFC 6716, 3.1)
    private static int Frames(byte[] packet)
    {
        int toc = packet[0];
        int frameSize = (toc & 0x80) != 0 ? 120 << ((toc >> 3) & 3)            // CELT: 2.5 to 20 ms
            : (toc & 0x60) == 0x60 ? ((toc & 0x08) != 0 ? 960 : 480)            // hybrid: 10 or 20 ms
            : ((toc >> 3) & 3) == 3 ? 2880 : 480 << ((toc >> 3) & 3);           // SILK: 10 to 60 ms
        int frames = (toc & 3) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => packet.Length >= 2 ? packet[1] & 0x3F : throw new InvalidDataException("An Opus packet is cut short."),
        };
        return frameSize * frames;
    }
}
