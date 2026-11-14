// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Globalization;
using System.Text;

namespace DotGame.Framework.Diagnostics;

internal sealed class DefaultTraceWriter : ITraceWriter
{
    // One StringBuilder per thread; [ThreadStatic] fields must not have initializers — use ??= at point of use.
    [ThreadStatic]
    private static StringBuilder? _threadLocalStringBuilder;

    public void Write(
        TraceLevel level,
        uint tag,
        string? message,
        Exception? exception)
    {
        var timestampUtc = DateTime.UtcNow;
        var threadId = (uint)Environment.CurrentManagedThreadId;

        var sb = _threadLocalStringBuilder ??= new StringBuilder(160);
        sb.Clear();
        string formatted = FormatTrace(sb, timestampUtc, threadId, level, tag, message, exception);

        System.Diagnostics.Trace.WriteLine(formatted);

        if (OperatingSystem.IsBrowser())
        {
            Console.WriteLine(formatted);
        }
    }

    private static string FormatTrace(
        StringBuilder stringBuilder,
        DateTime timestampUtc,
        uint threadId,
        TraceLevel level,
        uint tag,
        string? message,
        Exception? exception)
    {
        stringBuilder.Clear();

        stringBuilder.Append(CultureInfo.InvariantCulture, $"{timestampUtc:O}\t0x{threadId:x}\t0x{tag:x8}\t");

        // NOTE: Only what stands out, so a search finds it and an ordinary line stays as it was
        if (level == TraceLevel.Warning)
        {
            stringBuilder.Append("WARN\t");
        }
        else if (level == TraceLevel.Error)
        {
            stringBuilder.Append("ERROR\t");
        }

        if (message is not null)
        {
            stringBuilder.Append(message);
        }

        if (exception is not null)
        {
            stringBuilder.Append('\t');
            FormatException(stringBuilder, exception);
        }

        return stringBuilder.ToString();
    }

    private static void FormatException(
        StringBuilder stringBuilder,
        Exception exception)
    {
        var current = exception;
        int depth = 0;
        while ((current is not null) && (depth < 10))
        {
            if (depth > 0)
            {
                stringBuilder.Append(" --> ");
            }
            stringBuilder.Append(CultureInfo.InvariantCulture,
                $"{DiagnosticInfo.FullTypeName(current)}: '{current.Message}' (0x{current.HResult:x8})");
            current = current.InnerException;
            depth++;
        }
    }
}
