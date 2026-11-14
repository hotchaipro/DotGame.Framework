// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

//#define INSTRUMENT
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotGame.Framework.Threading;

/// <summary>Runs work on the game thread: what's behind an await continuing there. Most code uses <see cref="GameThread"/> instead.</summary>
internal sealed class GameSynchronizationContext : SynchronizationContext
{
    internal static readonly GameSynchronizationContext GameThread = new();

    private readonly struct Continuation
    {
        internal Continuation(
            SendOrPostCallback sendOrPostCallback,
            object? state,
            Action? abandoned)
        {
            ArgumentNullException.ThrowIfNull(sendOrPostCallback);

            this.SendOrPostCallback = sendOrPostCallback;
            this.State = state;
            this.Abandoned = abandoned;
        }

        private readonly SendOrPostCallback SendOrPostCallback;
        private readonly object? State;

        // Tells whoever waits on this work that it will never run
        internal readonly Action? Abandoned;

        public void Invoke()
        {
            this.SendOrPostCallback(this.State);
        }
    }

    private readonly Queue<Continuation> _queue;
    private readonly Lock _syncObject = new();
    private static long _isInitialized;

    // Set from Shutdown until the next Initialize. Work posted in between belongs to a session
    // that has ended, and must not run against the next session's game. Guarded by _syncObject.
    private bool _isShutDown;

    // How long one drain keeps running what the work it ran has posted, before returning to the
    // host. Long enough for a chain of hops during startup, short enough that a continuation
    // polling with Task.Yield can't hang a page or a paused app.
    private const long DrainTimeCapMs = 50;

    // Upper bound on how long Initialize waits for a previous session's teardown
    // to release the one-shot guard before treating a still-set guard as a
    // genuine double-initialization. Android can start a replacement render
    // thread (and call Initialize) in the same process before the outgoing
    // render thread has finished its bounded stop pump and called Shutdown, so
    // the incoming thread must ride out that overlap instead of aborting the
    // process. Comfortably exceeds RenderThread's StopPumpCapMs cap.
    private const long InitializeHandoffTimeoutMs = 10000;

    // Adaptive per-frame budget for the steady-state continuation pump
    // (ExecutePendingContinuations). Owned here so every engine shares a
    // single self-tuning pump; engines only report the display refresh rate
    // via SetTargetFrameRate. Driven exclusively on the game thread by the
    // pump, so it needs no locking of its own.
    private readonly AdaptiveContinuationBudget _budget = new();

    // Stopwatch timestamp captured at the start of the previous adaptive pump,
    // used to self-measure the wall-clock interval between successive frames
    // as the AIMD feedback signal. Zero until the first adaptive pump runs.
    // Game-thread-only, like the pump itself.
    private long _lastPumpTimestamp;

    private GameSynchronizationContext()
    {
        this._queue = new Queue<Continuation>();
    }

    internal void Initialize()
    {
        // Claim the one-shot game-thread guard. Only one thread may own the game
        // thread role at a time, but across an Android same-process relaunch a
        // replacement render thread can reach here before the outgoing render
        // thread has finished tearing down and called Shutdown to release the
        // guard. Rather than fail fast on that legitimate hand-off overlap
        // (which would abort the process with SIGABRT), wait briefly for the
        // previous owner to release the guard before claiming it.
        if (Interlocked.CompareExchange(ref _isInitialized, 1, 0) == 1)
        {
            long start = Environment.TickCount64;

            SpinWait spinner = default;
            while (Interlocked.CompareExchange(ref _isInitialized, 1, 0) == 1)
            {
                if (Environment.TickCount64 - start > InitializeHandoffTimeoutMs)
                {
                    // The guard never cleared: this is a real double
                    // initialization (or a wedged prior game thread), not a
                    // hand-off, so fail fast as before.
                    FatalError.Terminate(0x61d20576, new InvalidOperationException("GameSynchronizationContext already initialized"));
                }

                // NOTE: Sleeps once it has spun a little: this can be a real wait, for a game
                // running its OnStop
                spinner.SpinOnce(sleep1Threshold: 20);
            }
        }

        lock (this._syncObject)
        {
            this._isShutDown = false;
        }

        _IsGameThread = true;

        SetSynchronizationContext(this);
    }

