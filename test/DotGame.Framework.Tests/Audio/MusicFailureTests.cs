// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Diagnostics;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;
using DotGame.Framework.Tests.Platform;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// A track that can't play leaves the game running, silently and honestly, and is reported. A
/// Debug build ends the game instead, so the failure can't be missed.
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class MusicFailureTests : IDisposable
{
    private readonly List<Exception> _nonFatal = [];
    private readonly List<Exception?> _fatal = [];

    public MusicFailureTests()
    {
        FatalError.SetUnwindForTests(true);
        NonFatalError.ResetForTests();
        Trace.NonFatalException += this.OnNonFatal;
        Trace.FatalException += this.OnFatal;
    }

    public void Dispose()
    {
        Trace.NonFatalException -= this.OnNonFatal;
        Trace.FatalException -= this.OnFatal;
        FatalError.SetUnwindForTests(false);
        NonFatalError.ResetForTests();
    }

    private void OnNonFatal(
        object? sender,
        NonFatalExceptionEventArgs e)
    {
        lock (this._nonFatal)
        {
            this._nonFatal.Add(e.Exception);
        }
    }

    private void OnFatal(
        object? sender,
        FatalExceptionEventArgs e)
    {
        lock (this._fatal)
        {
            this._fatal.Add(e.Exception);
        }
    }

#if DEBUG
    [Fact]
    public async Task FailedAcquire_EndsADebugBuild()
    {
        using var music = new TestMusic { AcquireFault = new InvalidOperationException("Bad track") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IMusicStrategy)music).Prepare(null, null, null, false));

        Assert.Equal([music.AcquireFault], this._fatal);
        Assert.Empty(this._nonFatal);
    }
#else
    [Fact]
    public async Task FailedLoad_AfterPlay_StopsSayingItsPlaying()
    {
        using var music = new TestMusic { AcquireFault = new InvalidOperationException("Bad track") };

        ((IMusicStrategy)music).Play(null, null, null, false);
        await music.Drain();

        Assert.False(music.IsPlaying);
        Assert.False(music.IsPaused);
        Assert.Equal(MusicState.Loaded, music.State);
    }

    [Fact]
    public async Task FailedLoad_AfterPause_StopsSayingItsPaused()
    {
        using var music = new TestMusic { AcquireFault = new InvalidOperationException("Bad track") };

        ((IMusicStrategy)music).Play(null, null, null, false);
        ((IMusicStrategy)music).Pause();
        await music.Drain();

        Assert.False(music.IsPlaying);
        Assert.False(music.IsPaused);
    }

    [Fact]
    public async Task FailedAcquire_ReturnsToLoaded_AndIsReported()
    {
        using var music = new TestMusic { AcquireFault = new InvalidOperationException("Bad track") };

        await ((IMusicStrategy)music).Prepare(null, null, null, false);
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.False(music.IsPlaying);
        Assert.Equal([music.AcquireFault], this._nonFatal);
        Assert.Empty(this._fatal);
    }

    [Fact]
    public async Task FailedAcquire_WithPendingPlay_NeverStartsOutput()
    {
        using var music = new TestMusic { AcquireFault = new InvalidOperationException("Bad track") };

        music.Play(null, null, null, false);
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Equal(0, music.Starts);
    }

    [Fact]
    public async Task AfterAFailedAcquire_PlayTriesAgain()
    {
        using var music = new TestMusic { AcquireFault = new InvalidOperationException("Bad track") };
        await ((IMusicStrategy)music).Prepare(null, null, null, false);
        await music.Drain();

        music.AcquireFault = null;
        music.Play(null, null, null, false);
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal(1, music.Starts);
    }

    [Fact]
    public async Task RepeatedFailure_IsReportedOnce()
    {
        using var music = new TestMusic { AcquireFault = new InvalidOperationException("Bad track") };

        await ((IMusicStrategy)music).Prepare(null, null, null, false);
        await music.Drain();
        await ((IMusicStrategy)music).Prepare(null, null, null, false);
        await music.Drain();

        Assert.Equal(2, music.Acquires);
        Assert.Single(this._nonFatal);
    }

    [Fact]
    public async Task FailedRelease_DoesNotStopTheQueue()
    {
        using var music = new TestMusic { ReleaseFault = new InvalidOperationException("Bad release") };
        await ((IMusicStrategy)music).Prepare(null, null, null, false);

        ((IMusicStrategy)music).Unprepare();
        await music.Drain();

        music.ReleaseFault = null;
        await ((IMusicStrategy)music).Prepare(null, null, null, false);

        Assert.Equal(MusicState.Prepared, music.State);
        Assert.Single(this._nonFatal);
    }
#endif

    [Fact]
    public async Task EndOfThePreviousPlayback_DoesNotStopANewOne()
    {
        using var music = new TestMusic();
        music.Play(null, null, null, false);
        await music.Drain();
        Assert.Equal(MusicState.Playing, music.State);

        // The stop and the new play get in ahead of the completion's pool thread
        music.HoldCompletion(() =>
        {
            music.Stop();
            music.Play(null, null, null, false);
        });
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal(1, music.Stops);
    }

    [Fact]
    public async Task EndOfPlayback_StopsTheTrack()
    {
        using var music = new TestMusic();
        music.Play(null, null, null, false);
        await music.Drain();

        music.GetCompletionForTests()();
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
    }

    private sealed class TestMusic : MusicStrategyBase
    {
        public TestMusic()
            : base(listener: null)
        {
        }

        public Exception? AcquireFault { get; set; }

        public Exception? ReleaseFault { get; set; }

        public int Acquires { get; private set; }

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public override TimeSpan Duration => TimeSpan.FromSeconds(1);

        public override Strategy<IVolumeControlStrategy>? VolumeControl
        {
            set
            {
            }
        }

        // Everything queued so far, including what the queued work itself queues
        public async Task Drain()
        {
            Task pending;
            do
            {
                pending = this.PendingOperations;
                await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            while (pending != this.PendingOperations);
        }

        public void HoldCompletion(
            Action beforeItRuns)
        {
            var completion = this.GetCompletionForTests();
            beforeItRuns();
            completion();
        }

        protected override Task OnAcquireAsync(
            TimeSpan? start,
            TimeSpan? end,
            TimeSpan? loopPoint,
            bool loop,
            CancellationToken cancellationToken)
        {
            this.Acquires++;
            return this.AcquireFault is { } fault ? throw fault : Task.CompletedTask;
        }

        protected override Task OnReleaseAsync()
        {
            return this.ReleaseFault is { } fault ? throw fault : Task.CompletedTask;
        }

        protected override void OnStartOutput()
        {
            this.Starts++;
        }

        protected override void OnResumeOutput()
        {
        }

        protected override void OnPauseOutput()
        {
        }

        protected override void OnStopOutput()
        {
            this.Stops++;
        }
    }
}
