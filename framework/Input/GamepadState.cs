// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>
/// A snapshot of a gamepad, for code that prefers polling over events (movement during update).
/// </summary>
public readonly struct GamepadState
{
    internal const int AxisCount = 6;

    private readonly uint _buttons;
    private readonly float _leftX;
    private readonly float _leftY;
    private readonly float _rightX;
    private readonly float _rightY;
    private readonly float _leftTrigger;
    private readonly float _rightTrigger;

    internal GamepadState(
        bool isConnected,
        uint buttons,
        float[] axes)
    {
        this.IsConnected = isConnected;
        this._buttons = buttons;
        this._leftX = axes[0];
        this._leftY = axes[1];
        this._rightX = axes[2];
        this._rightY = axes[3];
        this._leftTrigger = axes[4];
        this._rightTrigger = axes[5];
    }

    /// <summary>Whether a gamepad is connected at this index.</summary>
    public bool IsConnected { get; }

    /// <summary>Whether the button is held.</summary>
    public bool IsButtonDown(
        GamepadButton button)
    {
        return (this._buttons & (1u << (int)button)) != 0;
    }

    /// <summary>An axis's value: sticks from -1 to 1, triggers from 0 to 1.</summary>
    public float GetAxis(
        GamepadAxis axis)
    {
        return axis switch
        {
            GamepadAxis.LeftX => this._leftX,
            GamepadAxis.LeftY => this._leftY,
            GamepadAxis.RightX => this._rightX,
            GamepadAxis.RightY => this._rightY,
            GamepadAxis.LeftTrigger => this._leftTrigger,
            GamepadAxis.RightTrigger => this._rightTrigger,
            _ => 0,
        };
    }
}
