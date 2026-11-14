// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>
/// A gamepad's buttons, by where they are rather than what's printed on them: <see cref="South"/>
/// is A on an Xbox pad, B on a Nintendo one and the cross on a PlayStation one.
/// </summary>
public enum GamepadButton
{
    /// <summary>The bottom face button: A on Xbox, Cross on PlayStation.</summary>
    South,
    /// <summary>The right face button: B on Xbox, Circle on PlayStation.</summary>
    East,
    /// <summary>The left face button: X on Xbox, Square on PlayStation.</summary>
    West,
    /// <summary>The top face button: Y on Xbox, Triangle on PlayStation.</summary>
    North,
    /// <summary>The left bumper.</summary>
    LeftShoulder,
    /// <summary>The right bumper.</summary>
    RightShoulder,
    /// <summary>The back or select button.</summary>
    Back,
    /// <summary>The start or menu button.</summary>
    Start,
    /// <summary>The guide or home button.</summary>
    Guide,
    /// <summary>Pressing the left stick.</summary>
    LeftStick,
    /// <summary>Pressing the right stick.</summary>
    RightStick,
    /// <summary>Up on the directional pad.</summary>
    DPadUp,
    /// <summary>Down on the directional pad.</summary>
    DPadDown,
    /// <summary>Left on the directional pad.</summary>
    DPadLeft,
    /// <summary>Right on the directional pad.</summary>
    DPadRight,
}
