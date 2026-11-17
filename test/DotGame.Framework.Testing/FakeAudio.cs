// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DotGame.Framework.Audio;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.Testing;

/// <summary>
/// An <see cref="AudioService"/> over fake voices that play until a test finishes them.
/// </summary>
public sealed class FakeAudio : IAudioServiceStrategy
{
    private readonly Strategy<IVolumeControlStrategy> _master;

    public FakeAudio()
    {
        this._master = new Strategy<IVolumeControlStrategy>(new FakeVolumeControl([]));
        this.Service = new AudioService(this.ToStrategy());
    }

    public AudioService Service { get; }

    /// <summary>The voices played, in order.</summary>
    public List<FakeVoice> Voices { get; } = [];

    /// <summary>The volume controls made, in order; not the master.</summary>
    public List<FakeVolumeControl> VolumeControls { get; } = [];

    /// <summary>
    /// What was done to the voices and their volume controls, in order, with room for allocation
    /// tests to play without growing it.
    /// </summary>
    public List<string> Log { get; } = new(65536);

    /// <summary>
    /// A sound that lasts <paramref name="seconds"/>.
    /// </summary>
    public static Sound CreateSound(
        double seconds = 10)
    {
        return new Sound(new Strategy<ISoundStrategy>(new FakeSound(TimeSpan.FromSeconds(seconds))));
    }

    /// <summary>
    /// The platform's side of <paramref name="control"/>.
    /// </summary>
    public static FakeVolumeControl? FakeOf(
        VolumeControl control)
    {
        ArgumentNullException.ThrowIfNull(control);

        return control.Strategy.IsDisposed ? null : control.Strategy.Interface as FakeVolumeControl;
    }

    /// <summary>
    /// Whether the platform's side of <paramref name="control"/> has been released.
    /// </summary>
    public static bool IsReleased(
        VolumeControl control)
    {
        ArgumentNullException.ThrowIfNull(control);

        return control.Strategy.IsDisposed;
    }

    internal Strategy<IAudioServiceStrategy> ToStrategy() => new(this);

    Task IAudioServiceStrategy.OnInitialize() => Task.CompletedTask;

    Strategy<IVolumeControlStrategy> IAudioServiceStrategy.MasterVolumeControl => this._master;

    Strategy<IVolumeControlStrategy> IAudioServiceStrategy.CreateVolumeControl()
    {
        var control = new FakeVolumeControl(this.Log);
        this.VolumeControls.Add(control);
        return new Strategy<IVolumeControlStrategy>(control);
    }

    Strategy<IVoiceStrategy> IAudioServiceStrategy.CreateVoice(
        Strategy<ISoundStrategy> sound,
        TimeSpan offset,
        TimeSpan duration)
    {
        var voice = new FakeVoice(this.Log, offset, duration);
        this.Voices.Add(voice);
        return new Strategy<IVoiceStrategy>(voice);
    }

    bool IAudioServiceStrategy.ShouldPlayMusic() => true;

    private sealed class FakeSound(TimeSpan duration) : ISoundStrategy
    {
        public TimeSpan Duration { get; } = duration;

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A voice the framework played, which plays until <see cref="Finish"/>.
/// </summary>
public sealed class FakeVoice : IVoiceStrategy
{
    private readonly List<string> _log;

    internal FakeVoice(
        List<string> log,
        TimeSpan offset,
        TimeSpan duration)
    {
        this._log = log;
        this.Offset = offset;
        this.Duration = duration;
    }

    public TimeSpan Offset { get; }

    public TimeSpan Duration { get; }

    public bool IsPlaying { get; private set; }

    public int Plays { get; private set; }

    public int Stops { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>The volume control it plays through, or null for the master.</summary>
    public FakeVolumeControl? VolumeControl { get; private set; }

    /// <summary>
    /// The cue reaches its end.
    /// </summary>
    public void Finish()
    {
        this.IsPlaying = false;
    }

    /// <summary>The rate it was last told to play at.</summary>
    public float PlaybackRate { get; private set; } = 1f;

    void IVoiceStrategy.SetLooping(
        bool isLooping)
    {
    }

    void IVoiceStrategy.SetPlaybackRate(
        float rate)
    {
        this.PlaybackRate = rate;
    }

    void IVoiceStrategy.SetVolumeControl(
        Strategy<IVolumeControlStrategy>? volumeControl)
    {
        this.VolumeControl = volumeControl?.Interface as FakeVolumeControl;
    }

    /// <summary>
    /// Plays it, as the framework does; a test plays one again itself to see what's left of an
    /// earlier stop or fade.
    /// </summary>
    public void Play()
    {
        this._log.Add("play");
        this.Plays += 1;
        this.IsPlaying = true;
    }

    void IVoiceStrategy.Stop()
    {
        this._log.Add("stop");
        this.Stops += 1;
        this.IsPlaying = false;
    }

    void IVoiceStrategy.Pause()
    {
        this.IsPlaying = false;
    }

    void IDisposable.Dispose()
    {
        this.IsDisposed = true;
        this.IsPlaying = false;
    }
}

/// <summary>
/// A volume control the framework made.
/// </summary>
public sealed class FakeVolumeControl : IVolumeControlStrategy
{
    private readonly List<string> _log;
    private float _volume = 1;
    private Strategy<IVolumeControlStrategy>? _parent;

    internal FakeVolumeControl(
        List<string> log)
    {
        this._log = log;
    }

    /// <summary>The control it's chained under, or null for the master.</summary>
    public FakeVolumeControl? Parent => this._parent?.Interface as FakeVolumeControl;

    public float Volume => this._volume;

    /// <summary>The volume a fade is going to, or null if none is.</summary>
    public float? FadeTarget { get; private set; }

    public TimeSpan FadeDuration { get; private set; }

    Strategy<IVolumeControlStrategy>? IVolumeControlStrategy.Parent
    {
        set => this._parent = value;
    }

    float IVolumeControlStrategy.Volume
    {
        get => this._volume;
        set
        {
            this._log.Add("volume");
            this._volume = value;
            this.FadeTarget = null;
        }
    }

    void IVolumeControlStrategy.FadeTo(
        float toVolume,
        TimeSpan duration,
        FadeCurve curve)
    {
        this._log.Add("fade");
        this.FadeTarget = toVolume;
        this.FadeDuration = duration;
    }
}
