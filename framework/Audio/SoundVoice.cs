// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.Audio;

/// <summary>
/// A pre-allocated player for a single <see cref="Audio.Sound"/>.
/// </summary>
/// <remarks>
/// <para>
/// A voice owns the platform-specific resources required to play its
/// sound repeatedly without managed-side allocation. The cost of
/// engaging the platform is paid once at
/// <see cref="AudioService.CreateVoice(Audio.Sound)"/>; subsequent
/// calls to <see cref="Play"/>, <see cref="Stop"/>, and
/// <see cref="Pause"/> drive the underlying resource directly.
/// </para>
/// <para>
/// A voice plays one sound cue. To play multiple cues, or to play
/// the same cue with overlapping instances, create one voice per
/// concurrent instance. A fixed-size ring of voices bounds how many
/// instances of a cue play at once.
/// </para>
/// <para>
/// Voices are owned by the caller and must be disposed when no
/// longer needed; disposing a voice stops playback and releases its
/// platform resources.
/// </para>
/// </remarks>
public sealed class SoundVoice : IDisposable
{
    private bool _isPaused;

    private readonly Strategy<IVoiceStrategy> _strategy;
    private readonly Sound _sound;
    private VolumeControl? _volumeControl;
    private bool _isLooping;

    internal SoundVoice(
        Strategy<IVoiceStrategy> strategy,
        Sound sound)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(sound);

        this._strategy = strategy;
        this._sound = sound;
        sound.AddVoice(this);
    }

    /// <summary>
    /// Whether the voice is paused, from <see cref="Pause"/> on a voice that's playing until
    /// <see cref="Resume"/>, <see cref="Play"/> or <see cref="Stop"/>.
    /// </summary>
    public bool IsPaused => this._isPaused && (!this._sound.IsDisposed);

    /// <summary>
    /// <c>true</c> while the voice is actively producing audio.
    /// Transitions to <c>false</c> when playback reaches the end of
    /// the cue, when <see cref="Stop"/> is called, or when
    /// <see cref="Pause"/> is called.
    /// </summary>
    public bool IsPlaying
    {
        get
        {
            if (this._strategy.IsDisposed)
            {
                return false;
            }

            return this._strategy.Interface.IsPlaying;
        }
    }

    /// <summary>
    /// An optional volume control that drives this voice's gain.
    /// Share one control across voices to group their volume, or
    /// create a dedicated control to vary one voice independently.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The control has been disposed.</exception>
    public VolumeControl? VolumeControl
    {
        get => this._volumeControl;
        set
        {
            value?.AddUser();

            var previous = this._volumeControl;
            this._volumeControl = value;
            this._strategy.Interface.SetVolumeControl(value?.Strategy);

            previous?.RemoveUser();
        }
    }

    /// <summary>
    /// Whether the last <see cref="Play"/> asked the cue to start over when it reaches its end.
    /// </summary>
    public bool IsLooping => this._isLooping;

    /// <summary>
    /// Plays the voice's sound cue from the beginning, whatever it was doing.
    /// <see cref="Resume"/> carries on from a pause.
    /// </summary>
    /// <param name="loop"><c>true</c> if the cue should start over when it reaches its end, for an
    /// engine's hum or ambience, playing until <see cref="Stop"/>.</param>
    /// <exception cref="ObjectDisposedException">The voice's sound has been disposed.</exception>
    public void Play(
        bool loop = false)
    {
        ObjectDisposedException.ThrowIf(this._sound.IsDisposed, this._sound);

        // NOTE: A platform's voice resumes when paused
        if (this._isPaused)
        {
            this._isPaused = false;
            this._strategy.Interface.Stop();
        }

        // NOTE: Most cues never loop, and on the web this crosses into JavaScript
        if (loop != this._isLooping)
        {
            this._isLooping = loop;
            this._strategy.Interface.SetLooping(loop);
        }

        this._strategy.Interface.Play();
    }

    /// <summary>
    /// Carries on from where <see cref="Pause"/> stopped. Does nothing unless the voice is paused.
    /// </summary>
    public void Resume()
    {
        if (this._isPaused && !this._sound.IsDisposed)
        {
            this._isPaused = false;
            this._strategy.Interface.Play();
        }
    }

    /// <summary>
    /// Stops playback (if active) and resets the play position to
    /// the beginning. Safe to call when already stopped.
    /// </summary>
    public void Stop()
    {
        // NOTE: Disposing the sound stopped its voices, and platforms free what a stop may use
        if (this._sound.IsDisposed)
        {
            return;
        }

        this._isPaused = false;
        this._strategy.Interface.Stop();
    }

    /// <summary>
    /// Pauses playback at the current position, for <see cref="Resume"/>. Safe to call when
    /// already paused or stopped.
    /// </summary>
    public void Pause()
    {
        if (this._sound.IsDisposed)
        {
            return;
        }

        if (this.IsPlaying)
        {
            this._isPaused = true;
        }

        this._strategy.Interface.Pause();
    }

    /// <summary>Stops the voice and releases it.</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this._sound.RemoveVoice(this);
        this._strategy.Dispose();

        this._volumeControl?.RemoveUser();
        this._volumeControl = null;
    }
}
