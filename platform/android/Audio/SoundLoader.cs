// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;
using DotGame.Framework.Android.Content;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;

namespace DotGame.Framework.Android.Audio;

internal sealed class SoundLoader : IContentLoader<Sound>
{
    private readonly ContentServiceStrategy _contentService;

    public SoundLoader(
        ContentServiceStrategy contentService)
    {
        this._contentService = contentService;
    }

    // NOTE: Few at a time, so a bank loaded in parallel doesn't take every core while the game starts
    private static readonly System.Threading.SemaphoreSlim s_decodeLimit = new(3);

    private static async Task<T> RunDecode<T>(
        Func<T> decode)
    {
        await s_decodeLimit.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(decode).ConfigureAwait(false);
        }
        finally
        {
            s_decodeLimit.Release();
        }
    }

    public async Task<Sound> LoadAsync(
        string contentKey)
    {
        // Resolve the logical content key (no codec extension) to the
        // actual relative path produced by the build pipeline, which
        // may carry a .flac or .wav (lossless) or .opus (lossy) extension.
        var resolvedPath = this._contentService.ResolveContentPath(contentKey);

        // Load audio data from assets
        byte[] audioData;
        using (var stream = await this._contentService.OpenStreamAsync(resolvedPath).ConfigureAwait(false))
        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms).ConfigureAwait(false);
            audioData = ms.ToArray();
        }

        // Decode to 16-bit signed little-endian interleaved PCM at load
        // time, on a thread-pool worker. Decoding a full sound bank can
        // take most of a second; running it via Task.Run keeps that
        // work off the render/game thread so it can never stall the render
        // loop or a lifecycle transition. Doing this once means cues that
        // share the buffer (e.g. sound-bank segments) cost only an
        // offset+length into the shared samples at play time — no
        // re-decode, no per-cue copy.
        var bufferStrategy = await RunDecode(() =>
        {
            // WAV, FLAC or Ogg Opus, decoded by DotGame.AudioCodecs, so a sound has the same
            // samples as on desktop
            byte[] pcm;
            int sampleRate;
            int channels;
            try
            {
                (pcm, sampleRate, channels) = SoundDecoder.Decode(audioData);
            }
            catch (InvalidDataException error)
            {
                throw new InvalidDataException(
                    $"Couldn't decode the sound '{resolvedPath}': {error.Message}", error);
            }

            int bytesPerSecond = sampleRate * channels * 2;
            if (bytesPerSecond <= 0)
            {
                throw new InvalidDataException(
                    $"Failed to determine format for sound '{resolvedPath}'.");
            }

            var duration = TimeSpan.FromSeconds((double)pcm.Length / bytesPerSecond);
            if (duration <= TimeSpan.Zero)
            {
                throw new InvalidDataException(
                    $"Failed to determine duration for sound '{resolvedPath}'.");
            }

            // Convert once to the float samples the mixer adds up, at
            // the output rate, so playing them needs no conversion
            var samples = PcmConverter.ToFloat(
                pcm,
                channels,
                sampleRate,
                AudioOutput.SampleRate,
                out int outputChannels);

            return new MixerSoundStrategy(samples, outputChannels, duration);
        }).ConfigureAwait(false);

        return new Sound(new Strategy<ISoundStrategy>(bufferStrategy));
    }
}
