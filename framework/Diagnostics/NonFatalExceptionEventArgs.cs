// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Diagnostics;

/// <summary>A fault the game carries on from, reported for the game to log.</summary>
public sealed class NonFatalExceptionEventArgs : EventArgs
{
    internal NonFatalExceptionEventArgs(
        uint tag,
        Exception exception)
    {
        this.Tag = tag;
        this.Exception = exception;
    }

    /// <summary>Where it was reported from: a number unique in the source, to search for.</summary>
    public uint Tag { get; }

    /// <summary>What went wrong.</summary>
    public Exception Exception { get; }
}
