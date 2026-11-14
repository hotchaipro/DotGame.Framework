// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.WebGL.Audio;

[SupportedOSPlatform("browser")]
internal sealed class AudioServiceStrategy : IAudioServiceStrategy
{
    internal AudioServiceStrategy()
    {
    }

    public Task OnInitialize()
    {
        AudioBindings.InitializeAudio(FeatureSwitches.TraceAudio);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Strategy<IVolumeControlStrategy> MasterVolumeControl => this._master ??= new Strategy<IVolumeControlStrategy>(new VolumeControlStrategy(AudioBindings.GetMasterVolumeControl()));

    private Strategy<IVolumeControlStrategy>? _master;

    /// <inheritdoc />
    public Strategy<IVolumeControlStrategy> CreateVolumeControl()
    {
        var nativeHandle = AudioBindings.CreateVolumeControl();
        return new Strategy<IVolumeControlStrategy>(new VolumeControlStrategy(nativeHandle));
    }

    /// <inheritdoc />
    public Strategy<IVoiceStrategy> CreateVoice(
        Strategy<ISoundStrategy> sound,
        TimeSpan offset,
        TimeSpan duration)
    {
        var voice = new VoiceStrategy(
            sound.ToConcrete<SoundStrategy>(),
            offset,
            duration);
        return new Strategy<IVoiceStrategy>(voice);
    }

    /// <inheritdoc />
    public bool ShouldPlayMusic() => true;
}
