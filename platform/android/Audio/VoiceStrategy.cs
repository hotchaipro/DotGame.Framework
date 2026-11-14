// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;

namespace DotGame.Framework.Android.Audio;

/// <summary>
/// One Android voice: a segment of a sound, a play position and a
/// volume, mixed by <see cref="AudioMixer"/>. A voice owns no
/// platform resources, so a game can create as many as it likes.
/// </summary>
/// <remarks>
/// <para>
/// The game thread drives a voice through generation counters, and the
/// audio thread applies them at the start of its next block, so neither
/// side locks and <see cref="Play"/> allocates nothing:
/// </para>
/// <list type="bullet">
/// <item><see cref="Play"/> bumps the play generation; the audio
/// thread restarts from the segment's start when it sees a new one.</item>
/// <item><see cref="Stop"/> marks the current generation stopped.</item>
/// <item>The audio thread marks a generation finished when it reaches
/// the segment's end.</item>
/// <item><see cref="Pause"/> holds the position until the next
/// <see cref="Play"/>.</item>
/// </list>
/// <para>
/// So <see cref="IsPlaying"/> turns true as soon as <see cref="Play"/>
/// returns, as on the other platforms, rather than when the audio
/// thread gets to it.
/// </para>
/// </remarks>
internal sealed class VoiceStrategy : IVoiceStrategy
{
    private readonly AudioMixer _mixer;
    private readonly float[] _samples;
    private readonly int _channels;
    private readonly int _startFrame;
    private readonly int _frameCount;

    // Written by the game thread, read by the audio thread
    private int _playGeneration;
    private int _stoppedGeneration;
    private volatile bool _isPaused;
    private volatile bool _isLooping;
    private volatile PolledVolumeControlStrategy? _volumeControl;

    // Written by the audio thread, read by the game thread
    private int _finishedGeneration;

    // The audio thread's own
    private int _mixedGeneration;
    private int _position;
    private float _gain;

    private bool _isDisposed;

    internal VoiceStrategy(
        AudioMixer mixer,
        SoundStrategy sound,
        int sampleRate,
        TimeSpan offset,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(sound);

        this._mixer = mixer;
        this._samples = sound.Samples;
        this._channels = sound.Channels;

        // Bake the (offset, duration) segment into a frame range of
        // the sound's samples, which are already at the output rate
        int totalFrames = sound.FrameCount;
        this._startFrame = Math.Clamp((int)Math.Round(offset.TotalSeconds * sampleRate), 0, totalFrames);
        this._frameCount = Math.Clamp(
            (int)Math.Round(duration.TotalSeconds * sampleRate),
            0,
            totalFrames - this._startFrame);

        mixer.Register(this);
    }

    public bool IsPlaying
    {
        get
        {
            int generation = Volatile.Read(ref this._playGeneration);
            return !this._isPaused
                && (generation != Volatile.Read(ref this._stoppedGeneration))
                && (generation != Volatile.Read(ref this._finishedGeneration));
        }
    }

    public void SetVolumeControl(
        Strategy<IVolumeControlStrategy>? volumeControl)
    {
        this._volumeControl = volumeControl?.ToConcrete<PolledVolumeControlStrategy>();
    }

    public void SetLooping(
        bool isLooping)
    {
        this._isLooping = isLooping;
    }

    public void Play()
    {
        if (this._isDisposed)
        {
            return;
        }

        // Resume a paused voice where it was, unless it has since been
        // stopped
        if (this._isPaused)
        {
            this._isPaused = false;
            if (this.IsPlaying)
            {
                this._mixer.OnVoiceStarted();
                return;
            }
        }

        Volatile.Write(ref this._playGeneration, this._playGeneration + 1);
        this._mixer.OnVoiceStarted();
    }

    public void Stop()
    {
        Volatile.Write(ref this._stoppedGeneration, this._playGeneration);
        this._isPaused = false;
    }

    public void Pause()
    {
        if (this.IsPlaying)
        {
            this._isPaused = true;
        }
    }

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        this._isDisposed = true;
        this.Stop();
        this._mixer.Unregister(this);
    }

    /// <summary>
    /// Adds up to <paramref name="frames"/> frames of this voice into
    /// the interleaved stereo <paramref name="output"/>, on the audio
    /// thread. Returns <c>true</c> if the voice is still playing.
    /// </summary>
    internal bool MixInto(
        float[] output,
        int frames)
    {
        int generation = Volatile.Read(ref this._playGeneration);
        if (generation != this._mixedGeneration)
        {
            // A new Play: start from the top at the current volume
            this._mixedGeneration = generation;
            this._position = 0;
            this._gain = this.ReadVolume();
        }

        if ((generation == Volatile.Read(ref this._stoppedGeneration))
            || (generation == Volatile.Read(ref this._finishedGeneration))
            || this._isPaused)
        {
            return false;
        }

        // Ramp from the last block's volume to this one's across
        // the block, so volume changes and fades never click
        float gain = this._gain;
        float target = this.ReadVolume();
        float step = (target - gain) / frames;

        int mixed = 0;
        bool isPlaying = this._frameCount > 0;
        while (isPlaying && (mixed < frames))
        {
            int count = Math.Min(frames - mixed, this._frameCount - this._position);
            var samples = this._samples;
            int source = (this._startFrame + this._position) * this._channels;
            int first = mixed * 2;
            int last = first + (count * 2);

            if (this._channels == 2)
            {
                for (int i = first; i < last; i += 2, source += 2)
                {
                    output[i] += samples[source] * gain;
                    output[i + 1] += samples[source + 1] * gain;
                    gain += step;
                }
            }
            else
            {
                for (int i = first; i < last; i += 2, source++)
                {
                    float sample = samples[source] * gain;
                    output[i] += sample;
                    output[i + 1] += sample;
                    gain += step;
                }
            }

            mixed += count;
            this._position += count;

            if (this._position >= this._frameCount)
            {
                if (this._isLooping)
                {
                    this._position = 0;
                }
                else
                {
                    isPlaying = false;
                }
            }
        }

        this._gain = gain;

        if (!isPlaying)
        {
            Volatile.Write(ref this._finishedGeneration, generation);
            return false;
        }

        return true;
    }

    private float ReadVolume()
    {
        return PolledVolumeControlStrategy.ReadEffectiveVolume(this._volumeControl);
    }
}
