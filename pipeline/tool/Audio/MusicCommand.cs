// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Encodes a music track to the platform's streaming music codec: read and resampled by the build
/// tool, then encoded as Opus by it, or as AAC by Apple's encoder (see <see cref="AudioEncoder"/>).
/// </summary>
/// <remarks>
/// Music is long, streamed and decoded per track rather than mixed from a shared
/// buffer, so the channel count is preserved (mono stays mono) and only the
/// sample rate is normalized.  The content manifest's channel count and sample
/// rate are probed from the encoded output by the build.
/// </remarks>
internal static class MusicCommand
{
    public const string Name = "music";
    public const string ErrorCode = "MUS0001";

    public static void Run(CommandLineOptions options)
    {
        string afconvert = options.GetToolPath("afconvert", "afconvert");
        string codec = options.GetChoice("codec", "libopus", "libopus", "aac_at");
        string inputFile = options.GetPath("input");
        string outputFile = options.GetPath("output");
        int quality = options.GetInt32("quality", 5, 1, 10);
        int sampleRate = options.GetInt32("sample-rate", 44100, 1);
        options.ThrowIfUnrecognized();

        // Opus only operates at 48 kHz, so callers do not need to know that codec detail
        if (codec == "libopus")
        {
            sampleRate = 48000;
        }

        AudioEncoder.ThrowIfUnavailable(codec);
        PcmAudio audio = AudioEncoder.Prepare(AudioReader.Read(inputFile), sampleRate, channels: 0);
        AudioEncoder.Encode(codec, quality, audio, inputFile, outputFile, afconvert);
    }
}
