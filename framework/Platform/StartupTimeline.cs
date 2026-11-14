// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Trace = DotGame.Framework.Diagnostics.Trace;

namespace DotGame.Framework.Platform;

/// <summary>
/// Marks the milestones of a game's startup, so startup can be measured the same way in
/// every build, including a published one. Each milestone is traced with the time since the
/// process started, and the platform can record it too: on the web, as a performance mark
/// that the page's startup report (<c>window.dotgameStartup</c>) collects.
/// </summary>
/// <remarks>
/// A milestone is recorded once, the first time it's marked, so code on a path that can
/// run again (a resume, a second navigation) can mark it unconditionally. Marks are cheap:
/// a dictionary lookup, a trace, and on the web one interop call, for a dozen calls a
/// session.
/// </remarks>
internal static class StartupTimeline
{
    private static readonly Stopwatch s_stopwatch = Stopwatch.StartNew();
    private static readonly HashSet<string> s_marked = new(StringComparer.Ordinal);
    private static readonly Lock s_lock = new();

    /// <summary>
    /// Records a milestone with the platform, as well as tracing it. The WebGL platform sets
    /// it to add a performance mark; null elsewhere.
    /// </summary>
    internal static Action<string, string?>? PlatformSink { get; set; }

    /// <summary>
    /// Marks a startup milestone, the first time it's reached.
    /// </summary>
    /// <param name="name">
    /// The milestone, in kebab case, such as <c>"first-frame"</c>.
    /// </param>
    /// <param name="detail">
    /// Optional context recorded with it, such as the scene shown or the number of assets.
    /// </param>
    internal static void Mark(
        string name,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        lock (s_lock)
        {
            if (!s_marked.Add(name))
            {
                return;
            }
        }

        Trace.Write(0x5d71a3c4, (detail is null)
            ? FormattableString.Invariant($"[Startup] {name} +{s_stopwatch.ElapsedMilliseconds}ms")
            : FormattableString.Invariant($"[Startup] {name} +{s_stopwatch.ElapsedMilliseconds}ms ({detail})"));

        try
        {
            PlatformSink?.Invoke(name, detail);
        }
        catch (Exception e)
        {
            // Measurement must never break startup
            Trace.Write(0x2e86c19b, exception: e);
        }
    }
}
