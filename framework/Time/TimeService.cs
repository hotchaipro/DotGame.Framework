// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotGame.Framework.Time;

/// <summary>
/// The game's clock: how much game time has passed, how fast it runs, and waiting for it.
/// </summary>
public sealed class TimeService
{
    private TimeSpan _lastTimestamp;
    private bool _hasLastTimestamp;
    private TimeSpan _maxDelta = DefaultMaxDelta;
    private TimeSpan _totalTime = TimeSpan.Zero;
    private TimeSpan _deltaTime = TimeSpan.Zero;
    private TimeSpan _unscaledTotalTime = TimeSpan.Zero;
    private TimeSpan _unscaledDeltaTime = TimeSpan.Zero;
    private float _scale = 1;
    private TimeSpan? _fixedStep;
    private TimeSpan _fixedAccumulator = TimeSpan.Zero;
    private int _fixedStepsThisFrame;
    private bool _isInFixedStep;

    /// <summary>
    /// The most fixed steps a frame runs. Past this, game time slows rather than a slow step
    /// making the next frame slower still, as <see cref="MaxDelta"/> does through a stall.
    /// </summary>
    internal const int MaxFixedStepsPerFrame = 8;

    // In the order they're due, and for the same time in the order they were scheduled
    private readonly List<DelayHandle> _delayedActions = new();
    private long _nextDelaySequence;

    internal TimeService()
    {
    }

    /// <summary>
    /// All the game time since the game started.
    /// </summary>
    public TimeSpan Total => this._totalTime;

    /// <summary>
    /// The game time this update covers: the time since the last frame, at most a third of a second
    /// so that a stall slows the game rather than jumping it ahead, or in <c>Game.OnFixedUpdate</c>
    /// the length of a fixed step.
    /// </summary>
    public TimeSpan Delta => this._isInFixedStep ? this._fixedStep.GetValueOrDefault() : this._deltaTime;

    /// <summary>
    /// <see cref="Delta"/> in seconds, for the arithmetic of movement and animation.
    /// </summary>
    public float DeltaSeconds => (float)this.Delta.TotalSeconds;

