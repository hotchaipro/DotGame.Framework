// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotGame.Framework.Threading;

/// <summary>Reaches the game thread from another thread: most of the framework must be used from it.</summary>
public static class GameThread
{
    /// <summary>Whether the calling thread is the game thread.</summary>
    public static bool IsCurrent => GameSynchronizationContext.IsGameThread;

    /// <summary>
    /// Ends the game if the calling thread isn't the game thread, or if it has lost the game thread's
    /// synchronization context, so that an await there wouldn't come back to it. This runs in every
    /// build, and in a Debug build stops in an attached debugger first.
    /// </summary>
    /// <param name="tag">Identifies this check in the report.</param>
    public static void VerifyCurrent(
        uint tag)
    {
        var context = SynchronizationContext.Current;
        if ((!IsCurrent) || (context != GameSynchronizationContext.GameThread))
        {
            var message = $"[{tag:x8}] Expected game thread. IsCurrent={IsCurrent}, SynchronizationContext={context}";

            FatalError.Terminate(tag, new InvalidOperationException(message));
        }
    }

    /// <summary>Queues work for the game thread and returns. An exception it throws ends the game, as one in a frame does.</summary>
    public static void Post(
        Action action)
    {
        GameSynchronizationContext.GameThread.Post((_) =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException)
                {
                    // Crash the process
                    FatalError.Terminate(0xfee135b7, e);
                }
            }
        }, null);
    }

    /// <summary>Queues work for the game thread and returns. An exception it throws ends the game, as one in a frame does.</summary>
    public static void Post(
        Func<Task> asyncFunction)
    {
        GameSynchronizationContext.GameThread.Post(async (_) =>
            {
                try
                {
                    await asyncFunction();
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        // Crash the process
                        FatalError.Terminate(0x6abde8c4, e);
                    }
                }
            }, null);
    }

    /// <summary>Runs work on the game thread, and completes with its outcome.</summary>
    public static Task InvokeAsync(
        Action action)
    {
        if (GameSynchronizationContext.IsGameThread)
        {
            EnsureContext();

            try
            {
                action();

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return FailedTask(e);
            }
        }
        else
        {
            // NOTE: Runs a worker's continuation on the pool, not inside the game thread's pump
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            GameSynchronizationContext.GameThread.Post((_) =>
            {
                try
                {
                    action();

                    tcs.TrySetResult();
                }
                catch (Exception e)
                {
                    SetFailure(tcs, e);
                }
            }, null, () => tcs.TrySetCanceled());

            return tcs.Task;
        }
    }

    /// <summary>Runs work on the game thread, and completes with its result.</summary>
    public static Task<T> InvokeAsync<T>(
        Func<T> action)
    {
        if (GameSynchronizationContext.IsGameThread)
        {
            EnsureContext();

            try
            {
                return Task.FromResult(action());
            }
            catch (Exception e)
            {
                return FailedTask<T>(e);
            }
        }
        else
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            GameSynchronizationContext.GameThread.Post((_) =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception e)
                {
                    SetFailure(tcs, e);
                }
            }, null, () => tcs.TrySetCanceled());

            return tcs.Task;
        }
    }

    /// <summary>Runs work on the game thread, and completes with its outcome.</summary>
    public static Task InvokeAsync(
        Func<Task> action)
    {
        if (GameSynchronizationContext.IsGameThread)
        {
            EnsureContext();

            try
            {
                return action();
            }
            catch (Exception e)
            {
                return FailedTask(e);
            }
        }
        else
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            GameSynchronizationContext.GameThread.Post(async (_) =>
            {
                try
                {
                    await action();

                    tcs.TrySetResult();
                }
                catch (Exception e)
                {
                    SetFailure(tcs, e);
                }
            }, null, () => tcs.TrySetCanceled());

            return tcs.Task;
        }
    }

    /// <summary>Runs work on the game thread, and completes with its result.</summary>
    public static Task<T> InvokeAsync<T>(
        Func<Task<T>> action)
    {
        if (GameSynchronizationContext.IsGameThread)
        {
            EnsureContext();

            try
            {
                return action();
            }
            catch (Exception e)
            {
                return FailedTask<T>(e);
            }
        }
        else
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            GameSynchronizationContext.GameThread.Post(async (_) =>
            {
                try
                {
                    tcs.TrySetResult(await action());
                }
                catch (Exception e)
                {
                    SetFailure(tcs, e);
                }
            }, null, () => tcs.TrySetCanceled());

            return tcs.Task;
        }
    }

    // The WASM runtime can clear SynchronizationContext.Current across a JS->managed interop hop
    // that resumed us on the game thread (e.g. a ConfigureAwait(false) continuation);
    // re-establish it before running inline. No-op on multithreaded platforms where Current
    // already equals the game context whenever IsCurrent is true.
    private static void EnsureContext()
    {
        GameSynchronizationContext.GameThread.EnsureContext();
    }

    // NOTE: A body's failure reaches the caller through the task from every thread, so the same
    // call doesn't need two ways of handling it
    private static Task FailedTask(
        Exception e)
    {
        return (e is OperationCanceledException canceled)
            ? Task.FromCanceled(CanceledToken(canceled))
            : Task.FromException(e);
    }

    private static Task<T> FailedTask<T>(
        Exception e)
    {
        return (e is OperationCanceledException canceled)
            ? Task.FromCanceled<T>(CanceledToken(canceled))
            : Task.FromException<T>(e);
    }

    // Task.FromCanceled requires a canceled token, which ThrowIfCancellationRequested's has and a
    // bare OperationCanceledException's doesn't
    private static System.Threading.CancellationToken CanceledToken(
        OperationCanceledException e)
    {
        return e.CancellationToken.IsCancellationRequested
            ? e.CancellationToken
            : new System.Threading.CancellationToken(canceled: true);
    }

    private static void SetFailure(
        TaskCompletionSource tcs,
        Exception e)
    {
        if (e is OperationCanceledException canceled)
        {
            tcs.TrySetCanceled(canceled.CancellationToken);
        }
        else
        {
            tcs.TrySetException(e);
        }
    }

    private static void SetFailure<T>(
        TaskCompletionSource<T> tcs,
        Exception e)
    {
        if (e is OperationCanceledException canceled)
        {
            tcs.TrySetCanceled(canceled.CancellationToken);
        }
        else
        {
            tcs.TrySetException(e);
        }
    }
}
