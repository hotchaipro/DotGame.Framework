// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;

namespace DotGame.Framework.Diagnostics;

/// <summary>
/// Reports a failure the game runs on without, such as music that can't play. A trace alone would
/// reach no one in a shipped game, and the game would just be silent.
/// </summary>
internal static class NonFatalError
{
    private static readonly Lock _syncRoot = new();
    private static readonly HashSet<uint> _raisedTags = [];

    /// <summary>
    /// Traces <paramref name="exception"/> and raises <see cref="Trace.NonFatalException"/>, on the
    /// calling thread. Each site raises it once per run, as a failure that repeats (a bad track
    /// on every loop) is one problem. Doesn't throw.
    /// </summary>
    public static void Report(
        uint tag,
        Exception exception)
    {
        bool isFirst;

        lock (_syncRoot)
        {
            isFirst = _raisedTags.Add(tag);
        }

        try
        {
            Trace.Write(tag, exception: exception);

            if (isFirst)
            {
                Trace.RaiseNonFatalException(tag, exception);
            }
        }
        catch
        {
            // Diagnostics must never turn a non-fatal failure into a fatal one
        }
    }

    /// <summary>
    /// Forgets which sites have reported, for tests.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (_syncRoot)
        {
            _raisedTags.Clear();
        }
    }
}
