// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>
/// The state transition represented by a <see cref="KeyEvent"/>.
/// </summary>
public enum KeyEventKind
{
    /// <summary>
    /// The key transitioned to the down state (or fired an OS auto-repeat
    /// while still held, in which case <see cref="KeyEvent.IsRepeat"/> is <c>true</c>).
    /// </summary>
    Pressed,

    /// <summary>
    /// The key transitioned to the up state.
    /// </summary>
    Released,
}
