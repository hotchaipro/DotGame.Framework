// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace DotGame.Framework.WebGL;

/// <summary>
/// Records <see cref="Framework.Platform.StartupTimeline"/> milestones with the page, as performance
/// marks that its startup report (<c>window.dotgameStartup</c>, see diagnostics.js) collects.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class PageStartupTimeline
{
    // Registered by main.js before any managed code runs, like the rest of diagnostics.js
    [JSImport("markStartup", "diagnostics.js")]
    internal static partial void MarkStartup(string name, string? detail);
}
