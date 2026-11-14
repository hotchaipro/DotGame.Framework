// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using DotGame.Framework.Diagnostics;
using DotGame.Framework.Platform;

namespace DotGame.Framework.Platform.Common;

/// <summary>
/// A per-instance, single-flight serializer for the expensive
/// background work of a music track (acquiring and releasing decode
/// buffers, device streams, and native playback objects).
/// </summary>
/// <remarks>
/// <para>
/// Enqueued operations run one at a time, in submission order, on the
/// thread pool. This lets the audio state machine schedule an acquire
/// immediately after a release (or vice versa) from the game thread
/// without blocking to preserve ordering, and guarantees a platform
/// strategy never sees two overlapping resource operations.
/// </para>
/// <para>
/// <see cref="Enqueue"/> never blocks the calling thread and never
/// throws from the queued work: a faulted operation is handled by
/// <see cref="GameHost.ReportAudioFailure"/> and the chain continues,
/// so one failure cannot wedge the queue forever.
/// </para>
/// </remarks>
internal sealed class AsyncOperationQueue
{
    private readonly Lock _syncRoot = new();

    // The tail of the chain. Every enqueued operation continues from
    // the previous tail, which serializes execution. A failing
    // operation is handled below rather than propagating.
    private Task _tail = Task.CompletedTask;

    /// <summary>
    /// The task representing all work queued so far. Awaiting it waits
    /// for the queue to drain to the point of the last enqueue.
    /// </summary>
    internal Task Current
    {
        get
        {
            lock (this._syncRoot)
            {
                return this._tail;
            }
        }
    }

    /// <summary>
    /// Schedules <paramref name="operation"/> to run after all
    /// previously enqueued operations have completed. Returns
    /// immediately; the returned task completes when this operation
    /// (and everything queued before it) has finished.
    /// </summary>
    /// <param name="operation">The work to perform.</param>
    /// <param name="traceTag">Tag identifying the site if the
    /// operation fails.</param>
    internal Task Enqueue(
        Func<Task> operation,
        uint traceTag)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (this._syncRoot)
        {
            // Continue off the current tail. The operation and its tag
            // travel as arguments, not as a boxed ContinueWith state
            // object: unboxing a (Func<Task>, uint) state intermittently
            // threw InvalidCastException under the iOS AOT runtime.
            var next = RunAfter(this._tail, operation, traceTag);

            this._tail = next;

            return next;
        }
    }

    private static async Task RunAfter(
        Task previous,
        Func<Task> operation,
        uint traceTag)
    {
        // ForceYielding always continues on the thread pool, so the
        // thread that completed the previous operation (which may be an
        // audio callback thread) never runs this one inline, and the
        // caller of Enqueue returns before the operation starts.
        // SuppressThrowing runs the operation whatever the previous one
        // did, as the queue never stops for a failure.
        await previous.ConfigureAwait(
            ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);

        try
        {
            var task = operation();

            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // One track's failure isn't worth a player's game. A trace
            // alone once turned real bugs into silent, unexplained
            // silence that was very hard to diagnose, which is why this
            // reports it, and ends a Debug build.
            GameHost.ReportAudioFailure(traceTag, exception);
        }
    }
}
