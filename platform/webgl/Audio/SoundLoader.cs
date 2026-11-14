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
internal sealed class SoundLoader : IContentLoader<Sound>
{
    private readonly ContentServiceStrategy _contentService;

    public SoundLoader(
        ContentServiceStrategy contentService)
    {
        this._contentService = contentService;
    }

    public async Task<Sound> LoadAsync(
        string contentKey)
    {
        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3d5, $"[SoundTrace][Web] SoundLoader.Load ENTER: '{contentKey}'");
        }

        var jsAudioBuffer = AudioBindings.CreateAudioBuffer();

        var url = this._contentService.GetVersionedUrl(
            this._contentService.ResolveContentPath(contentKey));

        await AudioBindings.LoadAudioBufferFromContent(
            jsAudioBuffer,
            url).ConfigureAwait(false);

        var durationSeconds = AudioBindings.GetAudioBufferDuration(jsAudioBuffer);

        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3d6, $"[SoundTrace][Web] SoundLoader.Load DONE: '{contentKey}' url='{url}' duration={durationSeconds:F3}s");
        }

        var soundStrategy = new SoundStrategy(jsAudioBuffer, TimeSpan.FromSeconds(durationSeconds), contentKey);

        return new Sound(new Strategy<ISoundStrategy>(soundStrategy));
    }
}
