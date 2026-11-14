// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Input;

/// <summary>
/// Modifier keys held at the time a <see cref="KeyEvent"/> was produced.
/// </summary>
[Flags]
public enum ModifierKeys
{
    /// <summary>No modifier.</summary>
    None = 0,
    /// <summary>Either shift key.</summary>
    Shift = 1 << 0,
    /// <summary>Either control key.</summary>
    Control = 1 << 1,
    /// <summary>Either alt or option key.</summary>
    Alt = 1 << 2,
    /// <summary>Either Windows or command key.</summary>
    Meta = 1 << 3,
    /// <summary>Caps lock is on.</summary>
    CapsLock = 1 << 4,
    /// <summary>Num lock is on.</summary>
    NumLock = 1 << 5,
}
