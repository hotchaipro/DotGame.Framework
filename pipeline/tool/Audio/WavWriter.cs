// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.AudioCodecs.Wav;

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Writes WAV files with DotGame.AudioCodecs: 16-bit PCM, the platforms' lossless sound format, and
/// 32-bit float, the intermediate the external encoders read. Nothing but the format and the samples,
/// so a file depends only on its audio.
/// </summary>
public static class WavWriter
{
    public static void WriteInt16(string path, PcmAudio audio) => Write(path, audio, WavSampleFormat.Pcm16);

    /// <summary>IEEE float, which afconvert and ffmpeg both read.</summary>
    public static void WriteFloat32(string path, PcmAudio audio) => Write(path, audio, WavSampleFormat.Float32);

    private static void Write(string path, PcmAudio audio, WavSampleFormat format)
    {
        var file = new MemoryStream();
        using (var encoder = new WavEncoder(file, audio.SampleRate, audio.Channels, new WavEncoderOptions { SampleFormat = format }, leaveOpen: true))
        {
            encoder.Write(audio.Samples);
            encoder.Complete();
        }

        ContentPaths.CreateParentDirectory(path);
        ContentPaths.RetryWhileLocked(() => File.WriteAllBytes(path, file.ToArray()), $"Writing '{path}'");
    }
}
