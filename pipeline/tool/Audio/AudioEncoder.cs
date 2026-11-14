// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Turns a source sound into one of the platforms' formats. The build tool reads the source, resamples
/// and mixes it, and writes WAV, FLAC and Opus with DotGame.AudioCodecs (<see cref="WavWriter"/>,
/// <see cref="FlacEncoder"/>, <see cref="OpusFileEncoder"/>). AAC is Apple's encoder (see
/// <see cref="AppleAacEncoder"/>).
/// </summary>
/// <remarks>
/// The normalized 1..10 content quality means the same on every platform:
/// <code>
///   flac        lossless; quality is ignored
///   pcm_s16le   lossless; quality is ignored
///   libopus     Q -> the Opus ladder, VBR       (48 .. 256 kbps, Q=5 = 112 kbps); Ogg, or CAF for iOS
///   aac_at      Q -> the AAC ladder, CBR        (64 .. 320 kbps, Q=5 = 160 kbps), macOS only
/// </code>
/// AAC is Apple's encoder (AudioToolbox, through <c>afconvert</c>): no AAC encoder ships with DotGame,
/// because of the patents. Only iOS uses AAC, and iOS builds always run on a Mac.
/// </remarks>
public static class AudioEncoder
{
    /// <summary>
    /// AAC bitrates in kbps, indexed by Quality - 1.  AudioToolbox takes a bitrate rather
    /// than a quality scale; a table keeps every step on a round, conventional bitrate.
    /// Q=5 is 160 kbps, the usual default for stereo AAC.  Every step is a bitrate
    /// AudioToolbox accepts for stereo at 44.1 and 48 kHz; for mono it stops at 256 kbps,
    /// so Q=9 and Q=10 give a mono source 256.
    /// </summary>
    private static readonly int[] AacBitrateLadder = [64, 80, 96, 128, 160, 192, 224, 256, 288, 320];

    /// <summary>
    /// Opus bitrates in kbps, indexed by Quality - 1.  Opus is more efficient than
    /// AAC at the same bitrate, so the ladder sits lower: Q=5 is 112
    /// kbps, at or beyond transparency for stereo music.
    /// </summary>
    private static readonly int[] OpusBitrateLadder = [48, 64, 80, 96, 112, 128, 160, 192, 224, 256];

    public static bool IsLossless(string codec) => codec is "flac" or "pcm_s16le";

    public static int AacBitrate(int quality) => AacBitrateLadder[quality - 1];

    /// <summary>The Opus bitrate, in bits a second, of <paramref name="quality"/>.</summary>
    public static int OpusBitrate(int quality) => OpusBitrateLadder[quality - 1] * 1000;

    /// <summary>
    /// Throws when <paramref name="codec"/>'s encoder can't run on this machine: AAC is
    /// AudioToolbox's, which only macOS has.
    /// </summary>
    public static void ThrowIfUnavailable(string codec)
    {
        if (codec == "aac_at" && !OperatingSystem.IsMacOS())
        {
            throw new ContentBuildException("AAC (iOS sounds and music) is encoded with macOS's own encoder, AudioToolbox, so iOS content builds only on a Mac.");
        }
    }

    /// <summary>
    /// <paramref name="source"/> at <paramref name="sampleRate"/>, with <paramref name="channels"/>
    /// channels (0 keeps the source's). Mixing down comes before resampling and mixing up after, so
    /// the resampler has the fewer channels to filter.
    /// </summary>
    public static PcmAudio Prepare(PcmAudio source, int sampleRate, int channels)
    {
        if ((channels > 0) && (channels < source.Channels))
        {
            return source.WithChannels(channels).WithSampleRate(sampleRate);
        }

        PcmAudio resampled = source.WithSampleRate(sampleRate);
        return channels > 0 ? resampled.WithChannels(channels) : resampled;
    }

    /// <summary>Writes <paramref name="audio"/> to <paramref name="outputFile"/> as <paramref name="codec"/>.</summary>
    /// <param name="inputFile">The source's path, for messages.</param>
    public static void Encode(string codec, int quality, PcmAudio audio, string inputFile, string outputFile, string afconvert)
    {
        ThrowIfUnavailable(codec);
        switch (codec)
        {
            case "pcm_s16le":
                WavWriter.WriteInt16(outputFile, audio);
                break;
            case "flac":
                FlacEncoder.Write(outputFile, audio);
                break;
            case "aac_at":
                AppleAacEncoder.Encode(afconvert, audio, inputFile, outputFile, quality);
                break;
            case "libopus":
                byte[] ogg = OpusFileEncoder.Encode(audio, OpusBitrate(quality));
                if (Path.GetDirectoryName(Path.GetFullPath(outputFile)) is { } folder)
                {
                    Directory.CreateDirectory(folder);
                }

                // iOS: the same packets, in the container AVAudioFile reads
                File.WriteAllBytes(outputFile, outputFile.EndsWith(".caf", StringComparison.OrdinalIgnoreCase) ? CafOpusWriter.Convert(ogg) : ogg);
                break;
            default:
                throw new ContentBuildException($"Unsupported audio codec '{codec}'.");
        }
    }
}
