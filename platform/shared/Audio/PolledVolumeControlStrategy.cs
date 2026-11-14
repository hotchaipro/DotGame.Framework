// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Threading;
using DotGame.Framework.Audio;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.Platform.Common.Audio;

/// <summary>
/// A volume level, or a fade between two, that the game thread sets
/// and audio threads poll while a sound or music plays, so a change
/// or a fade takes effect on a voice that's already playing. Used by
/// the native platforms (Android, desktop, iOS); the web schedules
/// fades on its gain nodes instead.
/// </summary>
/// <remarks>
/// Reading never writes: the volume is computed from the fade's
/// fields, which the writer brackets with a sequence counter, so a
/// reader on another thread retries rather than seeing half of a new
/// fade. Writes come from the game thread only.
/// </remarks>
internal sealed class PolledVolumeControlStrategy : IVolumeControlStrategy
{
    // Over every control with no parent, and everything playing with no control. There's one
    // audio device, and a game that starts again in the same process starts it at full volume.
    public static PolledVolumeControlStrategy Master { get; } = new PolledVolumeControlStrategy();

    private volatile PolledVolumeControlStrategy? _parent;
    private int _sequence;
    private float _fromVolume = 1f;
    private float _toVolume = 1f;
    private long _fadeStart;
    private long _fadeTicks;
    private FadeCurve _fadeCurve = FadeCurve.Exponential;

    public Strategy<IVolumeControlStrategy>? Parent
    {
        set => this._parent = value?.ToConcrete<PolledVolumeControlStrategy>();
    }

    /// <summary>
    /// What a voice or music with this control (or none) plays at: the control's volume through
    /// its parents and the master.
    /// </summary>
    public static float ReadEffectiveVolume(
        PolledVolumeControlStrategy? control)
    {
        float volume = Master.Volume;

        // NOTE: A control can't be under itself: the framework refuses that, on the game thread
        for (; control is not null; control = control._parent)
        {
            if (!ReferenceEquals(control, Master))
            {
                volume *= control.Volume;
            }
        }

        return volume;
    }

    public float Volume
    {
        get
        {
            var spinner = default(SpinWait);
            while (true)
            {
                int sequence = Volatile.Read(ref this._sequence);
                if ((sequence & 1) == 0)
                {
                    float volume = this.Compute();

                    // NOTE: A load of the fields can't move past this and the read below on ARM
                    Interlocked.MemoryBarrier();

                    if (Volatile.Read(ref this._sequence) == sequence)
                    {
                        return volume;
                    }
                }

                spinner.SpinOnce();
            }
        }
        set
        {
            float volume = Math.Clamp(value, 0f, 1f);
            this.BeginWrite();
            this._fromVolume = volume;
            this._toVolume = volume;
            this._fadeTicks = 0;
            this.EndWrite();
        }
    }

    public void FadeTo(
        float toVolume,
        TimeSpan duration,
        FadeCurve curve)
    {
        float fromVolume = this.Compute();
        long fadeTicks = (long)(duration.TotalSeconds * Stopwatch.Frequency);

        this.BeginWrite();
        this._fromVolume = fromVolume;
        this._toVolume = Math.Clamp(toVolume, 0f, 1f);
        this._fadeStart = Stopwatch.GetTimestamp();
        this._fadeTicks = fadeTicks;
        this._fadeCurve = curve;
        this.EndWrite();
    }

    private void BeginWrite()
    {
        Interlocked.Increment(ref this._sequence);
    }

    private void EndWrite()
    {
        Interlocked.Increment(ref this._sequence);
    }

    private float Compute()
    {
        long fadeTicks = this._fadeTicks;
        if (fadeTicks <= 0)
        {
            return this._toVolume;
        }

        long elapsed = Stopwatch.GetTimestamp() - this._fadeStart;
        if (elapsed >= fadeTicks)
        {
            return this._toVolume;
        }

        float t = (float)((double)elapsed / fadeTicks);
        return this._fadeCurve.Interpolate(this._fromVolume, this._toVolume, t);
    }
}
