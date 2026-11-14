// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace DotGame.Framework.Diagnostics;

/// <summary>
/// Terminates the process for an unrecoverable framework error while preserving
/// the managed call stack in crash telemetry.
/// </summary>
/// <remarks>
/// <para>
/// A bare <see cref="Environment.FailFast(string, Exception)"/> aborts through
/// the native runtime (raising SIGABRT on Android/Mono) without routing the
/// exception through the managed unhandled-exception handlers a host installs,
/// so the resulting crash report contains only native frames (for example
/// <c>libmonosgen</c>/<c>libart</c>/<c>libmonochrome</c>) and no managed stack.
/// Compounding that, abort sites frequently pass a freshly constructed
/// exception that was never thrown, whose <see cref="Exception.StackTrace"/> is
/// therefore empty — so even a report that captured it would show no frames.
/// </para>
/// <para>
/// <see cref="Terminate"/> closes both gaps: it stamps the current managed call
/// stack onto the exception when it has none, reports it through
/// <see cref="Trace"/> (which a crash-reporting host records via its
/// managed-exception channel, producing a grouped managed stack), and only then
/// calls <see cref="Environment.FailFast(string, Exception)"/>. Route every
/// framework abort site through this method instead of calling
/// <see cref="Environment.FailFast(string, Exception)"/> directly.
/// </para>
/// <para>
/// The browser is the exception to that last step. There the runtime shares its
/// lifetime with the page showing the crash report, so aborting would destroy
/// the report; <see cref="Terminate"/> unwinds by rethrowing instead. It still
/// never returns, so abort sites read the same on every platform.
/// </para>
/// </remarks>
internal static class FatalError
{
    // Set once the fatal event has been raised, so the first (richest) crash
    // report is never replaced by a later one describing the unwind. See the
    // use site below.
    private static int _fatalRaised;

    // Unwinds as on the browser instead of ending the process, so tests can run this path
    private static bool _unwindForTests;

    /// <summary>
    /// Makes <see cref="Terminate"/> unwind as it does on the browser, and raise its event again,
    /// for a test of a path that ends here.
    /// </summary>
    internal static void SetUnwindForTests(
        bool unwind)
    {
        _unwindForTests = unwind;
        _fatalRaised = 0;
    }

    /// <summary>
    /// Reports <paramref name="exception"/> through the fatal trace channel with
    /// a guaranteed managed stack, then terminates the process. Does not return.
    /// </summary>
    /// <remarks>
    /// On the browser there is no process to abort: the runtime hosts the same
    /// page as the crash dialog, so aborting it would tear down the report the
    /// user is being shown. There, this method unwinds by rethrowing instead.
    /// </remarks>
    /// <param name="tag">Diagnostic tag identifying the abort site.</param>
    /// <param name="exception">
    /// The exception describing the fatal condition. If it was never thrown (its
    /// <see cref="Exception.StackTrace"/> is <see langword="null"/>), the current
    /// managed call stack is stamped onto it so the crash report is diagnosable.
    /// </param>
    [DoesNotReturn]
    public static void Terminate(
        uint tag,
        Exception? exception)
    {
        if (FeatureSwitches.IsDebug && System.Diagnostics.Debugger.IsAttached)
        {
            System.Diagnostics.Debugger.Break();
        }

        // A freshly constructed (never-thrown) exception carries no stack trace,
        // so a crash reporter would record an empty managed stack. Stamp the
        // current managed call stack onto it so the report identifies where the
        // fatal condition arose. SetCurrentStackTrace throws if a stack is
        // already present, so only apply it when the exception has none.
        if ((exception is not null) && (exception.StackTrace is null))
        {
            try
            {
                ExceptionDispatchInfo.SetCurrentStackTrace(exception);
            }
            catch
            {
                // Never let stack stamping prevent the report/abort below.
            }
        }

        try
        {
            Trace.Write(tag, CaptureAbortSite(exception), exception);
        }
        catch
        {
            // Diagnostics must never suppress the abort.
        }

        // First fatal wins. On the browser this method unwinds as an exception
        // (below), which reaches the host's global error handler and is routed
        // straight back into managed code -- re-entering here. Raising the
        // event again would replace the first, richest crash report with a
        // wrapper describing the unwind. Later fatals are still traced above,
        // so nothing is lost from the ring buffer.
        if (Interlocked.Exchange(ref _fatalRaised, 1) == 0)
        {
            try
            {
                Trace.RaiseFatalException(tag, exception);
            }
            catch
            {
                // Diagnostics must never suppress the abort.
            }
        }

        if (!OperatingSystem.IsBrowser() && !_unwindForTests)
        {
            Environment.FailFast(exception?.Message ?? "Fatal error", exception);
        }

        // The browser has no process to abort. FailFast would kill the runtime
        // hosting the very page the crash dialog is on, and returning is worse:
        // every abort site is a catch block, so control would resume as though
        // the fault were handled -- the requestAnimationFrame loop would
        // reschedule, hit the same fault next frame, and rebuild the dialog
        // from scratch on every frame, leaving it unreadable and its buttons
        // unclickable.
        //
        // Unwinding instead stops the faulting operation dead: a game-loop
        // frame that throws is never rescheduled by game.js, so the dialog is
        // rendered once and stays put. Nothing here navigates or reloads. The
        // original exception is rethrown with its stack intact so that if the
        // unwind is reported again at the JS boundary it still carries the real
        // fault rather than a synthesized one.
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        throw new InvalidOperationException($"Fatal error 0x{tag:x8}");
    }

    /// <summary>
    /// Returns the managed call stack of the abort site when the exception's
    /// own stack does not describe it, or <see langword="null"/> when the
    /// exception already carries a managed stack.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>System.Runtime.InteropServices.JavaScript.JSException</c> overrides
    /// <see cref="Exception.StackTrace"/> to return the *JavaScript* stack.
    /// That defeats both halves of the reporting above: the stamp is skipped
    /// because a stack is already present, and the stack that is present
    /// belongs to the browser rather than to the game — so nothing anywhere in
    /// the crash report says which managed code made the interop call. For a
    /// WebKit binding failure, whose message is often no more than
    /// <c>TypeError: Type error</c>, that leaves the report undiagnosable.
    /// </para>
    /// <para>
    /// The type is matched by name because it exists only in the browser
    /// runtime pack while this file is shared by every platform.
    /// </para>
    /// </remarks>
    private static string? CaptureAbortSite(
        Exception? exception)
    {
        if (!IsJavaScriptException(exception))
        {
            return null;
        }

        try
        {
            // skipFrames: 1 drops this helper; Terminate itself is kept, since
            // it is the frame that identifies the abort site.
            var stack = new System.Diagnostics.StackTrace(skipFrames: 1, fNeedFileInfo: true);
            return $"Managed abort site:{Environment.NewLine}{stack}";
        }
        catch
        {
            // Never let diagnostics capture prevent the report/abort.
            return null;
        }
    }

    private static bool IsJavaScriptException(
        Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (DiagnosticInfo.FullTypeName(current) == "System.Runtime.InteropServices.JavaScript.JSException")
            {
                return true;
            }
        }

        return false;
    }
}
