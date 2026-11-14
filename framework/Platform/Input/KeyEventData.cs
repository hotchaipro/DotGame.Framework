// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Input;

namespace DotGame.Framework.Platform.Input;

/// <summary>
/// A key event as a platform host reports it to <see cref="GameHost.QueueKeyEvent"/>.
/// </summary>
internal readonly struct KeyEventData
{
    public required KeyEventKind State { get; init; }

    public required Key Key { get; init; }

    public ModifierKeys Modifiers { get; init; }

    /// <summary>
    /// Whether this press repeats a key held down.
    /// </summary>
    public bool IsRepeat { get; init; }

    /// <summary>
    /// The platform's own code for the key, or 0.
    /// </summary>
    public int VirtualKey { get; init; }
}
