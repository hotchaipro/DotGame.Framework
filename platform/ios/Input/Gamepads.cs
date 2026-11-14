// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Input;
using DotGame.Framework.Platform.Common.Input;
using Foundation;
using GameController;

namespace DotGame.Framework.iOS.Input;

/// <summary>
/// Gamepads on iOS, through the GameController framework. Everything arrives on the main thread.
/// </summary>
internal sealed class Gamepads : IDisposable
{
    private readonly GameHost _host;
    private readonly GamepadSlots _slots = new();
    private readonly GamepadReporter _reporter;
    private readonly NSObject _connectObserver;
    private readonly NSObject _disconnectObserver;
    private readonly float[] _axes = new float[6];

    public Gamepads(
        GameHost host)
    {
        this._host = host;
        this._reporter = new GamepadReporter(host);

        this._connectObserver = GCController.Notifications.ObserveDidConnect((_, args) =>
        {
            if (args.Notification.Object is GCController controller)
            {
                this.Connect(controller);
            }
        });

        this._disconnectObserver = GCController.Notifications.ObserveDidDisconnect((_, args) =>
        {
            if (args.Notification.Object is GCController controller)
            {
                this.Disconnect(controller);
            }
        });

        foreach (var controller in GCController.Controllers)
        {
            this.Connect(controller);
        }
    }

    private void Connect(
        GCController controller)
    {
        var gamepad = controller.ExtendedGamepad;
        long id = ((nint)controller.Handle).ToInt64();
        if ((gamepad is null) || (this._slots.IndexOf(id) >= 0))
        {
            return;
        }

        int index = this._slots.Connect(id);
        if (index < 0)
        {
            return;
        }

        this._host.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Connected, index));
        gamepad.ValueChangedHandler = (pad, _) => this.Read(index, pad);
    }

    private void Disconnect(
        GCController controller)
    {
        int index = this._slots.Disconnect(((nint)controller.Handle).ToInt64());
        if (index >= 0)
        {
            this._reporter.Forget(index);
            this._host.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Disconnected, index));
        }
    }

    private void Read(
        int index,
        GCExtendedGamepad pad)
    {
        uint buttons = 0;
        Set(ref buttons, GamepadButton.South, pad.ButtonA.IsPressed);
        Set(ref buttons, GamepadButton.East, pad.ButtonB.IsPressed);
        Set(ref buttons, GamepadButton.West, pad.ButtonX.IsPressed);
        Set(ref buttons, GamepadButton.North, pad.ButtonY.IsPressed);
        Set(ref buttons, GamepadButton.LeftShoulder, pad.LeftShoulder.IsPressed);
        Set(ref buttons, GamepadButton.RightShoulder, pad.RightShoulder.IsPressed);
        Set(ref buttons, GamepadButton.Back, pad.ButtonOptions?.IsPressed == true);
        Set(ref buttons, GamepadButton.Start, pad.ButtonMenu.IsPressed);
        Set(ref buttons, GamepadButton.Guide, pad.ButtonHome?.IsPressed == true);
        Set(ref buttons, GamepadButton.LeftStick, pad.LeftThumbstickButton?.IsPressed == true);
        Set(ref buttons, GamepadButton.RightStick, pad.RightThumbstickButton?.IsPressed == true);
        Set(ref buttons, GamepadButton.DPadUp, pad.DPad.Up.IsPressed);
        Set(ref buttons, GamepadButton.DPadDown, pad.DPad.Down.IsPressed);
        Set(ref buttons, GamepadButton.DPadLeft, pad.DPad.Left.IsPressed);
        Set(ref buttons, GamepadButton.DPadRight, pad.DPad.Right.IsPressed);

        // NOTE: GameController has up as positive; the other platforms and the framework, negative
        this._axes[(int)GamepadAxis.LeftX] = pad.LeftThumbstick.XAxis.Value;
        this._axes[(int)GamepadAxis.LeftY] = -pad.LeftThumbstick.YAxis.Value;
        this._axes[(int)GamepadAxis.RightX] = pad.RightThumbstick.XAxis.Value;
        this._axes[(int)GamepadAxis.RightY] = -pad.RightThumbstick.YAxis.Value;
        this._axes[(int)GamepadAxis.LeftTrigger] = pad.LeftTrigger.Value;
        this._axes[(int)GamepadAxis.RightTrigger] = pad.RightTrigger.Value;

        this._reporter.Report(index, buttons, this._axes);
    }

    private static void Set(
        ref uint buttons,
        GamepadButton button,
        bool isPressed)
    {
        if (isPressed)
        {
            buttons |= 1u << (int)button;
        }
    }

    public void Dispose()
    {
        this._connectObserver.Dispose();
        this._disconnectObserver.Dispose();
    }
}