    /// <summary>
    /// Resets the one-shot game-thread state so a later <see cref="Initialize"/>
    /// can re-establish the game thread in the same process. Android can fully
    /// tear down the game and its render thread while keeping the process
    /// alive, then relaunch (a new activity spins up a new render thread); the
    /// static one-shot guard in <see cref="Initialize"/> would otherwise fail
    /// fast on that second initialization and abort the process (SIGABRT). Must
    /// be called on the game thread as the final teardown step, after the last
    /// continuation has run and immediately before the game thread exits.
    /// </summary>
    internal void Shutdown()
    {
        // Discard any continuations left over from the torn-down session so they
        // cannot leak into the next launch and run against a disposed
        // game/engine, and later posts with them.
        Continuation[] abandoned;
        lock (this._syncObject)
        {
            this._isShutDown = true;
            abandoned = this._queue.ToArray();
            this._queue.Clear();
        }

        // Whoever waits on that work isn't left waiting forever
        foreach (var continuation in abandoned)
        {
            continuation.Abandoned?.Invoke();
        }

        // Reset the adaptive pump's self-measured timing so the next session
        // begins from a clean feedback state instead of a stale frame delta.
        this._lastPumpTimestamp = 0L;

        // Detach the context and clear the game-thread marker on this exiting
        // thread, then release the one-shot guard last so a new thread's
        // Initialize cannot observe an initialized guard alongside stale
        // per-thread state while claiming the game-thread role.
        SetSynchronizationContext(null);
        _IsGameThread = false;

        Interlocked.Exchange(ref _isInitialized, 0);
    }

    /// <summary>
    /// Ensures the game synchronization context is set on the current thread.
    /// The .NET WASM runtime may reset SynchronizationContext.Current between
    /// JS-to-managed interop calls.
    /// </summary>
    internal void EnsureContext()
    {
        if (!_IsGameThread)
        {
            FatalError.Terminate(0x7c1bf46e, new InvalidOperationException($"EnsureContext requires the game thread. IsGameThread={_IsGameThread}, SynchronizationContext={Current}"));
        }

        if (Current != this)
        {
            SetSynchronizationContext(this);
        }
    }

    [ThreadStatic]
    private static bool _IsGameThread;

    /// <summary>Whether the calling thread is the game thread.</summary>
    internal static bool IsGameThread => _IsGameThread;

