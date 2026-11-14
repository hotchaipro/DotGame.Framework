// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Diagnostics;

/// <summary>
/// Extensions for tagging exceptions with a stable <see cref="uint"/>
/// identifier that pinpoints the throwing call site without relying on
/// reflection or symbol resolution.
/// </summary>
public static class ExceptionExtensions
{
    /// <summary>
    /// Stamps a per-call-site <paramref name="tag"/> onto an exception so
    /// it survives transport through any logger or telemetry sink. The tag
    /// is stored in <see cref="Exception.HResult"/>, which is preserved by
    /// every standard serializer and rethrow path and is reflection-free.
    /// </summary>
    /// <typeparam name="TException">
    /// The concrete exception type — preserved so callers can write
    /// <c>throw new InvalidOperationException("...").WithTag(0x...)</c>
    /// without losing the exception type at the throw site.
    /// </typeparam>
    public static TException WithTag<TException>(
        this TException exception,
        uint tag)
        where TException : Exception
    {
        if (exception is not null)
        {
            exception.HResult = unchecked((int)tag);
        }
        return exception!;
    }
}
