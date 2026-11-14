// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>
/// An immutable gamepad input event, handed to <c>Game.OnGamepadEvent</c>.
/// </summary>
public readonly record struct GamepadEvent
{
    /// <summary>Makes an event, as a platform does.</summary>
    public GamepadEvent(
        GamepadEventKind kind,
        int index,
        GamepadButton button = default,
        GamepadAxis axis = default,
        float value = 0)
    {
        this.Kind = kind;
        this.Index = index;
        this.Button = button;
        this.Axis = axis;
        this.Value = value;
    }

    /// <summary>What happened.</summary>
    public GamepadEventKind Kind { get; }

    /// <summary>
    /// Which gamepad, from 0 to one less than <see cref="InputService.MaxGamepads"/>: the lowest
    /// free one when it connected, so it's stable while it stays connected.
    /// </summary>
    public int Index { get; }

    /// <summary>The button pressed or released.</summary>
    public GamepadButton Button { get; }

    /// <summary>The axis moved.</summary>
    public GamepadAxis Axis { get; }

    /// <summary>The axis's new value.</summary>
    public float Value { get; }
}
