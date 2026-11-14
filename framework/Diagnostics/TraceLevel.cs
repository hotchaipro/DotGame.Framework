// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Diagnostics;

/// <summary>
/// How much a trace message matters, for <see cref="Trace.MinLevel"/>.
/// </summary>
public enum TraceLevel
{
    /// <summary>Detail for following what happened.</summary>
    Verbose,

    /// <summary>The ordinary record of what a game did; <see cref="Trace.Write(uint, string?, System.Exception?)"/>.</summary>
    Information,

    /// <summary>Something went wrong and the game carries on.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error,
}
