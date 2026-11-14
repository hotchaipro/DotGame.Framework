// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Diagnostics;

/// <summary>Where trace output goes. Set one on <see cref="Trace.Writer"/> to send it to your own logging.</summary>
public interface ITraceWriter
{
    /// <summary>
    /// Records a tagged event with an optional message and exception.
    /// </summary>
    /// <param name="level">How much it matters. <see cref="Trace.MinLevel"/> has already been applied,
    /// except to <c>Trace.WriteDebug</c>, which is <see cref="TraceLevel.Verbose"/> and always arrives in
    /// Debug builds.</param>
    /// <param name="tag">The number that identifies the call site.</param>
    /// <param name="message">The message, if any.</param>
    /// <param name="exception">The exception, if any.</param>
    void Write(
        TraceLevel level,
        uint tag,
        string? message,
        Exception? exception);
}
