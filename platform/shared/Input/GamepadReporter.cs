// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Input;

namespace DotGame.Framework.Platform.Common.Input;

/// <summary>
/// For a platform that reads a gamepad whole: queues what changed since the last reading.
/// Use it from one thread.
/// </summary>
internal sealed class GamepadReporter
{
    private const int ButtonCount = 15;

    private readonly uint[] _buttons = new uint[InputService.MaxGamepads];
    private readonly float[][] _axes = new float[InputService.MaxGamepads][];
    private readonly GameHost _host;

    public GamepadReporter(
        GameHost host)
    {
        this._host = host;
        for (int i = 0; i < this._axes.Length; i++)
        {
            this._axes[i] = new float[6];
        }
    }

    public void Report(
        int index,
        uint buttons,
        ReadOnlySpan<float> axes)
    {
        uint changed = buttons ^ this._buttons[index];
        for (int button = 0; (button < ButtonCount) && (changed != 0); button++)
        {
            uint bit = 1u << button;
            if ((changed & bit) != 0)
            {
                changed &= ~bit;
                var kind = ((buttons & bit) != 0) ? GamepadEventKind.ButtonPressed : GamepadEventKind.ButtonReleased;
                this._host.QueueGamepadEvent(new GamepadEvent(kind, index, (GamepadButton)button));
            }
        }

        this._buttons[index] = buttons;

        var last = this._axes[index];
        for (int axis = 0; axis < last.Length; axis++)
        {
            if (axes[axis] != last[axis])
            {
                last[axis] = axes[axis];
                this._host.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.AxisMoved, index, axis: (GamepadAxis)axis, value: axes[axis]));
            }
        }
    }

    /// <summary>
    /// Forgets a gamepad, when it disconnects: the framework releases what it held.
    /// </summary>
    public void Forget(
        int index)
    {
        this._buttons[index] = 0;
        Array.Clear(this._axes[index]);
    }
}
