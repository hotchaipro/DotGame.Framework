// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using DotGame.Framework.Platform;

namespace DotGame.Framework.Platform.Common;

internal static class SafeAsync
{
    public static void Invoke(
        uint tag,
        Task asyncTask)
    {
        ArgumentNullException.ThrowIfNull(asyncTask);

        _ = HandleInvoke(tag, asyncTask);
    }

    public static void Invoke(
        uint tag,
        Func<Task> asyncFunction)
    {
        ArgumentNullException.ThrowIfNull(asyncFunction);

        _ = HandleInvoke(tag, asyncFunction);
    }

    public static Action Action(
        uint tag,
        Func<Task> asyncFunction)
    {
        ArgumentNullException.ThrowIfNull(asyncFunction);

        return () => _ = HandleInvoke(tag, asyncFunction);
    }

    // NOTE: Calls the function inside the catch, so a function that isn't async and throws is
    // handled as one that faults after an await
    private static async Task HandleInvoke(
        uint tag,
        Func<Task> asyncFunction)
    {
        try
        {
            await asyncFunction();
        }
        catch (Exception e)
        {
            if (e is not OperationCanceledException)
            {
                GameHost.Terminate(tag, e);
            }
        }
    }

    private static async Task HandleInvoke(
        uint tag,
        Task asyncTask)
    {
        try
        {
            await asyncTask;
        }
        catch (Exception e)
        {
            // NOTE: Async event handlers should not cause a fatal error 
            // when a task is canceled.
            if (e is not OperationCanceledException)
            {
                // Fatal error
                GameHost.Terminate(tag, e);
            }
        }
    }
}
