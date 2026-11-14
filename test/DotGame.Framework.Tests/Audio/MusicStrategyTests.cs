// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// The music state machine every platform shares: its retention rules, and that prepare, play,
/// pause, stop and unprepare are safe in any order. Failures are in
/// <see cref="MusicFailureTests"/>.
/// </summary>
public sealed class MusicStrategyTests
{
    private const string Acquire = "acquire";
    private const string Release = "release";

    private static readonly TimeSpan Start = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan End = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan LoopPoint = TimeSpan.FromSeconds(5);

    private static void Play(
        FakeMusic music)
    {
        music.Play(null, null, null, false);
    }

    private static Task Prepare(
        FakeMusic music)
    {
        return music.PrepareAsync(null, null, null, false);
    }

    // ── Starting ───────────────────────────────────────────────────────

    [Fact]
    public void ANewTrack_IsLoaded_HoldingNothing()
    {
        using var music = new FakeMusic();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.False(music.IsPlaying);
        Assert.False(music.IsDisposed);
        Assert.Empty(music.Resources);
    }

    [Fact]
    public async Task Prepare_AcquiresWithItsParameters_WithoutAnyOutput()
    {
        using var music = new FakeMusic();

        await music.PrepareAsync(Start, End, LoopPoint, true);

        Assert.Equal(MusicState.Prepared, music.State);
        Assert.Equal([Acquire], music.Resources);
        Assert.Empty(music.Output);
        Assert.Equal((Start, End, LoopPoint, true), music.Acquired);
    }

    [Fact]
    public async Task Prepare_WhilePreparing_SharesTheOperation()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();

        var first = Prepare(music);
        var second = Prepare(music);
        Assert.Same(first, second);
        Assert.Equal(MusicState.Preparing, music.State);

        music.ReleaseAcquires();
        await first;

