// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>
/// A gamepad's sticks, from -1 to 1 with up and left negative, and its triggers, from 0 to 1.
/// Values are as the hardware reports them, so a game chooses its own dead zone.
/// </summary>
public enum GamepadAxis
{
    /// <summary>The left stick's horizontal position.</summary>
    LeftX,
    /// <summary>The left stick's vertical position.</summary>
    LeftY,
    /// <summary>The right stick's horizontal position.</summary>
    RightX,
    /// <summary>The right stick's vertical position.</summary>
    RightY,
    /// <summary>The left trigger.</summary>
    LeftTrigger,
    /// <summary>The right trigger.</summary>
    RightTrigger,
}
