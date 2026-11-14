// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Diagnostics;

namespace DotGame.Framework.Diagnostics;

/// <summary>Diagnostic messages, each with a tag: a number unique in the source, so a message can be found from a log.</summary>
public static class Trace
{
    private static ITraceWriter _writer = new DefaultTraceWriter();

    /// <summary>A fault the game can't recover from, just before the process ends: the place to save a crash report. Raised on whichever thread faulted.</summary>
    public static event EventHandler<FatalExceptionEventArgs>? FatalException;

    /// <summary>
    /// Raised for a failure the game runs on without, such as music that can't play, so a crash
    /// reporter can record it. Raised once per failure site per run, on whichever thread failed,
    /// so a handler must be thread-safe and quick.
    /// </summary>
    public static event EventHandler<NonFatalExceptionEventArgs>? NonFatalException;

    internal static void RaiseNonFatalException(
        uint tag,
        Exception exception)
    {
        NonFatalException?.Invoke(null, new NonFatalExceptionEventArgs(tag, exception));
    }

    internal static void RaiseFatalException(
        uint tag,
        Exception? exception)
    {
        FatalException?.Invoke(null, new FatalExceptionEventArgs(tag, exception));
    }

    /// <summary>
    /// Where trace output goes. By default it's written to <see cref="System.Diagnostics.Trace"/>.
    /// Replace it to send the output to your own logging.
    /// </summary>
    public static ITraceWriter Writer
    {
        get => _writer;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _writer = value;
        }
    }

    /// <summary>
    /// The least important messages that are written: <see cref="TraceLevel.Information"/> unless
    /// a game changes it, as a release build might to <see cref="TraceLevel.Warning"/>.
    /// </summary>
    public static TraceLevel MinLevel { get; set; } = TraceLevel.Information;

    /// <summary>Writes a message at the information level, with an exception if there is one.</summary>
    public static void Write(
        uint tag,
        string? message = null,
        Exception? exception = null)
    {
        Write(TraceLevel.Information, tag, message, exception);
    }

    /// <summary>Writes a message at the verbose level.</summary>
    public static void WriteVerbose(
        uint tag,
        string? message = null)
    {
        Write(TraceLevel.Verbose, tag, message, null);
    }

    /// <summary>Writes a message at the warning level.</summary>
    public static void WriteWarning(
        uint tag,
        string? message = null,
        Exception? exception = null)
    {
        Write(TraceLevel.Warning, tag, message, exception);
    }

    /// <summary>Writes a message at the error level.</summary>
    public static void WriteError(
        uint tag,
        string? message = null,
        Exception? exception = null)
    {
        Write(TraceLevel.Error, tag, message, exception);
    }

    private static void Write(
        TraceLevel level,
        uint tag,
        string? message,
        Exception? exception)
    {
        if (level >= MinLevel)
        {
            _writer.Write(level, tag, message, exception);
        }
    }

    /// <summary>Writes a message in Debug builds only: the call and its arguments are compiled out of Release.</summary>
    [Conditional("DEBUG")]
    public static void WriteDebug(
        uint tag,
        string? message = null,
        Exception? exception = null)
    {
        Write(TraceLevel.Verbose, tag, message, exception);
    }
}
