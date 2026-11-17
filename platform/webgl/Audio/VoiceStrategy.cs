// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.WebGL.Audio;

/// <summary>
/// One pre-allocated WebGL voice. Owns a JavaScript-side voice
/// object (a cached <c>GainNode</c> already connected to either the
/// audio context's destination or an attached volume control's
/// gain node). Each <see cref="Play"/> creates a new
/// <c>AudioBufferSourceNode</c> on the JS side and routes it through
/// the cached gain — Web Audio source nodes are by design one-shot,
/// so a managed-side wrapper is not allocated per play.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class VoiceStrategy : IVoiceStrategy
{
    private JSObject? _voice;
    private readonly SoundStrategy _sound;
    private Strategy<IVolumeControlStrategy>? _volumeControl;

    internal VoiceStrategy(
        SoundStrategy sound,
        TimeSpan offset,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(sound);

        this._sound = sound;
        // Bake the (offset, duration) segment into the JS Voice once
        // at creation time. Subsequent PlayVoice calls take no
        // parameters, saving 16 bytes of JS/WASM marshaling per Play.
        this._voice = AudioBindings.CreateVoice(
            sound.AudioBuffer,
            offset.TotalSeconds,
            duration.TotalSeconds);
    }

    public bool IsPlaying
    {
        get
        {
            var voice = this._voice;
            return voice is not null && AudioBindings.IsVoicePlaying(voice);
        }
    }

    public void SetVolumeControl(
        Strategy<IVolumeControlStrategy>? volumeControl)
    {
        this._volumeControl = volumeControl;

        var voice = this._voice;
        if (voice is null)
        {
            return;
        }

        var vcHandle = volumeControl?.ToConcrete<VolumeControlStrategy>().NativeHandle;
        AudioBindings.SetVoiceVolumeControl(voice, vcHandle);
    }

    public void SetLooping(
        bool isLooping)
    {
        var voice = this._voice;
        if (voice is not null)
        {
            AudioBindings.SetVoiceLooping(voice, isLooping);
        }
    }

    public void SetPlaybackRate(
        float rate)
    {
        var voice = this._voice;
        if (voice is not null)
        {
            AudioBindings.SetVoicePlaybackRate(voice, rate);
        }
    }

    public void Play()
    {
        var voice = this._voice;
        if (voice is null)
        {
            return;
        }

        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3d7, $"[SoundTrace][Web] VoiceStrategy.Play: '{this._sound.Name}'");
        }

        AudioBindings.PlayVoice(voice);
    }

    public void Stop()
    {
        var voice = this._voice;
        if (voice is null)
        {
            return;
        }

        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3d8, $"[SoundTrace][Web] VoiceStrategy.Stop: '{this._sound.Name}'");
        }

        AudioBindings.StopVoice(voice);
    }

    public void Pause()
    {
        var voice = this._voice;
        if (voice is null)
        {
            return;
        }

        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3d9, $"[SoundTrace][Web] VoiceStrategy.Pause: '{this._sound.Name}'");
        }

        AudioBindings.PauseVoice(voice);
    }

    public void Dispose()
    {
        var voice = this._voice;
        if (voice is null)
        {
            return;
        }

        this._voice = null;
        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3da, $"[SoundTrace][Web] VoiceStrategy.Dispose: '{this._sound.Name}'");
        }
        AudioBindings.DestroyVoice(voice);
        voice.Dispose();
    }
}
