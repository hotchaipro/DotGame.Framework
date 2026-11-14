// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;

namespace DotGame.Framework.Platform;

/// <summary>
/// The framework's Debug behavior, set by the game's build rather than the framework's: the
/// framework ships one build, and a game's Debug build turns these on. Each is a runtime feature
/// switch, which the game's build writes into its runtime configuration (the
/// <c>DotGameDebug</c>, <c>DotGameTraceInput</c> and <c>DotGameTraceAudio</c> properties, see
/// <c>packaging/build/DotGame.Framework.Switches.targets</c>). The trimmer and AOT compilers take
/// each as a constant, so in a trimmed Release build the code behind an off switch is removed, as
/// <c>#if DEBUG</c> did.
/// </summary>
internal static class FeatureSwitches
{
    /// <summary>
    /// Whether the game is built for debugging: the framework's self-checks run, an audio failure
    /// is fatal rather than reported, a fatal error breaks into an attached debugger, and the
    /// desktop window doesn't remember its place.
    /// </summary>
    [FeatureSwitchDefinition("DotGame.Debug")]
    internal static bool IsDebug { get; } = AppContext.TryGetSwitch("DotGame.Debug", out bool isOn) && isOn;

    /// <summary>
    /// Whether input events are traced, from the platform to the framework's queue.
    /// </summary>
    [FeatureSwitchDefinition("DotGame.TraceInput")]
    internal static bool TraceInput { get; } = AppContext.TryGetSwitch("DotGame.TraceInput", out bool isOn) && isOn;

    /// <summary>
    /// Whether sounds and music are traced, as they load, play and are released.
    /// </summary>
    [FeatureSwitchDefinition("DotGame.TraceAudio")]
    internal static bool TraceAudio { get; } = AppContext.TryGetSwitch("DotGame.TraceAudio", out bool isOn) && isOn;
}
