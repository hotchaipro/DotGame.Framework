// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.AudioCodecs.Opus;
using CodecsOpusEncoder = DotGame.AudioCodecs.Opus.OpusEncoder;

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Encodes 48 kHz audio as an Ogg Opus file (RFC 7845) with DotGame.AudioCodecs, whose encoder is a
/// port of libopus 1.6.1's fixed-point encoder (byte-identical to native libopus's), at opusenc's
/// settings: the audio application, complexity 10, unconstrained VBR, 20 ms frames.
/// </summary>
/// <remarks>
/// The pre-skip is the encoder's lookahead, the end is padded as opusenc pads it and trimmed again by
/// the last page's granule position, and pages hold up to a second of packets. The output depends only
/// on the samples, the bitrate and the library's version (in the vendor string): the stream serial
/// number is fixed, so the same source always gives the same bytes.
/// </remarks>
public static class OpusFileEncoder
{
    /// <summary>Opus always codes at 48 kHz.</summary>
    public const int SampleRate = 48000;

    /// <summary>Writes <paramref name="audio"/>, which must be at 48 kHz, to <paramref name="outputFile"/>.</summary>
    public static void Write(string outputFile, PcmAudio audio, int bitrate)
    {
        File.WriteAllBytes(outputFile, Encode(audio, bitrate));
    }

    /// <summary>The Ogg Opus file of <paramref name="audio"/>, which must be at 48 kHz.</summary>
    public static byte[] Encode(PcmAudio audio, int bitrate)
    {
        if (audio.SampleRate != SampleRate)
        {
            throw new ArgumentException($"Opus is encoded from 48 kHz audio, not {audio.SampleRate} Hz.", nameof(audio));
        }
        if (audio.Channels is < 1 or > 2)
        {
            throw new ContentBuildException($"Opus sounds and music are mono or stereo, not {audio.Channels} channels.");
        }

        var file = new MemoryStream();
        using (var encoder = new CodecsOpusEncoder(file, SampleRate, audio.Channels, new OpusEncoderOptions { Bitrate = bitrate }, leaveOpen: true))
        {
            encoder.Write(audio.Samples);
            encoder.Complete();
        }

        return file.ToArray();
    }
}
