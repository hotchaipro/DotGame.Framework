// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.WebGL;

/// <summary>
/// Wraps a JavaScript error or unhandled promise rejection that surfaced to the
/// browser's global error handlers with no managed frame on the stack, so the
/// framework crash pipeline can report it with the original JavaScript name,
/// message, and stack preserved before the process terminates.
/// </summary>
/// <remarks>
/// The originating JavaScript stack is surfaced through <see cref="StackTrace"/>
/// (rather than the managed interop boundary) so crash telemetry groups and
/// locates the failure at its real origin.
/// </remarks>
internal sealed class UnhandledBrowserException : Exception
{
    private readonly string? _javaScriptStack;

    public UnhandledBrowserException(
        string source,
        string? name,
        string? message,
        string? javaScriptStack)
        : base(BuildMessage(source, name, message))
    {
        this._javaScriptStack = javaScriptStack;
    }

    /// <summary>
    /// The JavaScript stack captured at the point the error was raised, or the
    /// managed stack when no JavaScript stack was provided.
    /// </summary>
    public override string? StackTrace =>
        string.IsNullOrEmpty(this._javaScriptStack)
            ? base.StackTrace
            : this._javaScriptStack;

    private static string BuildMessage(
        string source,
        string? name,
        string? message)
    {
        var label = string.IsNullOrEmpty(name) ? "Error" : name;

        return string.IsNullOrEmpty(message)
            ? $"Unhandled browser {source}: {label}"
            : $"Unhandled browser {source}: {label}: {message}";
    }
}
