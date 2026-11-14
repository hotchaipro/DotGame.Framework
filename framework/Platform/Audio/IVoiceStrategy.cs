// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Platform.Audio;

/// <summary>
/// Platform-specific pre-allocated voice that plays one sound cue.
/// </summary>
/// <remarks>
/// <para>
/// A voice owns whatever platform resources are needed to play its
/// sound repeatedly without further managed-side allocation (e.g. an
/// <c>AVAudioPlayerNode</c> + scheduled <c>AVAudioPCMBuffer</c> on
/// iOS, a play position in the shared mixer on Android, a long-lived
/// <c>SDL_AudioStream</c> on desktop, a cached <c>GainNode</c> on
/// the web). <see cref="Play"/>, <see cref="Stop"/>, and
/// <see cref="Pause"/> drive the underlying resource directly.
/// </para>
/// <para>
/// Voices are created by
/// <see cref="IAudioServiceStrategy.CreateVoice"/> and disposed by
/// the public <see cref="DotGame.Framework.Audio.SoundVoice"/> wrapper.
/// </para>
/// </remarks>
internal interface IVoiceStrategy : IDisposable
{
    /// <summary>
    /// <c>true</c> while the voice is actively producing audio.
    /// Transitions to <c>false</c> when playback reaches the end of
    /// the cue, when <see cref="Stop"/> is called, or when
    /// <see cref="Pause"/> is called.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Attaches (or detaches, when <c>null</c>) a volume control
    /// that drives this voice's gain. Callers that want per-voice
    /// variation create a dedicated volume control for the voice;
    /// callers that want grouped control share one across voices.
    /// </summary>
    void SetVolumeControl(
        Strategy<IVolumeControlStrategy>? volumeControl);

    /// <summary>
    /// Sets whether the voice's cue starts over when it reaches its end. Turned off while playing,
    /// it plays to the end of the cue and finishes. <see cref="IsPlaying"/> stays <c>true</c> while
    /// it loops.
    /// </summary>
    void SetLooping(
        bool isLooping);

    /// <summary>
    /// Starts playback of the voice's sound cue from the beginning,
    /// or resumes playback if the voice was paused. If the voice is
    /// already playing the call restarts playback from the beginning.
    /// </summary>
    void Play();

    /// <summary>
    /// Stops playback (if active) and resets the play position to
    /// the beginning. Safe to call when the voice is already stopped.
    /// </summary>
    void Stop();

    /// <summary>
    /// Pauses playback at the current position. Safe to call when
    /// the voice is already paused or stopped.
    /// </summary>
    void Pause();
}
