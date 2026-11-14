// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Encodes a sound effect (or a concatenated sound bank): read, resampled and
/// mixed by the build tool, then written as WAV, FLAC or Opus by it, or as AAC by Apple's encoder (see
/// <see cref="AudioEncoder"/>).
/// </summary>
internal static class SoundCommand
{
    public const string Name = "sound";
    public const string ErrorCode = "SND0001";

    public static void Run(CommandLineOptions options)
    {
        // Used only by AAC
        string afconvert = options.GetToolPath("afconvert", "afconvert");
        string codec = options.GetChoice("codec", null, "flac", "pcm_s16le", "libopus", "aac_at");
        string compression = options.GetChoice("compression", null, "Lossless", "Lossy");
        string inputFile = options.GetPath("input");
        string outputFile = options.GetPath("output");
        int quality = options.GetInt32("quality", 8, 1, 10);
        int sampleRate = options.GetInt32("sample-rate", 44100, 1);

        // 0 preserves the source channel count, so a mono sound effect stays mono
        // (halving its decoded size).  Callers that need a fixed layout, such as the
        // sound bank concatenation, pass an explicit count.
        int channels = options.GetInt32("channels", 0, 0, 2);
        options.ThrowIfUnrecognized();

        bool lossless = AudioEncoder.IsLossless(codec);
        if (lossless != (compression == "Lossless"))
        {
            throw new ContentBuildException($"Codec '{codec}' is {(lossless ? "lossless" : "lossy")}-only; --compression must be {(lossless ? "Lossless" : "Lossy")}.");
        }

        // Opus only operates at 48 kHz
        if (codec == "libopus")
        {
            sampleRate = 48000;
        }

        AudioEncoder.ThrowIfUnavailable(codec);
        PcmAudio audio = AudioEncoder.Prepare(AudioReader.Read(inputFile), sampleRate, channels);
        AudioEncoder.Encode(codec, quality, audio, inputFile, outputFile, afconvert);
    }
}
