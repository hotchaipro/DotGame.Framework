// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;
using DotGame.AudioCodecs;

namespace DotGame.Framework.Platform.Common.Audio;

/// <summary>
/// Decodes a whole sound, WAV, FLAC or Ogg Opus as the content pipeline writes them, to 16-bit
/// interleaved PCM, with DotGame.AudioCodecs: the same samples on desktop, Android and iOS (its lossless
/// sounds). The web decodes sounds in the browser.
/// </summary>
internal static class SoundDecoder
{
    /// <summary>
    /// <paramref name="file"/>'s samples as 16-bit little-endian interleaved PCM bytes, with its rate
    /// and channel count.
    /// </summary>
    /// <exception cref="InvalidDataException">The file isn't a sound this reads, or is damaged.</exception>
    public static (byte[] Pcm, int SampleRate, int Channels) Decode(byte[] file)
    {
        using AudioDecoder decoder = AudioDecoder.Open(file);
        int channels = decoder.Channels;

        // The pipeline's files all give their length; a FLAC file that doesn't is read in pieces
        if (decoder.Frames is { } frames)
        {
            long bytes = frames * channels * sizeof(short);
            if (bytes > Array.MaxLength)
            {
                throw new InvalidDataException($"The sound is too long to decode at once: {frames} frames.");
            }

            byte[] pcm = GC.AllocateUninitializedArray<byte>((int)bytes);
            int read = decoder.Read(MemoryMarshal.Cast<byte, short>(pcm.AsSpan()));
            if (read != frames)
            {
                throw new InvalidDataException($"The sound ended after {read} of its {frames} frames.");
            }

            return (pcm, decoder.SampleRate, channels);
        }

        using var whole = new MemoryStream();
        var chunk = new byte[4096 * channels * sizeof(short)];
        int count;
        while ((count = decoder.Read(MemoryMarshal.Cast<byte, short>(chunk.AsSpan()))) > 0)
        {
            whole.Write(chunk, 0, count * channels * sizeof(short));
        }

        byte[] result = GC.AllocateUninitializedArray<byte>((int)whole.Length);
        whole.GetBuffer().AsSpan(0, result.Length).CopyTo(result);
        return (result, decoder.SampleRate, channels);
    }
}
