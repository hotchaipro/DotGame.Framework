// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.WebGL.Content;

namespace DotGame.Framework.WebGL.Audio;

[SupportedOSPlatform("browser")]
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
        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3dc, $"[SoundTrace][Web] MusicLoader.Load ENTER: '{contentKey}'");
        }

        var jsAudioStream = AudioBindings.CreateAudioStream();

        var url = this._contentService.GetVersionedUrl(this._contentService.ResolveContentPath(contentKey));

        var hasEntry = this._contentService.Manifest.TryGet(contentKey, out var entry);

        // The channel count comes from the manifest, where the content
        // pipeline recorded it by probing the encoded artifact, so the
        // playback graph can be built with the right output channel count
        // without opening the file. Mono tracks stay mono.
        var channelCount = (hasEntry && entry.TryGetMusicFormat(out _, out var channels))
            ? channels
            : 0;

        // Registers the URL and format; deliberately does no I/O. Every
        // fetch and decode is deferred to AudioService.PrepareAsync or
        // AudioService.Play, which is what keeps loading a music track
        // free regardless of how large it is.
        AudioBindings.LoadAudioStreamFromContent(
            jsAudioStream,
            url,
            channelCount);

        // Prefer the pipeline-precomputed duration so loading the game
        // never blocks on decoding the track. Fall back to a JS-side
        // query only when the manifest does not carry a duration
        // (e.g. hand-authored mod content).
        TimeSpan duration;
        if (hasEntry
            && entry.TryGetMusicDuration(out var precomputed))
        {
            duration = precomputed;
        }
        else
        {
            var seconds = await AudioBindings.GetAudioStreamDuration(jsAudioStream).ConfigureAwait(false);
            duration = (double.IsFinite(seconds) && seconds >= 0.0)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
        }

        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3dd, $"[SoundTrace][Web] MusicLoader.Load DONE: '{contentKey}' url='{url}' duration={duration.TotalSeconds:F3}s");
        }

        var audioStream = new MusicStrategy(jsAudioStream, duration, contentKey);

        return new Music(new Strategy<IMusicStrategy>(audioStream));
    }
}
