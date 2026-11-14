// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Globalization;

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Encodes AAC with macOS's own encoder and file writer: the build tool writes the prepared audio
/// (see <see cref="AudioEncoder.Prepare"/>) as a float WAV, and <c>afconvert</c> encodes that with
/// AudioToolbox into an .m4a.
/// </summary>
/// <remarks>
/// <para>
/// Not ffmpeg's <c>aac_at</c>, which uses the same encoder: its last packet keeps a whole frame, so the
/// file's edit list runs up to 1,023 samples past the end of the audio, and iOS, which loops music at
/// <c>AVAudioFile.Length</c>, would play that much silence at every loop. Apple's writer records the
/// exact length (found 2026-11-07: a 37,485-frame source read back as 37,888 frames from
/// <c>aac_at</c>, 37,485 from <c>afconvert</c>).
/// </para>
/// <para>
/// Constant bitrate on <see cref="AudioEncoder"/>'s AAC ladder, at the encoder's best quality. Mono stops
/// at 256 kbps, which is AudioToolbox's limit. <c>afconvert</c> stamps the time into the movie, track
/// and media headers, so they're zeroed: an encode depends only on the audio.
/// </para>
/// </remarks>
internal static class AppleAacEncoder
{
    public const int MonoBitrateLimit = 256;

    public static int Bitrate(int quality, int channels) => channels == 1 ? Math.Min(AudioEncoder.AacBitrate(quality), MonoBitrateLimit) : AudioEncoder.AacBitrate(quality);

    /// <param name="inputFile">The source's path, for messages.</param>
    public static void Encode(string afconvert, PcmAudio audio, string inputFile, string outputFile, int quality)
    {
        AudioEncoder.ThrowIfUnavailable("aac_at");
        ContentPaths.CreateParentDirectory(outputFile);

        string decoded = outputFile + ".decoded.wav";
        try
        {
            WavWriter.WriteFloat32(decoded, audio);

            int bitrate = Bitrate(quality, audio.Channels);
            ExternalProcess.Run(
                afconvert,
                ["-f", "m4af", "-d", "aac", "-b", Format(bitrate * 1000), "-s", "0", "-q", "127", decoded, outputFile],
                $"afconvert encode of '{inputFile}'");
            ClearTimestamps(outputFile);
        }
        finally
        {
            File.Delete(decoded);
        }
    }

    // The creation and modification times of mvhd, tkhd and mdhd: 4 bytes each in version 0, 8 in version 1
    internal static void ClearTimestamps(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        ClearTimestamps(file, 0, file.Length);
        File.WriteAllBytes(path, file);
    }

    private static void ClearTimestamps(byte[] file, int start, int end)
    {
        int at = start;
        while (at + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            if ((size < 8) || (at + size > end))
            {
                throw new ContentBuildException("afconvert wrote an MP4 box this build can't read.");
            }
            string type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            int body = at + 8;
            if (type is "moov" or "trak" or "mdia")
            {
                ClearTimestamps(file, body, at + (int)size);
            }
            else if (type is "mvhd" or "tkhd" or "mdhd")
            {
                int length = file[body] == 1 ? 16 : 8;
                Array.Clear(file, body + 4, length);
            }
            at += (int)size;
        }
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