        Assert.Equal(MusicState.Prepared, music.State);
        Assert.Equal([Acquire], music.Resources);
    }

    [Fact]
    public async Task Prepare_WhenAlreadyResident_DoesNothing()
    {
        using var music = new FakeMusic();
        await Prepare(music);

        Assert.True(Prepare(music).IsCompletedSuccessfully);

        Play(music);
        Assert.True(Prepare(music).IsCompletedSuccessfully);

        music.Pause();
        Assert.True(Prepare(music).IsCompletedSuccessfully);

        Assert.Equal([Acquire], music.Resources);
    }

    [Fact]
    public async Task Play_FromLoaded_ReturnsAtOnce_ThenStartsOnceAcquired()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();

        music.Play(Start, End, LoopPoint, true);
        Assert.Equal(MusicState.Preparing, music.State);
        Assert.True(music.IsPlaying);
        Assert.False(music.IsPaused);
        Assert.Empty(music.Output);

        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.True(music.IsPlaying);
        Assert.Equal(["start"], music.Output);
        Assert.Equal((Start, End, LoopPoint, true), music.Acquired);
    }

    [Fact]
    public async Task Play_WhenPrepared_StartsAtOnce_WithoutAcquiringAgain()
    {
        using var music = new FakeMusic();
        await Prepare(music);

        Play(music);

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal(["start"], music.Output);
        await music.Drain();
        Assert.Equal([Acquire], music.Resources);
    }

    [Fact]
    public async Task Play_WhilePlaying_DoesNothing()
    {
        using var music = new FakeMusic();
        await Prepare(music);
        Play(music);

        Play(music);

        Assert.Equal(["start"], music.Output);
    }

    // ── Pausing ────────────────────────────────────────────────────────

    [Fact]
    public async Task Pause_KeepsEverything_AndPlayResumes()
    {
        using var music = new FakeMusic();
        await Prepare(music);
        Play(music);

        music.Pause();
        Assert.Equal(MusicState.Paused, music.State);
        Assert.False(music.IsPlaying);

        // The parameters are ignored: it carries on from where it paused
        music.Play(Start, End, LoopPoint, true);
        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal(["start", "pause", "resume"], music.Output);

        await music.Drain();
        Assert.Equal([Acquire], music.Resources);
    }

    [Fact]
    public async Task Pause_WhenNotPlaying_DoesNothing()
    {
        using var music = new FakeMusic();

        music.Pause();
        Assert.Equal(MusicState.Loaded, music.State);

        await Prepare(music);
        music.Pause();
        Assert.Equal(MusicState.Prepared, music.State);
        Assert.Empty(music.Output);
    }

    [Fact]
    public async Task Pause_WhilePreparing_KeepsItFromStartingLater()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        Play(music);

        music.Pause();

        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Prepared, music.State);
        Assert.Empty(music.Output);
    }

    [Fact]
    public async Task Pause_WhileLoading_IsPausedAtOnce_AndStaysSoOnceReady()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        Play(music);

        music.Pause();

        Assert.True(music.IsPaused);
        Assert.False(music.IsPlaying);

        music.ReleaseAcquires();
        await music.Drain();

        Assert.True(music.IsPaused);
        Assert.False(music.IsPlaying);
    }

    [Fact]
    public async Task Resume_AfterPauseWhileLoading_WhenStillLoading_StartsOnceReady()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        Play(music);
        music.Pause();

        Play(music);

        Assert.True(music.IsPlaying);
        Assert.False(music.IsPaused);

        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal(["start"], music.Output);
    }

    [Fact]
    public async Task Resume_AfterPauseWhileLoading_WhenReady_StartsNow()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        Play(music);
        music.Pause();
        music.ReleaseAcquires();
        await music.Drain();

        Play(music);

        Assert.True(music.IsPlaying);
        Assert.False(music.IsPaused);
        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal(["start"], music.Output);
    }

    [Fact]
    public async Task Stop_AfterPauseWhileLoading_IsNeither_AndReleases()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        Play(music);
        await music.WaitForAcquireToStart();
        music.Pause();

        music.Stop();

        Assert.False(music.IsPlaying);
        Assert.False(music.IsPaused);

        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Empty(music.Output);
        Assert.Equal([Acquire, Release], music.Resources);
    }

    [Fact]
    public async Task Play_WhenPreparedForAnotherRange_PreparesAgain_ThenPlaysThatRange()
    {
        using var music = new FakeMusic();
        await Prepare(music);

        music.Play(Start, End, LoopPoint, true);

        Assert.True(music.IsPlaying);
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal((Start, End, LoopPoint, true), music.Acquired);
        Assert.Equal(["start"], music.Output);
    }

    [Fact]
    public async Task Play_WhenPreparedForTheSameRange_StartsNow()
    {
        using var music = new FakeMusic();
        await music.PrepareAsync(Start, End, LoopPoint, true);

        music.Play(Start, End, LoopPoint, true);

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal(["start"], music.Output);
    }

    [Fact]
    public async Task Play_NoStartAndStartOfMusic_AreTheSameRange()
    {
        using var music = new FakeMusic();
        await Prepare(music);

        music.Play(TimeSpan.Zero, null, TimeSpan.Zero, false);

        Assert.Equal(MusicState.Playing, music.State);
    }

    [Fact]
    public async Task Play_WhilePreparingForAnotherRange_PlaysThatRange()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        var preparing = Prepare(music);

        music.Play(Start, End, LoopPoint, true);

        music.ReleaseAcquires();
        await preparing;
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal((Start, End, LoopPoint, true), music.Acquired);
        Assert.Equal(["start"], music.Output);
    }

    [Fact]
    public async Task Resume_AfterPauseWhileLoading_KeepsItsRange()
    {
        using var music = new FakeMusic();
        music.Play(Start, End, LoopPoint, true);
        music.Pause();
        await music.Drain();

        Play(music);
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal((Start, End, LoopPoint, true), music.Acquired);
    }

    [Fact]
    public async Task Prepare_WhenPreparedForAnotherRange_PreparesAgain()
    {
        using var music = new FakeMusic();
        await Prepare(music);

        await music.PrepareAsync(Start, End, LoopPoint, true);

        Assert.Equal(MusicState.Prepared, music.State);
        Assert.Equal((Start, End, LoopPoint, true), music.Acquired);
    }

    [Fact]
    public async Task Prepare_ForAnotherRange_WhilePlaying_LeavesItPlaying()
    {
        using var music = new FakeMusic();
        Play(music);
        await music.Drain();

        await music.PrepareAsync(Start, End, LoopPoint, true);

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal(["start"], music.Output);
    }

    [Fact]
    public async Task Prepare_ForAnotherRange_WhileLoadingToPlay_KeepsThePlaysRange()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        music.Play(Start, End, LoopPoint, true);

        _ = Prepare(music);

        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal((Start, End, LoopPoint, true), music.Acquired);
    }

    [Fact]
    public void Pause_WhenNotPlaying_IsNotPaused()
    {
        using var music = new FakeMusic();

        music.Pause();

        Assert.False(music.IsPaused);
    }

    [Fact]
    public async Task Pause_WhenOnlyPrepared_IsNotPaused()
    {
        using var music = new FakeMusic();
        await Prepare(music);

        music.Pause();

        Assert.False(music.IsPaused);
        Assert.Equal(MusicState.Prepared, music.State);
    }

    [Fact]
    public async Task Pause_WhilePlaying_IsPaused_AndPlayCarriesOn()
    {
        using var music = new FakeMusic();
        Play(music);
        await music.Drain();

        music.Pause();

        Assert.True(music.IsPaused);
        Assert.False(music.IsPlaying);

        Play(music);

        Assert.True(music.IsPlaying);
        Assert.False(music.IsPaused);
    }

    [Fact]
    public async Task PlaybackReachingTheEnd_IsNeither()
    {
        using var music = new FakeMusic();
        Play(music);
        await music.Drain();

        music.GetCompletionForTests()();

        Assert.False(music.IsPlaying);
        Assert.False(music.IsPaused);
    }

    [Fact]
    public async Task Dispose_WhileLoadingToPlay_IsNeither()
    {
        var music = new FakeMusic();
        music.HoldAcquires();
        Play(music);

        music.Dispose();
        music.ReleaseAcquires();
        await music.Drain();

        Assert.False(music.IsPlaying);
        Assert.False(music.IsPaused);
    }

    // ── Retention ──────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_WithoutAPrepare_ReleasesEverything()
    {
        using var music = new FakeMusic();
        Play(music);
        await music.Drain();

        music.Stop();
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Equal(["start", "stop"], music.Output);
        Assert.Equal([Acquire, Release], music.Resources);
    }

    [Fact]
    public async Task Stop_OfAPreparedTrack_KeepsItPrepared_AtItsStart()
    {
        using var music = new FakeMusic();
        await Prepare(music);
        Play(music);

        music.Stop();
        await music.Drain();

        Assert.Equal(MusicState.Prepared, music.State);
        Assert.Equal(["start", "stop"], music.Output);

        // It went back to its start position, which is an acquire, and not through a release
        Assert.Equal([Acquire, Acquire], music.Resources);
    }

    [Fact]
    public async Task Stop_OfAPausedTrack_StopsItsOutput()
    {
        using var music = new FakeMusic();
        Play(music);
        await music.Drain();
        music.Pause();

        music.Stop();
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Equal(["start", "pause", "stop"], music.Output);
        Assert.False(music.OutputRunning);
    }

    [Fact]
    public async Task Unprepare_OfAPlayingTrack_KeepsPlaying_AndTheStopReleases()
    {
        using var music = new FakeMusic();
        await Prepare(music);
        Play(music);

        music.Unprepare();
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal([Acquire], music.Resources);

        music.Stop();
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Equal([Acquire, Release], music.Resources);
    }

    [Fact]
    public async Task StopThenUnprepare_EndsTheSameAsUnprepareThenStop()
    {
        using var music = new FakeMusic();
        await Prepare(music);
        Play(music);

        music.Stop();
        await music.Drain();
        music.Unprepare();
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Equal(Release, music.Resources[^1]);
    }

    [Fact]
    public async Task Unprepare_OfAPausedTrack_StaysPaused()
    {
        using var music = new FakeMusic();
        await Prepare(music);
        Play(music);
        music.Pause();

        music.Unprepare();
        await music.Drain();

        Assert.Equal(MusicState.Paused, music.State);
        Assert.Equal([Acquire], music.Resources);
    }

    [Fact]
    public async Task Unprepare_OfAPreparedTrack_Releases()
    {
        using var music = new FakeMusic();
        await Prepare(music);

        music.Unprepare();
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Equal([Acquire, Release], music.Resources);
    }

    [Fact]
    public async Task StopAndUnprepare_OfALoadedTrack_ReleaseNothing()
    {
        using var music = new FakeMusic();

        music.Stop();
        music.Unprepare();
        await music.Drain();

        Assert.Empty(music.Resources);
    }

    // ── During an acquire ──────────────────────────────────────────────

    [Fact]
    public async Task Stop_WhilePreparing_DoesNotStartIt_AndReleasesAfterTheAcquire()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        Play(music);
        await music.WaitForAcquireToStart();

        music.Stop();
        Assert.Equal(MusicState.Loaded, music.State);

        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Empty(music.Output);
        Assert.Equal([Acquire, Release], music.Resources);
    }

    [Fact]
    public async Task Stop_WhilePreparing_ARetainedTrack_EndsPrepared()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        _ = Prepare(music);
        Play(music);

        music.Stop();

        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Prepared, music.State);
        Assert.Empty(music.Output);
    }

    [Fact]
    public async Task Unprepare_WhilePreparing_ReleasesAfterTheAcquire()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        _ = Prepare(music);
        await music.WaitForAcquireToStart();

        music.Unprepare();
        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Equal(Release, music.Resources[^1]);
    }

    [Fact]
    public async Task PlayAfterAStopThatLandedDuringAnAcquire_EndsPlaying_OnFreshResources()
    {
        using var music = new FakeMusic();
        music.HoldAcquires();
        Play(music);
        await music.WaitForAcquireToStart();

        // The first acquire is superseded, and its late finish must neither start output nor
        // release what the second acquire is building
        music.Stop();
        Play(music);

        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal(["start"], music.Output);
        Assert.Equal([Acquire, Release, Acquire], music.Resources);
    }

    [Fact]
    public async Task Play_WhileItGoesBackToItsStart_StartsOnceItIsThere()
    {
        using var music = new FakeMusic();
        await Prepare(music);
        Play(music);
        music.HoldAcquires();

        music.Stop();
        Assert.Equal(MusicState.Preparing, music.State);
        Play(music);

        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Playing, music.State);
        Assert.Equal(["start", "stop", "start"], music.Output);
    }

    // ── The end of playback ────────────────────────────────────────────

    [Fact]
    public async Task EndOfPlayback_OfAPreparedTrack_KeepsItPrepared()
    {
        using var music = new FakeMusic();
        await Prepare(music);
        Play(music);

        music.GetCompletionForTests()();
        await music.Drain();

        Assert.Equal(MusicState.Prepared, music.State);
        Assert.False(music.IsPlaying);
        Assert.Equal(["start", "stop"], music.Output);
    }

    [Fact]
    public async Task EndOfPlayback_AfterAPause_DoesNothing()
    {
        using var music = new FakeMusic();
        Play(music);
        await music.Drain();

        music.Pause();
        music.GetCompletionForTests()();

        Assert.Equal(MusicState.Paused, music.State);
    }

    [Fact]
    public async Task EndOfPlayback_ReportedFromAnAudioThread_SettlesOnThePool()
    {
        using var music = new FakeMusic();
        Play(music);
        await music.Drain();

        music.FinishPlayback();

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (music.Resources.Count < 2)
        {
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(30), "The end of playback never arrived.");
            await Task.Yield();
        }

        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Equal([Acquire, Release], music.Resources);
    }

    // ── Disposal ───────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_StopsTheOutput_AndReleases_Once()
    {
        var music = new FakeMusic();
        Play(music);
        await music.Drain();

        ((IDisposable)music).Dispose();
        ((IDisposable)music).Dispose();
        await music.Drain();

        Assert.Equal(MusicState.Unloaded, music.State);
        Assert.True(music.IsDisposed);
        Assert.Equal(["start", "stop"], music.Output);
        Assert.Equal([Acquire, Release], music.Resources);
    }

    [Fact]
    public async Task Dispose_OfALoadedTrack_StillReleases()
    {
        // A platform frees its metadata-level resources (a native decoder) there
        var music = new FakeMusic();

        ((IDisposable)music).Dispose();
        await music.Drain();

        Assert.Equal([Release], music.Resources);
    }

    [Fact]
    public async Task AfterDispose_EveryOperationDoesNothing()
    {
        var music = new FakeMusic();
        ((IDisposable)music).Dispose();
        await music.Drain();

        Play(music);
        Assert.True(Prepare(music).IsCompletedSuccessfully);
        music.Pause();
        music.Stop();
        music.Unprepare();
        music.GetCompletionForTests()();
        await music.Drain();

        Assert.Equal(MusicState.Unloaded, music.State);
        Assert.Empty(music.Output);
        Assert.Equal([Release], music.Resources);
    }

    [Fact]
    public async Task Dispose_DuringAnAcquire_LeavesItUnloaded_ReleasedOnce()
    {
        var music = new FakeMusic();
        music.HoldAcquires();
        Play(music);
        await music.WaitForAcquireToStart();

        ((IDisposable)music).Dispose();
        Assert.Equal(MusicState.Unloaded, music.State);

        music.ReleaseAcquires();
        await music.Drain();

        Assert.Equal(MusicState.Unloaded, music.State);
        Assert.Empty(music.Output);
        Assert.Equal([Acquire, Release], music.Resources);
    }

    [Fact]
    public async Task DisposeAndDrain_WaitsForTheRelease()
    {
        var music = new FakeMusic();
        Play(music);
        await music.Drain();

        music.DisposeAndDrain(TimeSpan.FromSeconds(30));

        Assert.Equal([Acquire, Release], music.Resources);
    }

    // ── The listener ───────────────────────────────────────────────────

    [Fact]
    public void TheGamesRequests_AreReportedToTheListener()
    {
        var listener = new FakeListener();
        using var music = new FakeMusic(listener);
        var game = (IMusicStrategy)music;

        game.Play(null, null, null, false);
        game.Pause();
        game.Stop();

        Assert.Equal(["CanPlay", "OnPlay", "OnPause", "OnStop"], listener.Calls);
    }

    [Fact]
    public async Task ATrackTheListenerRefuses_DoesNotPlay()
    {
        var listener = new FakeListener { Allow = false };
        using var music = new FakeMusic(listener);

        ((IMusicStrategy)music).Play(null, null, null, false);
        await music.Drain();

        Assert.Equal(["CanPlay"], listener.Calls);
        Assert.Equal(MusicState.Loaded, music.State);
        Assert.Empty(music.Resources);
    }

    [Fact]
    public async Task ThePlatformsOwnPausesAndResumes_AreNotReported()
    {
        var listener = new FakeListener();
        using var music = new FakeMusic(listener);

        Play(music);
        await music.Drain();
        music.Pause();
        Play(music);
        music.Stop();

        Assert.Empty(listener.Calls);
    }

    // ── Every order ────────────────────────────────────────────────────

    private enum Operation
    {
        Play,
        Prepare,
        Pause,
        Stop,
        Unprepare,
    }

    private const int OperationCount = 5;
    private const int SequenceLength = 4;
    private const int SequenceCount = OperationCount * OperationCount * OperationCount * OperationCount;

    private static Operation[] SequenceAt(
        int index)
    {
        var sequence = new Operation[SequenceLength];
        for (int i = 0; i < SequenceLength; i++)
        {
            sequence[i] = (Operation)(index % OperationCount);
            index /= OperationCount;
        }

        return sequence;
    }

    private static void Apply(
        FakeMusic music,
        Operation operation)
    {
        switch (operation)
        {
            case Operation.Play:
                Play(music);
                break;
            case Operation.Prepare:
                _ = Prepare(music);
                break;
            case Operation.Pause:
                music.Pause();
                break;
            case Operation.Stop:
                music.Stop();
                break;
            default:
                music.Unprepare();
                break;
        }
    }

    // What holds of a settled track, whatever was done to it
    private static void AssertConsistent(
        FakeMusic music,
        string sequence)
    {
        Assert.True(music.MostOperationsRunning <= 1, $"Resource operations overlapped after {sequence}");
        Assert.True(music.OutputRunning == (music.State == MusicState.Playing), $"Output is {(music.OutputRunning ? "on" : "off")} in {music.State} after {sequence}");

        if (music.State == MusicState.Loaded)
        {
            Assert.True((music.Resources.Count == 0) || (music.Resources[^1] == Release), $"A Loaded track still holds resources after {sequence}");
        }
        else
        {
            Assert.True(music.Resources[^1] == Acquire, $"A {music.State} track holds no resources after {sequence}");
        }
    }

    [Fact]
    public async Task EveryOrderOfOperations_Settled_MatchesTheRetentionRules()
    {
        for (int index = 0; index < SequenceCount; index++)
        {
            var sequence = SequenceAt(index);
            string description = string.Join(", ", sequence);

            using var music = new FakeMusic();
            var expected = MusicState.Loaded;
            bool prepareRetained = false;
            bool playbackRetained = false;

            foreach (var operation in sequence)
            {
                switch (operation)
                {
                    case Operation.Play:
                        playbackRetained = true;
                        expected = MusicState.Playing;
                        break;

                    case Operation.Prepare:
                        prepareRetained = true;
                        if (expected == MusicState.Loaded)
                        {
                            expected = MusicState.Prepared;
                        }

                        break;

                    case Operation.Pause:
                        if (expected == MusicState.Playing)
                        {
                            expected = MusicState.Paused;
                        }

                        break;

                    case Operation.Stop:
                        playbackRetained = false;
                        expected = prepareRetained ? MusicState.Prepared : MusicState.Loaded;
                        break;

                    default:
                        prepareRetained = false;
                        if (!playbackRetained)
                        {
                            expected = MusicState.Loaded;
                        }

                        break;
                }

                Apply(music, operation);
                await music.Drain();

                Assert.True(expected == music.State, $"Expected {expected}, not {music.State}, after {operation} in {description}");
                AssertConsistent(music, description);
            }
        }
    }

    [Fact]
    public async Task EveryOrderOfOperations_WithoutWaiting_SettlesConsistently()
    {
        for (int index = 0; index < SequenceCount; index++)
        {
            var sequence = SequenceAt(index);
            using var music = new FakeMusic();
            foreach (var operation in sequence)
            {
                Apply(music, operation);
            }

            await music.Drain();

            AssertConsistent(music, string.Join(", ", sequence));
        }
    }

    private sealed class FakeListener : IMusicPlaybackListener
    {
        public bool Allow { get; init; } = true;

        public List<string> Calls { get; } = [];

        public bool CanPlay(
            MusicStrategyBase music)
        {
            this.Calls.Add("CanPlay");
            return this.Allow;
        }

        public void OnPlay(
            MusicStrategyBase music)
        {
            this.Calls.Add("OnPlay");
        }

        public void OnPause(
            MusicStrategyBase music)
        {
            this.Calls.Add("OnPause");
        }

        public void OnStop(
            MusicStrategyBase music)
        {
            this.Calls.Add("OnStop");
        }
    }

    private sealed class FakeMusic(IMusicPlaybackListener? listener = null) : MusicStrategyBase(listener)
    {
        private readonly List<string> _resources = [];
        private readonly TaskCompletionSource _acquireStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _acquireGate;
        private int _running;
        private int _mostRunning;

        public List<string> Resources
        {
            get
            {
                lock (this._resources)
                {
                    return [.. this._resources];
                }
            }
        }

        public List<string> Output { get; } = [];

        public bool OutputRunning { get; private set; }

        public int MostOperationsRunning => Volatile.Read(ref this._mostRunning);

        public (TimeSpan? Start, TimeSpan? End, TimeSpan? LoopPoint, bool Loop) Acquired { get; private set; }

        public override TimeSpan Duration => TimeSpan.FromSeconds(10);

        public override Strategy<IVolumeControlStrategy>? VolumeControl
        {
            set
            {
            }
        }

        public void HoldAcquires()
        {
            this._acquireGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseAcquires()
        {
            var gate = this._acquireGate!;
            this._acquireGate = null;
            gate.SetResult();
        }

        public Task WaitForAcquireToStart()
        {
            return this._acquireStarted.Task;
        }

        public void FinishPlayback()
        {
            this.NotifyPlaybackCompleted();
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

        protected override async Task OnAcquireAsync(
            TimeSpan? start,
            TimeSpan? end,
            TimeSpan? loopPoint,
            bool loop,
            CancellationToken cancellationToken)
        {
            this.Enter(Acquire);
            this.Acquired = (start, end, loopPoint, loop);
            this._acquireStarted.TrySetResult();
            try
            {
                if (this._acquireGate is { } gate)
                {
                    await gate.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Decrement(ref this._running);
            }
        }

        protected override Task OnReleaseAsync()
        {
            this.Enter(Release);
            Interlocked.Decrement(ref this._running);
            return Task.CompletedTask;
        }

        protected override void OnStartOutput()
        {
            this.Output.Add("start");
            this.OutputRunning = true;
        }

        protected override void OnResumeOutput()
        {
            this.Output.Add("resume");
            this.OutputRunning = true;
        }

        protected override void OnPauseOutput()
        {
            this.Output.Add("pause");
            this.OutputRunning = false;
        }

        protected override void OnStopOutput()
        {
            this.Output.Add("stop");
            this.OutputRunning = false;
        }

        private void Enter(
            string operation)
        {
            int running = Interlocked.Increment(ref this._running);
            lock (this._resources)
            {
                this._resources.Add(operation);
                this._mostRunning = Math.Max(this._mostRunning, running);
            }
        }
    }
}
