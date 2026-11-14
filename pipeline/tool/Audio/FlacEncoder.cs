// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using CodecsFlacEncoder = DotGame.AudioCodecs.Flac.FlacEncoder;
using CodecsFlacEncoderOptions = DotGame.AudioCodecs.Flac.FlacEncoderOptions;

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Encodes 16-bit audio as FLAC (RFC 9639) with DotGame.AudioCodecs, at flac's level 8 (<c>-8</c>), its
/// most compact, with no padding.
/// </summary>
/// <remarks>
/// The file has STREAMINFO, with the samples' MD5, and the comment block with the library's vendor
/// string, which names its version. The encoder adds products in a fixed order, so the same audio
/// encodes to the same bytes on every build machine; a new version of the library can change them,
/// which a content version bump covers (ContentVersions.props).
/// </remarks>
public static class FlacEncoder
{
    private static readonly CodecsFlacEncoderOptions Options = new() { CompressionLevel = 8, Padding = 0 };

    public static void Write(string path, PcmAudio audio)
    {
        byte[] file = Encode(audio);
        ContentPaths.CreateParentDirectory(path);
        ContentPaths.RetryWhileLocked(() => File.WriteAllBytes(path, file), $"Writing '{path}'");
    }

    /// <summary>The FLAC file of <paramref name="audio"/>, at 16 bits.</summary>
    public static byte[] Encode(PcmAudio audio)
    {
        var file = new MemoryStream();
        using (var encoder = new CodecsFlacEncoder(file, audio.SampleRate, audio.Channels, Options, leaveOpen: true))
        {
            encoder.Write(audio.Samples);
            encoder.Complete();
        }

        return file.ToArray();
    }

    /// <summary>Encodes interleaved 16-bit samples.</summary>
    public static byte[] Encode(short[] interleaved, int channels, int sampleRate)
    {
        var file = new MemoryStream();
        using (var encoder = new CodecsFlacEncoder(file, sampleRate, channels, Options, leaveOpen: true))
        {
            encoder.Write(interleaved);
            encoder.Complete();
        }

        return file.ToArray();
    }
}
