// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using AVFoundation;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.iOS.Audio;
using DotGame.Framework.Platform.Audio;
using Foundation;

namespace DotGame.Framework.iOS.Content;

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
        // Resolve the logical content key (no codec extension) to the
        // actual relative path produced by the build pipeline.
        var resolvedPath = this._contentService.ResolveContentPath(contentKey);

        Trace.Write(0xf872db9b, $"[MusicLoader] Loading '{resolvedPath}'");

        var filePath = this._contentService.GetFullPath(resolvedPath);
        var fileUrl = NSUrl.FromFilename(filePath);

        TimeSpan duration;
        int sampleRate;
        int channels;

        // Prefer the pipeline-precomputed metadata: when the manifest
        // carries the duration and audio format, loading opens no
        // AVAudioFile at all and the track starts in the metadata-only
        // Loaded state. Fall back to briefly opening the file when
        // absent.
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
            using var probe = new AVAudioFile(fileUrl, out var error);

            if (error is not null)
            {
                throw new InvalidOperationException(
                    $"Failed to open music file '{resolvedPath}': {error.LocalizedDescription}");
            }

            var format = probe.ProcessingFormat;

            sampleRate = (int)format.SampleRate;
            channels = (int)format.ChannelCount;
            duration = (hasEntry && entry.TryGetMusicDuration(out var manifestDuration))
                ? manifestDuration
                : TimeSpan.FromSeconds(probe.Length / format.SampleRate);
        }

        var audioService = this._contentService.AudioService
            ?? throw new InvalidOperationException("The content service has no audio service.");
        var iosMusic = new MusicStrategy(fileUrl, sampleRate, duration, audioService);

        Trace.Write(0x21a7351a,
            $"[MusicLoader] Loaded music " +
            $"({duration.TotalSeconds:F1}s, " +
            $"{sampleRate}Hz, " +
            $"{channels}ch)");

        return Task.FromResult(
            new Music(new Strategy<IMusicStrategy>(iosMusic)));
    }
}
