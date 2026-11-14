// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Audio;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// Play starts over and Resume carries on, as a voice's do, though a platform's music does both with
/// one call.
/// </summary>
public sealed class MusicControlTests : IDisposable
{
    private readonly FakeMusic _platform = new();
    private readonly Music _music;

    public MusicControlTests()
    {
        this._music = new Music(new Strategy<IMusicStrategy>(this._platform));
    }

    public void Dispose()
    {
        ((IDisposable)this._music).Dispose();
    }

    [Fact]
    public void Play_WhilePlaying_StartsOver()
    {
        this._platform.State = MusicState.Playing;

        this._music.Play();

        Assert.Equal(["Stop", "Play"], this._platform.Calls);
    }

    [Fact]
    public void Play_WhilePaused_StartsOver()
    {
        this._platform.State = MusicState.Paused;

        this._music.Play();

        Assert.Equal(["Stop", "Play"], this._platform.Calls);
    }

    [Fact]
    public void Play_WhilePausedBeforeStarting_StartsOver_ForTheNewRange()
    {
        this._platform.State = MusicState.Preparing;
        this._platform.PausedBeforeStarting = true;

        this._music.Play();

        Assert.Equal(["Stop", "Play"], this._platform.Calls);
    }

    [Fact]
    public void Play_WhileLoadingToPlay_CarriesOn()
    {
        this._platform.State = MusicState.Preparing;

        this._music.Play();

        Assert.Equal(["Play"], this._platform.Calls);
    }

    [Fact]
    public void Play_WhenStopped_JustPlays()
    {
        this._music.Play();

        Assert.Equal(["Play"], this._platform.Calls);
    }

    [Fact]
    public void Resume_WhenPaused_CarriesOn()
    {
        this._platform.State = MusicState.Paused;

        this._music.Resume();

        Assert.Equal(["Play"], this._platform.Calls);
    }

    [Fact]
    public void Resume_WhenPausedBeforeStarting_LetsItStart()
    {
        this._platform.State = MusicState.Preparing;
        this._platform.PausedBeforeStarting = true;

        this._music.Resume();

        Assert.Equal(["Play"], this._platform.Calls);
    }

    [Fact]
    public void Resume_WhenStopped_DoesNothing()
    {
        this._music.Resume();

        Assert.Empty(this._platform.Calls);
    }

    [Fact]
    public void Resume_WhenPlaying_DoesNothing()
    {
        this._platform.State = MusicState.Playing;

        this._music.Resume();

        Assert.Empty(this._platform.Calls);
    }

    [Fact]
    public void A_control_stays_until_the_music_under_it_has_moved_off()
    {
        var control = new VolumeControl(new Strategy<IVolumeControlStrategy>(new PolledVolumeControlStrategy()));
        this._music.VolumeControl = control;

        control.Dispose();
        Assert.False(control.Strategy.IsDisposed);

        this._music.VolumeControl = null;
        Assert.True(control.Strategy.IsDisposed);
    }

    [Fact]
    public void Disposing_the_music_lets_a_disposed_control_go()
    {
        var control = new VolumeControl(new Strategy<IVolumeControlStrategy>(new PolledVolumeControlStrategy()));
        this._music.VolumeControl = control;
        control.Dispose();

        ((IDisposable)this._music).Dispose();

        Assert.True(control.Strategy.IsDisposed);
    }

    [Fact]
    public void Music_cant_go_under_a_disposed_control()
    {
        var control = new VolumeControl(new Strategy<IVolumeControlStrategy>(new PolledVolumeControlStrategy()));
        control.Dispose();

        Assert.Throws<ObjectDisposedException>(() => this._music.VolumeControl = control);
        Assert.Null(this._music.VolumeControl);
    }

    [Fact]
    public void WhenDisposed_NeitherPlayingNorPaused()
    {
        this._platform.State = MusicState.Paused;
        ((IDisposable)this._music).Dispose();

        Assert.False(this._music.IsPlaying);
        Assert.False(this._music.IsPaused);
    }

    private sealed class FakeMusic : IMusicStrategy
    {
        public List<string> Calls { get; } = [];

        public MusicState State { get; set; } = MusicState.Loaded;

        public bool PausedBeforeStarting { get; set; }

        public bool IsPlaying => this.State == MusicState.Playing;

        public bool IsPaused => this.PausedBeforeStarting || (this.State == MusicState.Paused);

        public Strategy<IVolumeControlStrategy>? VolumeControl
        {
            set
            {
            }
        }

        public TimeSpan Duration => TimeSpan.FromSeconds(1);

        public void Play(TimeSpan? start, TimeSpan? end, TimeSpan? loopPoint, bool loop) => this.Calls.Add("Play");

        public Task Prepare(TimeSpan? start, TimeSpan? end, TimeSpan? loopPoint, bool loop) => Task.CompletedTask;

        public void Unprepare()
        {
        }

        public void Stop() => this.Calls.Add("Stop");

        public void Pause() => this.Calls.Add("Pause");

#pragma warning disable CA1822 // The music's Dispose, called as an instance's
        public void Dispose()
        {
        }
#pragma warning restore CA1822
    }
}