    /// <summary>Runs work on the game thread and waits for it.</summary>
    public override void Send(
#pragma warning disable CA1725 // Parameter names should match base declaration
        SendOrPostCallback callback,
#pragma warning restore CA1725 // Parameter names should match base declaration
        object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

#if INSTRUMENT
        if (Interlocked.Read(ref _isInitialized) == 0)
        {
            throw new Exception("Game synchronization context not initialized").WithTag(0x6093fb5a);
        }
#endif

        if (_IsGameThread)
        {
            callback(state);
        }
        else
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            this.Post((_) =>
            {
                try
                {
                    callback(state);

                    tcs.SetResult();
                }
                catch (Exception e)
                {
                    tcs.SetException(e);
                }
            }, null, () => tcs.TrySetCanceled());

            tcs.Task.Wait();
        }
    }

    /// <summary>Queues work for the game thread.</summary>
    public override void Post(
#pragma warning disable CA1725 // Parameter names should match base declaration
        SendOrPostCallback callback,
#pragma warning restore CA1725 // Parameter names should match base declaration
        object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

#if INSTRUMENT
        if (Interlocked.Read(ref _isInitialized) == 0)
        {
            throw new Exception("Game synchronization context not initialized").WithTag(0x61961f65);
        }
#endif

        this.Post(callback, state, null);
    }

    /// <summary>
    /// Queues work, with what to do if it will never run: the session ended first.
    /// </summary>
    internal void Post(
        SendOrPostCallback callback,
        object? state,
        Action? abandoned)
    {
        bool isQueued;
        lock (this._syncObject)
        {
            isQueued = !this._isShutDown;
            if (isQueued)
            {
                this._queue.Enqueue(new Continuation(callback, state, abandoned));
            }
        }

        if (isQueued)
        {
            this.WorkPosted?.Invoke();
        }
        else
        {
            abandoned?.Invoke();
        }
    }

    /// <summary>
    /// Called after work is posted, so a platform can see that it's drained soon even when
    /// no frame is coming. The WebGL engine sets it while starting, when a hidden page gets
    /// no animation frames, and clears it once the game has started. Called on the posting
    /// thread.
    /// </summary>
    internal Action? WorkPosted { get; set; }

    /// <summary>
    /// Reports the display refresh rate the adaptive continuation pump should
    /// tune against. Each engine calls this with its platform-specific frame
    /// rate (e.g. the Android display refresh rate, the iOS preferred frames
    /// per second, the desktop monitor refresh, or the WebGL loop cadence);
    /// non-positive or implausible values fall back to a 60 FPS target. Must
    /// be called on the game thread.
    /// </summary>
    /// <param name="displayRefreshHz">The target frame rate in hertz.</param>
    internal void SetTargetFrameRate(
        double displayRefreshHz)
    {
        this._budget.SetTargetFrameRate(displayRefreshHz);
    }

    /// <summary>
    /// Drains queued continuations using an adaptively tuned per-frame budget.
    /// This is the steady-state pump: it is called once per frame from
    /// <see cref="Platform.GameHost.Tick"/> (the single per-frame pump site) so that as
    /// many queued continuations as possible run each frame without pushing the
    /// frame past the display refresh interval. The interval between successive
    /// calls is self-measured and fed to the owned
    /// <see cref="AdaptiveContinuationBudget"/> as the AIMD feedback signal, so
    /// no external timing source is required. Must be called on the game thread.
    /// </summary>
    internal void ExecutePendingContinuations()
    {
        // Self-measure the wall-clock interval since the previous adaptive pump
        // and use it as the feedback signal. The first call has no previous
        // timestamp, so the delta is TimeSpan.Zero, which the controller treats
        // as "no signal" and holds the budget steady.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan lastFrameTime = this._lastPumpTimestamp != 0L
            ? System.Diagnostics.Stopwatch.GetElapsedTime(this._lastPumpTimestamp, now)
            : TimeSpan.Zero;
        this._lastPumpTimestamp = now;

        bool hasBacklog;
        lock (this._syncObject)
        {
            hasBacklog = this._queue.Count > 0;
        }

        TimeSpan budget = this._budget.Next(lastFrameTime, hasBacklog);

        this.PumpQueue(budget);
    }

    /// <summary>
    /// Drains the continuation queue with no per-frame budget. Used by engine
    /// startup pumps (before the game's steady-state frame loop is running)
    /// where continuations must make progress as fast as possible and there is
    /// no rendered frame to protect. What the drained work posts runs too, up to
    /// a time cap, so the host regains control from work that keeps posting
    /// itself. Does not touch the adaptive pump's timing state. Must be called
    /// on the game thread.
    /// </summary>
    internal void DrainPendingContinuations()
    {
        long start = Environment.TickCount64;

        do
        {
            this.PumpQueue(TimeSpan.Zero);
        }
        while (this.HasPendingContinuations()
            && ((Environment.TickCount64 - start) < DrainTimeCapMs));
    }

    private bool HasPendingContinuations()
    {
        lock (this._syncObject)
        {
            return this._queue.Count > 0;
        }
    }

    private void PumpQueue(
        TimeSpan budget)
    {
#if INSTRUMENT
        if (Interlocked.Read(ref _isInitialized) == 0)
        {
            throw new Exception("Game synchronization context not initialized").WithTag(0x3735c41e);
        }
#endif

        // Only what's queued now runs: what it posts waits for the next pump, so
        // `while (!ready) await Task.Yield();` waits for a frame rather than
        // spinning the budget away. A zero budget means unlimited, which is the
        // behavior startup pumps rely on. With a positive budget the pump always
        // runs at least one continuation (so progress is guaranteed and the GL
        // thread never wedges its own pump), then stops once the elapsed time
        // exceeds the budget, leaving the rest queued for the next frame. This
        // spreads bursts of expensive engine-restricted continuations — most
        // notably cold-start shader compilation/linking on the GL thread —
        // across frames so a single frame cannot monopolize the GPU driver and
        // starve the system RenderThread the main thread synchronously waits on.
        bool hasBudget = budget > TimeSpan.Zero;
        long startTimestamp = hasBudget ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        long budgetTicks = hasBudget
            ? (long)(budget.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)
            : 0L;

        int remaining;
        lock (this._syncObject)
        {
            remaining = this._queue.Count;
        }

        for (; remaining > 0; remaining--)
        {
            Continuation continuation;
            lock (this._syncObject)
            {
                if (!this._queue.TryDequeue(out continuation))
                {
                    break;
                }
            }

            //Trace.WriteDebug(0x34aa80ec, "Start Continuation");

            try
            {
                continuation.Invoke();
            }
            catch (OperationCanceledException)
            {
                // Benign: an unobserved cancellation (the managed equivalent of
                // the browser's AbortError) posted back to the game thread — for
                // example an async void handler whose awaited operation was
                // canceled. Cancellation is control flow, not a crash, so it is
                // swallowed here to match GameThread.Post and the
                // AbortError handling in main.js.
            }
            catch (Exception e)
            {
                // A continuation that faults after an await would otherwise
                // escape the pump, escape the frame, and cross the
                // JSExport boundary where the managed exception is swallowed on
                // some devices (a tap appears to "do nothing") or wedges the
                // animation-frame loop on others. The most common source is an
                // async void handler: AsyncVoidMethodBuilder.SetException posts
                // the unhandled exception back to this captured synchronization
                // context, which rethrows it here. Route every such escape
                // through the fatal path so the crash dialog is always shown.
                FatalError.Terminate(0x9c17be5a, e);
            }

            //Trace.WriteDebug(0x34aa80ec, "End Continuation");

            // NOTE: Work that lost the context, as the browser's runtime can make it, doesn't take
            // it from what runs next
            this.EnsureContext();

            if (hasBudget &&
                (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) >= budgetTicks)
            {
                // Per-frame budget exhausted; defer the remaining queued
                // continuations to the next frame's pump.
                break;
            }
        }
    }

    /// <summary>Returns this context: there's one game thread.</summary>
    public override SynchronizationContext CreateCopy()
    {
        // Re-use the same synchronization context
        return this;
    }
}
