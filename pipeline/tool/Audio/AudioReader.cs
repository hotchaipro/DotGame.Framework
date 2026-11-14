// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using AudioCodecsDecoder = DotGame.AudioCodecs.AudioDecoder;

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Reads a source sound: WAV, FLAC or Ogg Opus, mono or stereo. Anything else fails with an error
/// naming the format.
/// </summary>
/// <remarks>
/// <para>
/// Every format is read by DotGame.AudioCodecs, the codecs desktop and Android decode sounds with, as
/// float samples: WAV's integer PCM of 8 to 32 bits and IEEE float of 32 or 64 bits, plain or
/// <c>WAVE_FORMAT_EXTENSIBLE</c>; FLAC of any width; Ogg Opus, always at 48 kHz.
/// </para>
/// <para>
/// WAV and FLAC are a game's lossless masters, and Ogg Opus its compressed sources. Ogg Vorbis, MP3
/// and AAC aren't read (decided by the user, 2026-11-08): a lossy source would be compressed twice, and
/// each would need a decoder of its own.
/// </para>
/// </remarks>
public static class AudioReader
{
    public const string SupportedFormats = "WAV, FLAC or Ogg Opus";

    public static PcmAudio Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new ContentBuildException($"Source audio file not found: '{path}'");
        }

        byte[] file = ContentPaths.RetryWhileLocked(() => File.ReadAllBytes(path), $"Reading '{path}'");
        return Read(file, path);
    }

    public static PcmAudio Read(byte[] file, string path)
    {
        if (!IsWav(file) && !IsFlac(file) && !IsOggOpus(file))
        {
            throw Unsupported(path, Describe(file));
        }

        try
        {
            using AudioCodecsDecoder decoder = AudioCodecsDecoder.Open(file);
            if (decoder.Channels is < 1 or > 2)
            {
                throw new ContentBuildException($"'{path}' has {decoder.Channels} channels: sources are mono or stereo.");
            }

            return new PcmAudio(decoder.SampleRate, decoder.Channels, ReadAll(decoder));
        }
        catch (InvalidDataException error)
        {
            throw new ContentBuildException($"Couldn't read '{path}': {error.Message}");
        }
    }

    // Every sample, interleaved
    private static float[] ReadAll(AudioCodecsDecoder decoder)
    {
        if (decoder.Frames is { } frames)
        {
            var samples = new float[checked((int)(frames * decoder.Channels))];
            int read = decoder.Read(samples);
            return read == frames ? samples : samples[..(read * decoder.Channels)];
        }

        var all = new List<float>();
        var chunk = new float[4096 * decoder.Channels];
        int count;
        while ((count = decoder.Read(chunk)) > 0)
        {
            all.AddRange(chunk.AsSpan(0, count * decoder.Channels));
        }
        return [.. all];
    }

    private static bool IsWav(ReadOnlySpan<byte> file) =>
        (file.Length >= 12) && file[..4].SequenceEqual("RIFF"u8) && file[8..12].SequenceEqual("WAVE"u8);

    private static bool IsFlac(ReadOnlySpan<byte> file) =>
        (file.Length >= 4) && file[..4].SequenceEqual("fLaC"u8);

    private static bool IsOggOpus(ReadOnlySpan<byte> file) =>
        (file.Length >= 36) && file[..4].SequenceEqual("OggS"u8) && file[28..36].SequenceEqual("OpusHead"u8);

    // What a file that isn't one of the three is, for the error
    internal static string Describe(ReadOnlySpan<byte> file)
    {
        if ((file.Length >= 35) && file[..4].SequenceEqual("OggS"u8) && file[28..35].SequenceEqual("\u0001vorbis"u8))
        {
            return "Ogg Vorbis";
        }
        if ((file.Length >= 4) && file[..4].SequenceEqual("OggS"u8))
        {
            return "an Ogg file that isn't Opus";
        }
        if (((file.Length >= 3) && file[..3].SequenceEqual("ID3"u8)) || ((file.Length >= 2) && (file[0] == 0xFF) && ((file[1] & 0xE0) == 0xE0)))
        {
            return "MP3";
        }
        if ((file.Length >= 8) && file[4..8].SequenceEqual("ftyp"u8))
        {
            return "MP4 (AAC or ALAC)";
        }
        if ((file.Length >= 4) && (file[..4].SequenceEqual("FORM"u8)))
        {
            return "AIFF";
        }
        if ((file.Length >= 4) && (file[..4].SequenceEqual("RF64"u8) || file[..4].SequenceEqual("RIFX"u8)))
        {
            return "a WAV variant (RF64 or RIFX)";
        }

        return "not a sound format the build knows";
    }

    private static ContentBuildException Unsupported(string path, string format) =>
        new($"'{path}' is {format}, which the content pipeline doesn't read. Sources are {SupportedFormats}: convert it, from its lossless master if there is one.");
}
