// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.Platform.Common.Audio;

/// <summary>
/// One voice on a platform that mixes sounds itself (Android and
/// desktop): a segment of a sound, a play position and a volume, mixed
/// by <see cref="AudioMixer"/>. A voice owns no platform resources, so
/// a game can create as many as it likes.
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
/// <item><see cref="SetPlaybackRate"/> writes a rate the audio thread
/// reads once a block.</item>
/// </list>
/// <para>
/// So <see cref="IsPlaying"/> turns true as soon as <see cref="Play"/>
/// returns, as on the other platforms, rather than when the audio
/// thread gets to it.
/// </para>
/// </remarks>
internal sealed class MixerVoiceStrategy : IVoiceStrategy
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
    private volatile float _playbackRate = 1f;
    private volatile PolledVolumeControlStrategy? _volumeControl;

    // Written by the audio thread, read by the game thread
    private int _finishedGeneration;

    // The audio thread's own
    // (The position is in frames of the segment, with a fraction once the voice has played at
    // a rate other than 1. The two gains are the same: they're kept apart so a pan is a change
    // of what they're set to, not of the mixing.)
    private int _mixedGeneration;
    private double _position;
    private float _gainLeft;
    private float _gainRight;

    private bool _isDisposed;

    internal MixerVoiceStrategy(
        AudioMixer mixer,
        MixerSoundStrategy sound,
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

    public void SetPlaybackRate(
        float rate)
    {
        this._playbackRate = rate;
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
            this._gainLeft = this._gainRight = this.ReadVolume();
        }

        if ((generation == Volatile.Read(ref this._stoppedGeneration))
            || (generation == Volatile.Read(ref this._finishedGeneration))
            || this._isPaused)
        {
            return false;
        }

        // Ramp from the last block's volume to this one's across
        // the block, so volume changes and fades never click
        float target = this.ReadVolume();
        float stepLeft = (target - this._gainLeft) / frames;
        float stepRight = (target - this._gainRight) / frames;

        // The rate is read once a block. At 1, on a whole frame, the samples are copied as they
        // are; at any other rate, or once a rate has left the position between frames, they're
        // read through the interpolator.
        float rate = this._playbackRate;
        bool isPlaying = (this._frameCount > 0)
            && (((rate == 1f) && (this._position == Math.Floor(this._position)))
                ? this.MixAsRecorded(output, frames, stepLeft, stepRight)
                : this.MixResampled(output, frames, rate, stepLeft, stepRight));

        if (!isPlaying)
        {
            Volatile.Write(ref this._finishedGeneration, generation);
            return false;
        }

        return true;
    }

    private bool MixAsRecorded(
        float[] output,
        int frames,
        float stepLeft,
        float stepRight)
    {
        float gainLeft = this._gainLeft;
        float gainRight = this._gainRight;
        int position = (int)this._position;

        int mixed = 0;
        bool isPlaying = true;
        while (isPlaying && (mixed < frames))
        {
            int count = Math.Min(frames - mixed, this._frameCount - position);
            var samples = this._samples;
            int source = (this._startFrame + position) * this._channels;
            int first = mixed * 2;
            int last = first + (count * 2);

            if (this._channels == 2)
            {
                for (int i = first; i < last; i += 2, source += 2)
                {
                    output[i] += samples[source] * gainLeft;
                    output[i + 1] += samples[source + 1] * gainRight;
                    gainLeft += stepLeft;
                    gainRight += stepRight;
                }
            }
            else
            {
                for (int i = first; i < last; i += 2, source++)
                {
                    float sample = samples[source];
                    output[i] += sample * gainLeft;
                    output[i + 1] += sample * gainRight;
                    gainLeft += stepLeft;
                    gainRight += stepRight;
                }
            }

            mixed += count;
            position += count;

            if (position >= this._frameCount)
            {
                if (this._isLooping)
                {
                    position = 0;
                }
                else
                {
                    isPlaying = false;
                }
            }
        }

        this._position = position;
        this._gainLeft = gainLeft;
        this._gainRight = gainRight;

        return isPlaying;
    }

    /// <summary>
    /// Mixes the voice at a rate other than as recorded: each output frame is read from between
    /// the segment's frames with the 4-point interpolator the sounds are converted with, and the
    /// position steps on by the rate. The frames before the segment's first and after its last
    /// are its own end frames, or its other end's when it loops, so a loop has no seam.
    /// </summary>
    private bool MixResampled(
        float[] output,
        int frames,
        float rate,
        float stepLeft,
        float stepRight)
    {
        float gainLeft = this._gainLeft;
        float gainRight = this._gainRight;
        double position = this._position;

        var samples = this._samples;
        int channels = this._channels;
        int frameCount = this._frameCount;
        int lastFrame = frameCount - 1;
        int start = this._startFrame * channels;

        int mixed = 0;
        bool isPlaying = true;
        while (isPlaying && (mixed < frames))
        {
            int index = (int)position;
            float t = (float)(position - index);

            int i0 = index - 1;
            int i2 = index + 1;
            int i3 = index + 2;
            if ((i0 < 0) || (i3 > lastFrame))
            {
                if (this._isLooping)
                {
                    i0 = (i0 + frameCount) % frameCount;
                    i2 %= frameCount;
                    i3 %= frameCount;
                }
                else
                {
                    i0 = Math.Max(i0, 0);
                    i2 = Math.Min(i2, lastFrame);
                    i3 = Math.Min(i3, lastFrame);
                }
            }

            float left;
            float right;
            if (channels == 2)
            {
                left = PcmConverter.Hermite(
                    samples[start + (i0 * 2)],
                    samples[start + (index * 2)],
                    samples[start + (i2 * 2)],
                    samples[start + (i3 * 2)],
                    t);
                right = PcmConverter.Hermite(
                    samples[start + (i0 * 2) + 1],
                    samples[start + (index * 2) + 1],
                    samples[start + (i2 * 2) + 1],
                    samples[start + (i3 * 2) + 1],
                    t);
            }
            else
            {
                left = right = PcmConverter.Hermite(
                    samples[start + i0],
                    samples[start + index],
                    samples[start + i2],
                    samples[start + i3],
                    t);
            }

            output[mixed * 2] += left * gainLeft;
            output[(mixed * 2) + 1] += right * gainRight;
            gainLeft += stepLeft;
            gainRight += stepRight;
            mixed++;

            position += rate;
            if (position >= frameCount)
            {
                if (this._isLooping)
                {
                    do
                    {
                        position -= frameCount;
                    }
                    while (position >= frameCount);
                }
                else
                {
                    isPlaying = false;
                }
            }
        }

        this._position = position;
        this._gainLeft = gainLeft;
        this._gainRight = gainRight;

        return isPlaying;
    }

    private float ReadVolume()
    {
        return PolledVolumeControlStrategy.ReadEffectiveVolume(this._volumeControl);
    }
}
