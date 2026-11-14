// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Desktop.Audio;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;

namespace DotGame.Framework.Desktop.Content;

internal sealed class SoundLoader : IContentLoader<Sound>
{
    private readonly ContentServiceStrategy _contentService;

    public SoundLoader(
        ContentServiceStrategy contentService)
    {
        this._contentService = contentService;
    }

    public Task<Sound> LoadAsync(
        string contentKey)
    {
        var fullPath = this._contentService.GetFullPath(this._contentService.ResolveContentPath(contentKey));

        Trace.Write(0x63b70d2e, $"[SoundLoader] Loading '{fullPath}'");

        // Decode runs inline on whatever thread invoked the loader. A lossless sound is WAV or FLAC,
        // a lossy one Ogg Opus, decoded by DotGame.AudioCodecs as on Android.
        var (pcmData, freq, channels, duration) = DecodeFile(fullPath);

        var soundStrategy = new SoundStrategy(pcmData, freq, channels, duration);

        Trace.Write(0xbd787d8e, $"[SoundLoader] Loaded audio ({duration.TotalSeconds:F2}s, {freq}Hz, {channels}ch)");

        return Task.FromResult(new Sound(new Strategy<ISoundStrategy>(soundStrategy)));
    }

    private static (byte[] Pcm, int Freq, int Channels, TimeSpan Duration) DecodeFile(
        string fullPath)
    {
        var file = System.IO.File.ReadAllBytes(fullPath);

        // NOTE: On the pinned heap, as SDL reads it while voices play
        var (pcm, sampleRate, channels) = SoundDecoder.Decode(file, pinned: true);

        return (pcm, sampleRate, channels, TimeSpan.FromSeconds((double)(pcm.Length / (channels * sizeof(short))) / sampleRate));
    }
}
