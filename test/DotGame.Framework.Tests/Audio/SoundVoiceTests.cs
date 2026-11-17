// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Audio;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// Play starts over and Resume carries on, on every platform, though a platform's voice does both
/// with one call.
/// </summary>
public sealed class SoundVoiceTests : IDisposable
{
    private readonly FakeVoice _platform = new();
    private readonly SoundVoice _voice;

    public SoundVoiceTests()
    {
        var sound = new Sound(new Strategy<ISoundStrategy>(new FakeSound()));
        this._voice = new SoundVoice(new Strategy<IVoiceStrategy>(this._platform), sound);
    }

    public void Dispose()
    {
        this._voice.Dispose();
        this._platform.Dispose();
    }

    [Fact]
    public void Play_AfterAPause_StartsOver()
    {
        this._voice.Play();
        this._voice.Pause();
        this._platform.Calls.Clear();

        this._voice.Play();

        Assert.Equal(["Stop", "Play"], this._platform.Calls);
    }

    [Fact]
    public void Resume_AfterAPause_CarriesOn()
    {
        this._voice.Play();
        this._voice.Pause();
        this._platform.Calls.Clear();

        this._voice.Resume();

        Assert.Equal(["Play"], this._platform.Calls);
    }

    [Fact]
    public void PlaybackRate_IsAsRecordedUntilSet_AndReachesThePlatformOnce()
    {
        Assert.Equal(1f, this._voice.PlaybackRate);

        this._voice.PlaybackRate = 1f;
        Assert.Empty(this._platform.Calls);

        this._voice.PlaybackRate = 1.5f;
        this._voice.PlaybackRate = 1.5f;

        Assert.Equal(1.5f, this._voice.PlaybackRate);
        Assert.Equal(["Rate 1.5"], this._platform.Calls);
    }

    [Fact]
    public void PlaybackRate_StaysAcrossPlayStopAndPause()
    {
        this._voice.PlaybackRate = 2f;

        this._voice.Play();
        this._voice.Pause();
        this._voice.Resume();
        this._voice.Stop();
        this._voice.Play();

        Assert.Equal(2f, this._voice.PlaybackRate);
        Assert.Single(this._platform.Calls, call => call.StartsWith("Rate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0f, SoundVoice.MinPlaybackRate)]
    [InlineData(-3f, SoundVoice.MinPlaybackRate)]
    [InlineData(0.1f, SoundVoice.MinPlaybackRate)]
    [InlineData(9f, SoundVoice.MaxPlaybackRate)]
    [InlineData(float.PositiveInfinity, SoundVoice.MaxPlaybackRate)]
    public void PlaybackRate_OutsideItsRange_IsClamped(
        float value,
        float expected)
    {
        this._voice.PlaybackRate = value;

        Assert.Equal(expected, this._voice.PlaybackRate);
    }

    [Fact]
    public void PlaybackRate_NotANumber_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => this._voice.PlaybackRate = float.NaN);
        Assert.Equal(1f, this._voice.PlaybackRate);
    }

    [Fact]
    public void Resume_WithoutAPause_DoesNothing()
    {
        this._voice.Resume();
        Assert.Empty(this._platform.Calls);

        this._voice.Play();
        this._platform.Calls.Clear();
        this._voice.Resume();
        Assert.Empty(this._platform.Calls);
    }

    [Fact]
    public void Resume_AfterStop_DoesNothing()
    {
        this._voice.Play();
        this._voice.Pause();
        this._voice.Stop();
        this._platform.Calls.Clear();

        this._voice.Resume();

        Assert.Empty(this._platform.Calls);
    }

    [Fact]
    public void Pause_WhenNotPlaying_LeavesNothingToResume()
    {
        this._voice.Pause();
        this._platform.Calls.Clear();

        this._voice.Resume();

        Assert.Empty(this._platform.Calls);
    }

    private sealed class FakeSound : ISoundStrategy
    {
        public TimeSpan Duration => TimeSpan.FromSeconds(1);

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Play_WithLoop_ReachesThePlatform_BeforeItPlays_AndReadsBack()
    {
        Assert.False(this._voice.IsLooping);

        this._voice.Play(loop: true);

        Assert.True(this._voice.IsLooping);
        Assert.Equal(["Loop", "Play"], this._platform.Calls);
    }

    [Fact]
    public void Play_WithoutLoop_AfterALoop_TurnsItOff()
    {
        this._voice.Play(loop: true);
        this._platform.Calls.Clear();

        this._voice.Play();

        Assert.False(this._voice.IsLooping);
        Assert.Equal(["NoLoop", "Play"], this._platform.Calls);
    }

    [Fact]
    public void Play_WithoutLoop_LeavesThePlatformAlone()
    {
        this._voice.Play();

        Assert.Equal(["Play"], this._platform.Calls);
    }

    [Fact]
    public void Resume_KeepsLooping()
    {
        this._voice.Play(loop: true);
        this._voice.Pause();

        this._voice.Resume();

        Assert.True(this._voice.IsLooping);
    }

    [Fact]
    public void IsPaused_FollowsPauseResumeAndStop()
    {
        Assert.False(this._voice.IsPaused);

        this._voice.Pause();
        Assert.False(this._voice.IsPaused);

        this._voice.Play();
        this._voice.Pause();
        Assert.True(this._voice.IsPaused);

        this._voice.Resume();
        Assert.False(this._voice.IsPaused);

        this._voice.Pause();
        this._voice.Stop();
        Assert.False(this._voice.IsPaused);
    }

    private sealed class FakeVoice : IVoiceStrategy
    {
        public List<string> Calls { get; } = [];

        public bool IsPlaying { get; private set; }

        public void SetLooping(
            bool isLooping)
        {
            this.Calls.Add(isLooping ? "Loop" : "NoLoop");
        }

        public void SetVolumeControl(
            Strategy<IVolumeControlStrategy>? volumeControl)
        {
        }

        public void SetPlaybackRate(
            float rate)
        {
            this.Calls.Add(FormattableString.Invariant($"Rate {rate}"));
        }

        public void Play()
        {
            this.Calls.Add("Play");
            this.IsPlaying = true;
        }

        public void Stop()
        {
            this.Calls.Add("Stop");
            this.IsPlaying = false;
        }

        public void Pause()
        {
            this.Calls.Add("Pause");
            this.IsPlaying = false;
        }

        public void Dispose()
        {
        }
    }
}
