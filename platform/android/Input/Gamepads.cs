// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using Android.Hardware.Input;
using Android.OS;
using Android.Views;
using DotGame.Framework.Input;
using DotGame.Framework.Platform.Common.Input;
using AndroidKeyEvent = Android.Views.KeyEvent;

namespace DotGame.Framework.Android.Input;

/// <summary>
/// Gamepads on Android: buttons arrive as key events and sticks as motion events, from the surface
/// that has the focus; the input manager says when one connects or disconnects.
/// </summary>
internal sealed class Gamepads : Java.Lang.Object, InputManager.IInputDeviceListener, View.IOnGenericMotionListener
{
    private readonly GameHost _host;
    private readonly InputManager? _inputManager;
    private readonly GamepadSlots _slots = new();
    private readonly GamepadReporter _reporter;
    private readonly uint[] _buttons = new uint[InputService.MaxGamepads];
    private readonly float[][] _axes = new float[InputService.MaxGamepads][];
    private readonly uint[] _hatButtons = new uint[InputService.MaxGamepads];

    public Gamepads(
        GameHost host,
        InputManager? inputManager)
    {
        this._host = host;
        this._inputManager = inputManager;
        this._reporter = new GamepadReporter(host);

        for (int i = 0; i < this._axes.Length; i++)
        {
            this._axes[i] = new float[6];
        }

        inputManager?.RegisterInputDeviceListener(this, new Handler(Looper.MainLooper!));

        foreach (int id in InputDevice.GetDeviceIds() ?? [])
        {
            this.OnInputDeviceAdded(id);
        }
    }

    private static bool IsGamepad(
        InputDevice? device)
    {
        return (device is not null)
            && ((device.Sources & InputSourceType.Gamepad) == InputSourceType.Gamepad)
            && ((device.Sources & InputSourceType.Joystick) == InputSourceType.Joystick);
    }

    public void OnInputDeviceAdded(
        int deviceId)
    {
        if (IsGamepad(InputDevice.GetDevice(deviceId)) && (this._slots.IndexOf(deviceId) < 0))
        {
            int index = this._slots.Connect(deviceId);
            if (index >= 0)
            {
                this._host.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Connected, index));
            }
        }
    }

    public void OnInputDeviceChanged(
        int deviceId)
    {
    }

    public void OnInputDeviceRemoved(
        int deviceId)
    {
        int index = this._slots.Disconnect(deviceId);
        if (index >= 0)
        {
            this._buttons[index] = 0;
            this._hatButtons[index] = 0;
            Array.Clear(this._axes[index]);
            this._reporter.Forget(index);
            this._host.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Disconnected, index));
        }
    }

    private static int MapButton(
        Keycode keyCode)
    {
        var button = keyCode switch
        {
            Keycode.ButtonA => GamepadButton.South,
            Keycode.ButtonB => GamepadButton.East,
            Keycode.ButtonX => GamepadButton.West,
            Keycode.ButtonY => GamepadButton.North,
            Keycode.ButtonL1 => GamepadButton.LeftShoulder,
            Keycode.ButtonR1 => GamepadButton.RightShoulder,
            Keycode.ButtonSelect => GamepadButton.Back,
            Keycode.ButtonStart => GamepadButton.Start,
            Keycode.ButtonMode => GamepadButton.Guide,
            Keycode.ButtonThumbl => GamepadButton.LeftStick,
            Keycode.ButtonThumbr => GamepadButton.RightStick,
            Keycode.DpadUp => GamepadButton.DPadUp,
            Keycode.DpadDown => GamepadButton.DPadDown,
            Keycode.DpadLeft => GamepadButton.DPadLeft,
            Keycode.DpadRight => GamepadButton.DPadRight,
            _ => (GamepadButton)(-1),
        };

        return (int)button;
    }

    /// <summary>
    /// Takes a key event if it's a gamepad's. The surface's key listener asks first.
    /// </summary>
    public bool TryHandleKey(
        Keycode keyCode,
        AndroidKeyEvent e)
    {
        if ((e.Source & InputSourceType.Gamepad) != InputSourceType.Gamepad)
        {
            return false;
        }

        int button = MapButton(keyCode);
        int index = this._slots.IndexOf(e.DeviceId);
        if ((button < 0) || (index < 0))
        {
            return false;
        }

        if (e.RepeatCount == 0)
        {
            uint bit = 1u << button;
            this._buttons[index] = (e.Action == KeyEventActions.Down)
                ? (this._buttons[index] | bit)
                : (this._buttons[index] & ~bit);
            this._reporter.Report(index, this._buttons[index] | this._hatButtons[index], this._axes[index]);
        }

        return true;
    }

    public bool OnGenericMotion(
        View? v,
        MotionEvent? e)
    {
        if ((e is null)
            || ((e.Source & InputSourceType.Joystick) != InputSourceType.Joystick)
            || (e.Action != MotionEventActions.Move))
        {
            return false;
        }

        int index = this._slots.IndexOf(e.DeviceId);
        if (index < 0)
        {
            return false;
        }

        var axes = this._axes[index];
        axes[(int)GamepadAxis.LeftX] = e.GetAxisValue(Axis.X);
        axes[(int)GamepadAxis.LeftY] = e.GetAxisValue(Axis.Y);
        axes[(int)GamepadAxis.RightX] = e.GetAxisValue(Axis.Z);
        axes[(int)GamepadAxis.RightY] = e.GetAxisValue(Axis.Rz);
        axes[(int)GamepadAxis.LeftTrigger] = Math.Max(e.GetAxisValue(Axis.Ltrigger), e.GetAxisValue(Axis.Brake));
        axes[(int)GamepadAxis.RightTrigger] = Math.Max(e.GetAxisValue(Axis.Rtrigger), e.GetAxisValue(Axis.Gas));

        // Many pads report the d-pad as a hat axis rather than as keys
        float hatX = e.GetAxisValue(Axis.HatX);
        float hatY = e.GetAxisValue(Axis.HatY);
        uint hat = 0;
        hat |= (hatY < -0.5f) ? (1u << (int)GamepadButton.DPadUp) : 0;
        hat |= (hatY > 0.5f) ? (1u << (int)GamepadButton.DPadDown) : 0;
        hat |= (hatX < -0.5f) ? (1u << (int)GamepadButton.DPadLeft) : 0;
        hat |= (hatX > 0.5f) ? (1u << (int)GamepadButton.DPadRight) : 0;
        this._hatButtons[index] = hat;

        this._reporter.Report(index, this._buttons[index] | hat, axes);
        return true;
    }

    public void Shutdown()
    {
        this._inputManager?.UnregisterInputDeviceListener(this);
    }
}