    /// <summary>
    /// How fast game time runs: 1 is real time, 0.5 is half speed for slow motion, and 0 stops it,
    /// as a pause menu might. It applies to <see cref="Delta"/>, <see cref="Total"/>, delays and
    /// fixed steps, so what's meant to carry on while game time is stopped uses
    /// <see cref="UnscaledDelta"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public float Scale
    {
        get => this._scale;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this._scale = value;
        }
    }

    /// <summary>
    /// All the time since the game started, whatever <see cref="Scale"/> has been. It stops while
    /// the game is inactive, as game time does.
    /// </summary>
    public TimeSpan UnscaledTotal => this._unscaledTotalTime;

    /// <summary>
    /// The time since the last frame whatever <see cref="Scale"/> is, in <c>Game.OnFixedUpdate</c> too.
    /// </summary>
    public TimeSpan UnscaledDelta => this._unscaledDeltaTime;

    /// <summary>
    /// <see cref="UnscaledDelta"/> in seconds, for the arithmetic of movement and animation.
    /// </summary>
    public float UnscaledDeltaSeconds => (float)this._unscaledDeltaTime.TotalSeconds;

    /// <summary>
    /// How far game time is from the last fixed step to the next, from 0 up to 1: draw at
    /// <c>Vector2.Lerp(previous, current, Time.FixedAlpha)</c> to be smooth on any display.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>Game.FixedUpdateRate</c> isn't set, so there are no fixed steps.</exception>
    public float FixedAlpha
    {
        get
        {
            if (this._fixedStep is not TimeSpan step)
            {
                throw new InvalidOperationException("Game.FixedUpdateRate must be set before reading FixedAlpha.");
            }

            return MathF.Min((float)(this._fixedAccumulator.TotalSeconds / step.TotalSeconds), 1f);
        }
    }

    /// <summary>
    /// The length of each <c>Game.OnFixedUpdate</c>, in game time, from <c>Game.FixedUpdateRate</c>,
    /// or null while fixed updates are off, when this clock keeps no count of them.
    /// </summary>
    internal TimeSpan? FixedStep
    {
        get => this._fixedStep;
        set
        {
            if (value is TimeSpan step)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);
            }
            else
            {
                this._fixedAccumulator = TimeSpan.Zero;
                this._isInFixedStep = false;
            }

            this._fixedStep = value;
        }
    }

    /// <summary>
    /// Takes a fixed step from the time accumulated, if there's a whole one and the frame has
    /// room for it. <see cref="Delta"/> is the step until the next call.
    /// </summary>
    internal bool TryTakeFixedStep()
    {
        this._isInFixedStep = false;

        if ((this._fixedStep is not TimeSpan step) || (this._fixedAccumulator < step))
        {
            return false;
        }

        if (this._fixedStepsThisFrame == MaxFixedStepsPerFrame)
        {
            // What the frame can't run is dropped, rather than owed to the next frame
            this._fixedAccumulator = TimeSpan.FromTicks(this._fixedAccumulator.Ticks % step.Ticks);
            return false;
        }

        this._fixedStepsThisFrame++;
        this._fixedAccumulator -= step;
        this._isInFixedStep = true;
        return true;
    }

    /// <summary>
    /// The default <see cref="MaxDelta"/>: a third of a second.
    /// </summary>
    internal static readonly TimeSpan DefaultMaxDelta = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 3);

    /// <summary>
    /// The longest a single frame can advance game time. A frame that took longer (after a
    /// debugger breakpoint, a long garbage collection, or the process being suspended without
    /// a lifecycle event) advances game time by this much, so animations, timers and physics
    /// slow down through a stall rather than jumping ahead.
    /// </summary>
    internal TimeSpan MaxDelta
    {
        get => this._maxDelta;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            this._maxDelta = value;
        }
    }

    /// <summary>
    /// Schedules an action to be executed on the game thread after the specified delay.
    /// </summary>
    /// <param name="delay">The amount of game time to wait before executing the action.</param>
    /// <param name="action">The action to execute.</param>
    /// <returns>A handle that can be used to cancel the delayed action.</returns>
    public DelayHandle Delay(
        TimeSpan delay,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var handle = new DelayHandle(this._totalTime + delay, action);
        this.AddDelay(handle);

        return handle;
    }

    /// <summary>
    /// Completes at the next frame's update. Time stops while the game is paused, so this waits
    /// for the game to resume.
    /// </summary>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public Task NextFrameAsync(
        CancellationToken cancellationToken = default)
    {
        return this.DelayAsync(TimeSpan.Zero, cancellationToken);
    }

    /// <summary>
    /// Returns a task that completes on the game thread after the specified game-time delay.
    /// </summary>
    /// <param name="delay">The amount of game time to wait.</param>
    /// <param name="cancellationToken">An optional token to cancel the delay.</param>
    /// <returns>A task that completes on the game thread after the delay has elapsed.</returns>
    /// <remarks>
    /// Game time runs from the start of startup, and stops while the game is inactive and once
    /// it has stopped: a delay awaited in <c>OnPause</c> finishes after it resumes, and one
    /// awaited in <c>OnStop</c> doesn't finish.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        // NOTE: Code after the await runs in the pump, under its budget, like code after any other
        // await, rather than inside this service's list of delays
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // NOTE: The registration goes with the delay, so a loop of delays under one long-lived
        // token doesn't grow without limit
        CancellationTokenRegistration registration = default;
        var handle = new DelayHandle(this._totalTime + delay, () =>
        {
            registration.Dispose();
            tcs.TrySetResult();
        });
        this.AddDelay(handle);

        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() =>
            {
                handle.Cancel();
                tcs.TrySetCanceled(cancellationToken);
            });
        }

        return tcs.Task;
    }

    private void AddDelay(
        DelayHandle handle)
    {
        handle.Sequence = this._nextDelaySequence++;

        int index = this._delayedActions.Count;
        while ((index > 0) && (this._delayedActions[index - 1].TriggerTime > handle.TriggerTime))
        {
            index--;
        }

        this._delayedActions.Insert(index, handle);
    }

    /// <summary>
    /// Advances game time to a frame at <paramref name="frameTimestamp"/>, a monotonic time from
    /// the platform's frame source (only differences between timestamps are used), and runs the
    /// delayed actions that are due. Called by the host each frame.
    /// </summary>
    internal void Update(
        TimeSpan frameTimestamp)
    {
        // NOTE: The first frame after a reset has no previous timestamp to measure from, so it
        // repeats the last frame's elapsed time (zero after Reset), which is the best estimate
        // of a frame's length
        if (this._hasLastTimestamp)
        {
            var elapsed = frameTimestamp - this._lastTimestamp;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
            else if (elapsed > this._maxDelta)
            {
                elapsed = this._maxDelta;
            }

            this._unscaledDeltaTime = elapsed;
            this._deltaTime = elapsed * this._scale;
        }

        this._lastTimestamp = frameTimestamp;
        this._hasLastTimestamp = true;
        this._totalTime += this._deltaTime;
        this._unscaledTotalTime += this._unscaledDeltaTime;

        if (this._fixedStep is not null)
        {
            this._fixedAccumulator += this._deltaTime;
            this._fixedStepsThisFrame = 0;
        }

        this.ExecuteDelayedActions();
    }

    private void ExecuteDelayedActions()
    {
        // NOTE: What an action schedules waits for the next frame, even if it's due already, so
        // an action that schedules itself with no delay can't hang the frame
        long scheduledBefore = this._nextDelaySequence;

        int i = 0;
        while ((i < this._delayedActions.Count)
            && (this._totalTime >= this._delayedActions[i].TriggerTime))
        {
            var entry = this._delayedActions[i];
            if (entry.Sequence >= scheduledBefore)
            {
                i++;
                continue;
            }

            this._delayedActions.RemoveAt(i);
            entry.Action?.Invoke();

            // An action can schedule or cancel others
            i = 0;
        }
    }

    /// <summary>
    /// Forgets the last frame's timestamp, so the next frame doesn't count a stall (startup, a
    /// pause, a lost graphics context's recovery) as elapsed game time.
    /// </summary>
    internal void ResetFrameClock()
    {
        this._hasLastTimestamp = false;
    }

    /// <summary>
    /// Starts the frames from nothing, when the game has started: the first frame has no
    /// elapsed time.
    /// </summary>
    internal void ResetFrameTime()
    {
        this._hasLastTimestamp = false;
        this._deltaTime = TimeSpan.Zero;
        this._unscaledDeltaTime = TimeSpan.Zero;
    }

    /// <summary>
    /// Resets the game time to zero, once the game has initialized.
    /// </summary>
    internal void Reset()
    {
        // NOTE: A delay scheduled before this still waits for the same time, so a game that awaits
        // one in OnInitialize isn't left waiting forever
        foreach (var delay in this._delayedActions)
        {
            delay.TriggerTime -= this._totalTime;
        }

        this._hasLastTimestamp = false;
        this._totalTime = TimeSpan.Zero;
        this._deltaTime = TimeSpan.Zero;
        this._unscaledTotalTime = TimeSpan.Zero;
        this._unscaledDeltaTime = TimeSpan.Zero;
    }
}
