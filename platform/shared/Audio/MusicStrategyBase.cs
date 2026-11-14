// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using DotGame.Framework.Audio;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common;

namespace DotGame.Framework.Platform.Common.Audio;

/// <summary>
/// Shared state machine for every platform music strategy. Owns the
/// <see cref="MusicState"/> transitions, the buffer-retention
/// accounting, and the scheduling of all expensive work onto a
/// background queue so no public entry point ever blocks the game,
/// main, or audio-render thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Retention model.</b> Two independent reasons keep a track's
/// buffers alive:
/// </para>
/// <list type="bullet">
/// <item><description><i>Prepare retain</i> — set by
/// <see cref="PrepareAsync"/>, cleared by <see cref="Unprepare"/>.</description></item>
/// <item><description><i>Playback retain</i> — set by
/// <see cref="Play"/>, cleared by <see cref="Stop"/> (never by
/// <see cref="Pause"/>).</description></item>
/// </list>
/// <para>
/// Buffers are released only when both are clear, which makes the
/// operations order-independent: stopping a prepared track keeps its
/// memory, unpreparing a playing track keeps its memory, and whichever
/// of the two happens last performs the release.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Play"/>, <see cref="Pause"/>,
/// <see cref="Stop"/>, and <see cref="Unprepare"/> are synchronous but
/// non-blocking: they take a short lock, update state, invoke only the
/// cheap output primitives, and queue any resource work.
/// <see cref="PrepareAsync"/> returns the queued task without waiting
/// on it. <see cref="NotifyPlaybackCompleted"/> is safe to call from a
/// platform audio callback thread.
/// </para>
/// </remarks>
internal abstract class MusicStrategyBase : IMusicStrategy, IDisposable
{
    private const uint AcquireFailedTag = 0x4f1a92c7;
    private const uint ReleaseFailedTag = 0x4f1a92c8;

    private readonly Lock _syncRoot = new();
    private readonly AsyncOperationQueue _queue = new();
    private readonly Action<int> _playbackCompletedCallback;
    private readonly IMusicPlaybackListener? _listener;

    // Read without the lock from IsPlaying/State, which are polled
    // every frame; written only under _syncRoot.
    private volatile MusicState _state = MusicState.Loaded;

    private bool _prepareRetained;
    private bool _playbackRetained;
    private bool _disposed;

    // What the game last asked for, which is what IsPlaying and IsPaused report whatever
    // _state has got to, and what the acquire continuation acts on. Read without the lock;
    // written only under _syncRoot.
    private volatile PlaybackRequest _request;

    // Parameters of the in-flight or most recent acquire, replayed
    // when a pending play is flushed.
    private PlaybackParameters _parameters;

    // Bumped by Dispose and by any operation that invalidates an
    // in-flight acquire (Unprepare/Stop that releases, Dispose). A
    // continuation whose captured epoch no longer matches is stale and
    // must not mutate state or resurrect buffers.
    private int _epoch;

    private Task _prepareTask = Task.CompletedTask;

    /// <param name="listener">Observes the playback the game asks of this track, for an audio
    /// service that pauses and resumes it around system events; null for none.</param>
    protected MusicStrategyBase(
        IMusicPlaybackListener? listener)
    {
        this._listener = listener;
        this._playbackCompletedCallback = this.HandlePlaybackCompleted;
    }

    #region IMusicStrategy

    // The game's requests, which the listener observes. The audio service's own pauses and
    // resumes around system events call the internal operations below directly.

    void IMusicStrategy.Play(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop)
    {
        if ((this._listener is { } listener) && !listener.CanPlay(this))
        {
            return;
        }

        this.Play(start, end, loopPoint, loop);
        this._listener?.OnPlay(this);
    }

    Task IMusicStrategy.Prepare(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop)
    {
        return this.PrepareAsync(start, end, loopPoint, loop);
    }

    void IMusicStrategy.Unprepare()
    {
        this.Unprepare();
    }

    void IMusicStrategy.Stop()
    {
        this.Stop();
        this._listener?.OnStop(this);
    }

    void IMusicStrategy.Pause()
    {
        this.Pause();
        this._listener?.OnPause(this);
    }

