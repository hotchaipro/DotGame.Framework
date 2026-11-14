// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Time;

namespace DotGame.Framework.Testing;

/// <summary>
/// Game time a test runs by hand, frame by frame, as a game's host does.
/// </summary>
public sealed class TestClock
{
    /// <summary>The service, at no frame yet.</summary>
    public TimeService Service { get; } = new TimeService();

    /// <summary>
    /// A frame at <paramref name="frameTimestamp"/>: game time moves on, and the delayed actions
    /// that are due run.
    /// </summary>
    public void Update(
        TimeSpan frameTimestamp)
    {
        this.Service.Update(frameTimestamp);
    }
}
