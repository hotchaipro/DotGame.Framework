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

internal sealed class MusicLoader : IContentLoader<Music>
{
    private readonly ContentServiceStrategy _contentService;

    public MusicLoader(
        ContentServiceStrategy contentService)
    {
        this._contentService = contentService;
    }

    public async Task<Music> LoadAsync(
        string contentKey)
    {
        // Resolve the logical content key (no codec extension) to the
        // actual relative path produced by the build pipeline.
        var fullPath = this._contentService.ResolveContentPath(contentKey);

        Trace.Write(0x24c7fe8a, $"[MusicLoader] Loading '{fullPath}'");

        // Music is Ogg Opus, decoded by the shared decoder: opened on the playback thread's first
        // seek, not here
        Stream Open() => new AssetStream(
            AssetStream.GetNativeAssetManager(),
            this._contentService.GetFullPath(fullPath),
            AssetStream.AASSET_MODE_RANDOM);

        TimeSpan duration;
        int channels;
        long totalFrames;

        // When the manifest describes the track, loading reads nothing
        if (this._contentService.Manifest.TryGet(contentKey, out var entry)
            && entry.TryGetMusicDuration(out duration)
            && entry.TryGetMusicFormat(out _, out channels))
        {
            totalFrames = (long)(duration.TotalSeconds * OpusMusicReader.SampleRate);
        }
        else
        {
            byte[] file;
            using (var stream = await this._contentService.OpenStreamAsync(fullPath).ConfigureAwait(false))
            using (var memory = new MemoryStream())
            {
                await stream.CopyToAsync(memory).ConfigureAwait(false);
                file = memory.ToArray();
            }

            (channels, totalFrames) = OpusMusicReader.Probe(file);
            duration = TimeSpan.FromSeconds((double)totalFrames / OpusMusicReader.SampleRate);
        }

        var decoder = new OpusMusicReader(Open, channels, totalFrames);

        Trace.Write(0xac5d5806,
            $"[MusicLoader] Loaded music ({duration.TotalSeconds:F1}s, {OpusMusicReader.SampleRate}Hz, {channels}ch)");

        var androidMusic = new MusicStrategy(
            decoder, OpusMusicReader.SampleRate, channels, duration, this._contentService.MusicPlaybackListener);

        return new Music(new Strategy<IMusicStrategy>(androidMusic));
    }
}