    #endregion

    /// <inheritdoc />
    public MusicState State => this._state;

    /// <inheritdoc />
    public bool IsPlaying => this._request == PlaybackRequest.Playing;

    /// <inheritdoc />
    public bool IsPaused => this._request == PlaybackRequest.Paused;

    /// <inheritdoc />
    public abstract TimeSpan Duration { get; }

    /// <inheritdoc />
    public abstract Strategy<IVolumeControlStrategy>? VolumeControl { set; }

    /// <summary>
    /// <c>true</c> once this track has been disposed and its platform
    /// resources released. Audio services use this to evict tracks the
    /// game disposed from their suspend/resume tracking sets so they
    /// are never resurrected.
    /// </summary>
    internal bool IsDisposed => Volatile.Read(ref this._disposed);

    /// <summary>
    /// The playback parameters captured by the most recent
    /// <see cref="PrepareAsync"/> or <see cref="Play"/>. Platform
    /// implementations may consult this from their output primitives.
    /// </summary>
    protected PlaybackParameters Parameters => this._parameters;

    #region Platform primitives

    /// <summary>
    /// Acquires everything needed to render this track: opens the
    /// source, creates the decoder and platform playback objects,
    /// allocates decode and device buffers, and seeks to
    /// <paramref name="start"/>.
    /// </summary>
    /// <remarks>
    /// Always invoked on the background operation queue, never on the
    /// caller's thread. Must not produce any audio. Must be safe to
    /// call again after <see cref="OnReleaseAsync"/>.
    /// </remarks>
    protected abstract Task OnAcquireAsync(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases everything <see cref="OnAcquireAsync"/> reserved,
    /// returning the track to the metadata-only
    /// <see cref="MusicState.Loaded"/> state.
    /// </summary>
    /// <remarks>
    /// Always invoked on the background operation queue, so it may
    /// perform blocking native teardown (stopping a device, joining a
    /// playback thread, releasing a codec). Must be idempotent.
    /// </remarks>
    protected abstract Task OnReleaseAsync();

    /// <summary>
    /// Begins rendering from the prepared position. Called on the
    /// caller's thread, so it must be cheap and non-blocking — all
    /// costly work belongs in <see cref="OnAcquireAsync"/>.
    /// </summary>
    protected abstract void OnStartOutput();

    /// <summary>
    /// Resumes rendering after <see cref="OnPauseOutput"/>. Must be
    /// cheap and non-blocking.
    /// </summary>
    protected abstract void OnResumeOutput();

    /// <summary>
    /// Suspends rendering, retaining buffers and playback position.
    /// Must be cheap and non-blocking.
    /// </summary>
    protected abstract void OnPauseOutput();

    /// <summary>
    /// Halts rendering. Buffers are <i>not</i> freed here; the state
    /// machine calls <see cref="OnReleaseAsync"/> separately when the
    /// retention accounting allows it. Must be cheap and non-blocking.
    /// </summary>
    protected abstract void OnStopOutput();

    #endregion

    #region Public operations

    /// <summary>
    /// Primes this track for playback without producing audio, and
    /// retains its buffers until <see cref="Unprepare"/> is called.
    /// </summary>
    /// <returns>
    /// A task that completes when the track is prepared. Callers are
    /// never required to await it; <see cref="Play"/> may be called at
    /// any time and will start automatically once preparation
    /// finishes.
    /// </returns>
    internal Task PrepareAsync(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop)
    {
        lock (this._syncRoot)
        {
            if (this._disposed || this._state == MusicState.Unloaded)
            {
                return Task.CompletedTask;
            }

            // The prepare retain is taken regardless of the current
            // state so a later Stop leaves the track Prepared rather
            // than releasing its buffers.
            this._prepareRetained = true;

            var parameters = new PlaybackParameters(start, end, loopPoint, loop);

            switch (this._state)
            {
                case MusicState.Playing:
                case MusicState.Paused:
                    // Buffers are already resident, and another range would
                    // stop what the game is listening to.
                    return Task.CompletedTask;

                case MusicState.Prepared when parameters.IsSameRange(this._parameters):
                    return Task.CompletedTask;

                case MusicState.Preparing when (this._request != PlaybackRequest.None) || parameters.IsSameRange(this._parameters):
                    // Share the in-flight operation; Prepare is idempotent,
                    // and doesn't change the range of a play that's on its way.
                    return this._prepareTask;

                default:
                    // Nothing resident, or it's for another range.
                    this._parameters = parameters;
                    this._prepareTask = this.BeginAcquireLocked();

                    return this._prepareTask;
            }
        }
    }

    /// <summary>
    /// Begins or resumes playback. Returns immediately without
    /// blocking, even when buffers must first be acquired.
    /// </summary>
    /// <remarks>
    /// When the track is paused, the parameters are ignored and
    /// playback resumes from the paused position — this is what the
    /// platform lifecycle and audio-focus sweeps rely on when they
    /// call <c>Play(null, null, null, false)</c>. Otherwise, a track
    /// prepared or preparing for another range prepares again for this one.
    /// </remarks>
    internal void Play(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop)
    {
        lock (this._syncRoot)
        {
            if (this._disposed || this._state == MusicState.Unloaded)
            {
                return;
            }

            switch (this._state)
            {
                case MusicState.Playing:
                    // Already playing; preserve the existing
                    // start/end/loop state.
                    return;

                case MusicState.Paused:
                    this._playbackRetained = true;
                    this._request = PlaybackRequest.Playing;
                    this.OnResumeOutput();
                    this._state = MusicState.Playing;
                    return;

                case MusicState.Preparing:
                case MusicState.Prepared:
                    bool resuming = this._request == PlaybackRequest.Paused;
                    var parameters = new PlaybackParameters(start, end, loopPoint, loop);

                    this._playbackRetained = true;
                    this._request = PlaybackRequest.Playing;

                    if ((!resuming) && (!parameters.IsSameRange(this._parameters)))
                    {
                        this._parameters = parameters;
                        this._prepareTask = this.BeginAcquireLocked();
                    }
                    else if (this._state == MusicState.Prepared)
                    {
                        this.OnStartOutput();
                        this._state = MusicState.Playing;
                    }

                    // NOTE: If preparing, the acquire continuation starts playback
                    return;

                default:
                    // Loaded: implicitly prepare, then auto-play. The
                    // acquisition runs on the queue so the game thread
                    // is never blocked on I/O or decode.
                    this._playbackRetained = true;
                    this._request = PlaybackRequest.Playing;
                    this._parameters = new PlaybackParameters(
                        start,
                        end,
                        loopPoint,
                        loop);

                    this._prepareTask = this.BeginAcquireLocked();
                    return;
            }
        }
    }

    /// <summary>
    /// Suspends playback, retaining all buffers and the playback
    /// position. A subsequent <see cref="Play"/> resumes in place.
    /// </summary>
    internal void Pause()
    {
        lock (this._syncRoot)
        {
            if (this._disposed || (this._request != PlaybackRequest.Playing))
            {
                return;
            }

            this._request = PlaybackRequest.Paused;

            // NOTE: A track still preparing doesn't start when it's ready, and a later Play does
            if (this._state == MusicState.Playing)
            {
                this.OnPauseOutput();
                this._state = MusicState.Paused;
            }
        }
    }

    /// <summary>
    /// Stops playback and releases the playback retain. Buffers are
    /// freed only if no <see cref="PrepareAsync"/> retain is
    /// outstanding; otherwise the track returns to
    /// <see cref="MusicState.Prepared"/> so the next
    /// <see cref="Play"/> is still latency-free.
    /// </summary>
    internal void Stop()
    {
        lock (this._syncRoot)
        {
            if (this._disposed || this._state == MusicState.Unloaded)
            {
                return;
            }

            this._request = PlaybackRequest.None;
            this._playbackRetained = false;

            this.StopOutputLocked();
            this.SettleRetentionLocked();
        }
    }

    /// <summary>
    /// Releases the prepare retain. Buffers are freed only if playback
    /// is not currently retaining them; otherwise they are freed by
    /// the eventual <see cref="Stop"/>.
    /// </summary>
    internal void Unprepare()
    {
        lock (this._syncRoot)
        {
            if (this._disposed || this._state == MusicState.Unloaded)
            {
                return;
            }

            this._prepareRetained = false;

            this.SettleRetentionLocked();
        }
    }

    /// <summary>
    /// Called by a platform implementation when playback ends on its
    /// own (the track reached its end position and is not looping).
    /// </summary>
    /// <remarks>
    /// Safe to call from an audio render or decode thread: the
    /// transition is posted to the thread pool rather than taking the
    /// state lock inline, so the audio thread is never blocked behind
    /// a game-thread operation.
    /// </remarks>
    protected void NotifyPlaybackCompleted()
    {
        ThreadPool.UnsafeQueueUserWorkItem(
            this._playbackCompletedCallback,
            Volatile.Read(ref this._epoch),
            preferLocal: false);
    }

    /// <summary>
    /// All the resource work queued so far.
    /// </summary>
    internal Task PendingOperations => this._queue.Current;

    /// <summary>
    /// The handling <see cref="NotifyPlaybackCompleted"/> would queue, to run when a test says.
    /// </summary>
    internal Action GetCompletionForTests()
    {
        int epoch = Volatile.Read(ref this._epoch);
        return () => this.HandlePlaybackCompleted(epoch);
    }

    private void HandlePlaybackCompleted(
        int epoch)
    {
        lock (this._syncRoot)
        {
            // NOTE: The epoch moves on with a stop, so the end of the last playback can't stop
            // a new one the game started before this ran
            if (this._disposed || this._state != MusicState.Playing || this._epoch != epoch)
            {
                return;
            }

            // Natural end uses the same retention accounting as an
            // explicit Stop.
            this._request = PlaybackRequest.None;
            this._playbackRetained = false;

            this.StopOutputLocked();
            this.SettleRetentionLocked();
        }

        this._listener?.OnStop(this);
    }

    #endregion

    #region Transitions

    // Starts an acquire on the background queue and moves to
    // Preparing. Caller must hold _syncRoot.
    private Task BeginAcquireLocked()
    {
        // Invalidate any acquire that is already in flight. Without
        // this, a re-prime queued by SettleRetentionLocked runs with
        // the same epoch as the acquire it supersedes and the two
        // continuations race to set the final state — one of which
        // leaves the track in Prepared while the caller believes it is
        // Playing, producing silence.
        this._epoch++;

        this._state = MusicState.Preparing;

        int epoch = this._epoch;
        var parameters = this._parameters;

        return this._queue.Enqueue(
            () => this.AcquireAsync(epoch, parameters),
            AcquireFailedTag);
    }

    private async Task AcquireAsync(
        int epoch,
        PlaybackParameters parameters)
    {
        if (this.IsStale(epoch))
        {
            return;
        }

        try
        {
            await this.OnAcquireAsync(
                parameters.Start,
                parameters.End,
                parameters.LoopPoint,
                parameters.Loop,
                CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The track is silent, and honestly so: it goes back to
            // Loaded rather than claiming to be Prepared or Playing, and
            // a later Play tries again. Reported, since silence alone is
            // what made the SDL3 silence bug so hard to find.
            GameHost.ReportAudioFailure(AcquireFailedTag, exception);

            lock (this._syncRoot)
            {
                if (this._epoch == epoch && !this._disposed)
                {
                    this._prepareRetained = false;
                    this._playbackRetained = false;
                    this.BeginReleaseLocked();
                }
            }

            return;
        }

        lock (this._syncRoot)
        {
            if (this._epoch != epoch || this._disposed)
            {
                // Disposed, unprepared, or stopped while we were
                // acquiring. Hand the buffers straight back rather
                // than resurrecting a released track — but only when
                // nothing still wants them. A newer acquire may have
                // superseded this one, in which case releasing here
                // would tear down the resources it just built, since
                // the queued teardown would run after it.
                if (!this._prepareRetained && !this._playbackRetained)
                {
                    this.BeginReleaseLocked();
                }

                return;
            }

            if (this._request == PlaybackRequest.Playing)
            {
                this.OnStartOutput();
                this._state = MusicState.Playing;
            }
            else
            {
                this._state = MusicState.Prepared;
            }
        }
    }

    // Halts rendering without freeing buffers. Caller must hold
    // _syncRoot.
    private void StopOutputLocked()
    {
        if (this._state is MusicState.Playing or MusicState.Paused)
        {
            this.OnStopOutput();
        }
    }

    // Applies the retention rules after a retain flag was cleared.
    // Caller must hold _syncRoot.
    private void SettleRetentionLocked()
    {
        if (this._prepareRetained || this._playbackRetained)
        {
            // Something still holds the buffers. A track whose playback
            // has ended must go back to the state it had just after
            // preparation, which means re-seeking to the start position.
            // That is decoder work, so it is queued rather than performed
            // on the caller's thread. Playback that's still wanted (an
            // Unprepare while playing) carries on, and the stop releases.
            if ((!this._playbackRetained) && (this._state is MusicState.Playing or MusicState.Paused))
            {
                this._prepareTask = this.BeginAcquireLocked();
            }

            return;
        }

        this.BeginReleaseLocked();
    }

    // Invalidates any in-flight acquire and queues the teardown.
    // Caller must hold _syncRoot.
    private void BeginReleaseLocked()
    {
        // Invalidate in-flight acquires so a late completion cannot
        // move the track back to Prepared/Playing.
        this._epoch++;
        this._request = PlaybackRequest.None;

        if (this._state is MusicState.Loaded or MusicState.Unloaded)
        {
            // Nothing is resident, or disposal has queued its release.
            return;
        }

        this._state = MusicState.Loaded;

        this._queue.Enqueue(this.OnReleaseAsync, ReleaseFailedTag);
    }

    private bool IsStale(
        int epoch)
    {
        lock (this._syncRoot)
        {
            return (this._epoch != epoch) || this._disposed;
        }
    }

    #endregion

    #region Disposal

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this.DisposeCore();
    }

    /// <summary>
    /// Disposes the track and waits, up to
    /// <paramref name="timeout"/>, for the queued teardown to finish.
    /// </summary>
    /// <remarks>
    /// This is the only blocking API on this type. Use it solely on
    /// shutdown paths that must not leak native handles — never on the
    /// game thread or from an activity lifecycle callback.
    /// </remarks>
    internal void DisposeAndDrain(
        TimeSpan timeout)
    {
        this.Dispose();

        try
        {
            this._queue.Current.Wait(timeout);
        }
        catch (AggregateException)
        {
            // Queued operations already trace their own failures.
        }
    }

    private void DisposeCore()
    {
        lock (this._syncRoot)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;

            this._prepareRetained = false;
            this._playbackRetained = false;
            this._request = PlaybackRequest.None;

            this.StopOutputLocked();

            // Bump the epoch so any in-flight acquire completion is
            // discarded, then always queue the final teardown: platform
            // strategies release their metadata-level resources (native
            // decoders, synchronization handles) there, so it must run
            // even from the Loaded state.
            this._epoch++;

            this._queue.Enqueue(this.OnReleaseAsync, ReleaseFailedTag);

            this._state = MusicState.Unloaded;
        }
    }

    #endregion

    private enum PlaybackRequest
    {
        None,
        Playing,
        Paused,
    }

    /// <summary>
    /// The start/end/loop configuration of a playback request.
    /// </summary>
    protected readonly struct PlaybackParameters(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop)
    {
        public TimeSpan? Start { get; } = start;

        public TimeSpan? End { get; } = end;

        public TimeSpan? LoopPoint { get; } = loopPoint;

        public bool Loop { get; } = loop;

        // NOTE: No start or loop point is the start of the music
        public bool IsSameRange(
            PlaybackParameters other)
        {
            return ((this.Start ?? TimeSpan.Zero) == (other.Start ?? TimeSpan.Zero))
                && (this.End == other.End)
                && ((this.LoopPoint ?? TimeSpan.Zero) == (other.LoopPoint ?? TimeSpan.Zero))
                && (this.Loop == other.Loop);
        }
    }
}
