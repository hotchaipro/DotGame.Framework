// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Time;

/// <summary>
/// Represents a scheduled delayed action that can be canceled.
/// </summary>
public sealed class DelayHandle
{
    internal DelayHandle(
        TimeSpan triggerTime,
        Action action)
    {
        this.TriggerTime = triggerTime;
        this.Action = action;
    }

    internal TimeSpan TriggerTime { get; set; }

    // Null once canceled, so a long delay's closure isn't kept until its time comes
    internal Action? Action { get; private set; }

    // The order of scheduling, which settles delays due at the same time
    internal long Sequence { get; set; }

    /// <summary>
    /// Gets a value indicating whether this delayed action has been canceled.
    /// </summary>
    public bool IsCanceled { get; private set; }

    /// <summary>
    /// Cancels the delayed action, preventing it from being executed.
    /// </summary>
    public void Cancel()
    {
        this.IsCanceled = true;
        this.Action = null;
    }
}
