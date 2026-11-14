// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Input;

/// <summary>A mouse or pen's buttons, as flags.</summary>
[Flags]
public enum PointerButton
{
    /// <summary>No button.</summary>
    None = 0,

    /// <summary>The primary button.</summary>
    Left = 1,

    /// <summary>The secondary button.</summary>
    Right = 2,

    /// <summary>The middle button, often the wheel.</summary>
    Middle = 4,

    /// <summary>The first side button of a mouse, usually Back.</summary>
    X1 = 8,

    /// <summary>The second side button of a mouse, usually Forward.</summary>
    X2 = 16,
}
