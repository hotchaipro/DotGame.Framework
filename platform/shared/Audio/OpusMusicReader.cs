// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;
using DotGame.AudioCodecs.Opus;

namespace DotGame.Framework.Platform.Common.Audio;

/// <summary>
/// Streams a music track, an Ogg Opus file as the content pipeline writes music, as 16-bit
/// interleaved PCM at 48 kHz, with sample-exact seeking for start and loop points: DotGame.AudioCodecs's
/// <see cref="OpusDecoder"/> over the file, held in memory while the track is in use.
/// </summary>
/// <remarks>
/// The file is read on the first read or seek, so a track can be described from the manifest before
/// its file is touched, and <see cref="Release"/> drops it while the track sits idle. Music is small
/// compressed (112 kbps is 0.8 MB a minute), and holding it saves a file read in the audio path. A seek
/// starts decoding 400 ms before its target, so a loop has no seam (<see cref="OpusDecoder"/>). Not
/// thread-safe: one thread reads and seeks at a time. Desktop and Android only: iOS and the web decode
/// music natively.
/// </remarks>
internal sealed class OpusMusicReader
{
    /// <summary>Opus always decodes at 48 kHz.</summary>
    public const int SampleRate = 48000;

    private readonly Func<Stream> _open;

    // Resident between the first read or seek and Release
    private OpusDecoder? _decoder;

    private long _position;   // the next frame a read returns

    /// <summary>
    /// A reader of the file <paramref name="open"/> opens, which it reads to the end and disposes.
    /// <paramref name="channels"/> and <paramref name="totalFrames"/> are the manifest's, so a
    /// track can be described before its file is read; the file's own replace them once it is.
    /// </summary>
    public OpusMusicReader(Func<Stream> open, int channels, long totalFrames)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Opus music is mono or stereo.");
        }

        this._open = open;
        this.Channels = channels;
        this.TotalFrames = totalFrames;
    }

    /// <summary>The channel count.</summary>
    public int Channels { get; private set; }

    /// <summary>The track's length in frames (samples per channel).</summary>
    public long TotalFrames { get; private set; }

    /// <summary>The frame the next read returns.</summary>
    public long FramePosition => this._position;

    /// <summary>The channel count and length in frames of the Ogg Opus file <paramref name="file"/>.</summary>
    public static (int Channels, long Frames) Probe(byte[] file)
    {
        using var decoder = new OpusDecoder(file);
        return (decoder.Channels, decoder.Frames ?? 0);
    }

    /// <summary>The whole of <paramref name="stream"/>, which is then at its end.</summary>
    public static byte[] ReadAll(Stream stream)
    {
        if (stream.CanSeek)
        {
            var file = new byte[checked((int)(stream.Length - stream.Position))];
            stream.ReadExactly(file);
            return file;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Positions the next read at <paramref name="frame"/>, clamped to the track.</summary>
    public void SeekTo(long frame)
    {
        var decoder = this.EnsureLoaded();
        this._position = Math.Clamp(frame, 0, this.TotalFrames);
        decoder.Seek(this._position);
    }

    /// <summary>
    /// Decodes into <paramref name="destination"/>, interleaved, up to the end of the track:
    /// returns the frames written, 0 at the end.
    /// </summary>
    public int Read(Span<short> destination)
    {
        var decoder = this.EnsureLoaded();
        int frames = decoder.Read(destination);
        this._position += frames;
        return frames;
    }

    /// <summary>
    /// Decodes into <paramref name="destination"/> as bytes of 16-bit PCM: returns the bytes
    /// written, 0 at the end.
    /// </summary>
    public int ReadPcm16(Span<byte> destination) =>
        this.Read(MemoryMarshal.Cast<byte, short>(destination)) * this.Channels * sizeof(short);

    /// <summary>Drops the file and the decoder; the next read or seek loads them again, where this left off.</summary>
    public void Release()
    {
        this._decoder?.Dispose();
        this._decoder = null;
    }

    private OpusDecoder EnsureLoaded()
    {
        if (this._decoder is { } loaded)
        {
            return loaded;
        }

        byte[] file;
        using (var stream = this._open())
        {
            file = ReadAll(stream);
        }

        var decoder = new OpusDecoder(file);
        if (decoder.Channels is < 1 or > 2)
        {
            decoder.Dispose();
            throw new InvalidDataException($"Music is mono or stereo; this track has {decoder.Channels} channels.");
        }

        this._decoder = decoder;
        this.Channels = decoder.Channels;
        this.TotalFrames = decoder.Frames ?? 0;
        this._position = Math.Min(this._position, this.TotalFrames);
        decoder.Seek(this._position);
        return decoder;
    }
}
