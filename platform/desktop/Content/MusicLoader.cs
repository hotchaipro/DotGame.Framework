// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Desktop.Audio;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;

namespace DotGame.Framework.Desktop.Content;

internal sealed class MusicLoader : IContentLoader<Music>
{
    private readonly ContentServiceStrategy _contentService;

    public MusicLoader(
        ContentServiceStrategy contentService)
    {
        this._contentService = contentService;
    }

    public Task<Music> LoadAsync(
        string contentKey)
    {
        var resolvedPath = this._contentService.ResolveContentPath(contentKey);
        var fullPath = this._contentService.GetFullPath(resolvedPath);

        Trace.Write(0x655a168d, $"[MusicLoader] Loading '{fullPath}'");

        TimeSpan duration;
        int sampleRate;
        int channels;

        // Prefer the pipeline-precomputed metadata: when the manifest
        // carries the duration and audio format, loading performs no
        // I/O at all and the track starts in the metadata-only Loaded
        // state. Fall back to briefly opening the codec when absent.
        bool hasEntry = this._contentService.Manifest.TryGet(contentKey, out var entry);

        if (hasEntry
            && entry.TryGetMusicDuration(out var precomputedDuration)
            && entry.TryGetMusicFormat(out var precomputedSampleRate, out var precomputedChannels))
        {
            duration = precomputedDuration;
            sampleRate = precomputedSampleRate;
            channels = precomputedChannels;
        }
        else
        {
            var (probedChannels, frames) = OpusMusicReader.Probe(File.ReadAllBytes(fullPath));

            sampleRate = OpusMusicReader.SampleRate;
            channels = probedChannels;
            duration = (hasEntry && entry.TryGetMusicDuration(out var manifestDuration))
                ? manifestDuration
                : TimeSpan.FromSeconds((double)frames / sampleRate);
        }

        var stream = new MusicStrategy(fullPath, sampleRate, channels, duration, this._contentService.MusicPlaybackListener);
        var music = new Music(new Strategy<IMusicStrategy>(stream));

        Trace.Write(0xacd89dd7, $"[MusicLoader] Loaded music ({duration.TotalSeconds:F1}s, {sampleRate}Hz, {channels}ch)");

        return Task.FromResult(music);
    }
}
