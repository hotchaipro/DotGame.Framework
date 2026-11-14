// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using Android.Views;
using DotGame.Framework.Input;

namespace DotGame.Framework.Android.Input;

internal static class KeyMap
{
    /// <summary>
    /// Maps an Android <see cref="Keycode"/> to the engine's layout-independent <see cref="Key"/>.
    /// Returns <see cref="Key.None"/> for keycodes not modeled by the engine.
    /// </summary>
    public static Key FromKeycode(
        Keycode keycode)
    {
        return keycode switch
        {
            Keycode.A => Key.A,
            Keycode.B => Key.B,
            Keycode.C => Key.C,
            Keycode.D => Key.D,
            Keycode.E => Key.E,
            Keycode.F => Key.F,
            Keycode.G => Key.G,
            Keycode.H => Key.H,
            Keycode.I => Key.I,
            Keycode.J => Key.J,
            Keycode.K => Key.K,
            Keycode.L => Key.L,
            Keycode.M => Key.M,
            Keycode.N => Key.N,
            Keycode.O => Key.O,
            Keycode.P => Key.P,
            Keycode.Q => Key.Q,
            Keycode.R => Key.R,
            Keycode.S => Key.S,
            Keycode.T => Key.T,
            Keycode.U => Key.U,
            Keycode.V => Key.V,
            Keycode.W => Key.W,
            Keycode.X => Key.X,
            Keycode.Y => Key.Y,
            Keycode.Z => Key.Z,

            Keycode.Num0 => Key.D0,
            Keycode.Num1 => Key.D1,
            Keycode.Num2 => Key.D2,
            Keycode.Num3 => Key.D3,
            Keycode.Num4 => Key.D4,
            Keycode.Num5 => Key.D5,
            Keycode.Num6 => Key.D6,
            Keycode.Num7 => Key.D7,
            Keycode.Num8 => Key.D8,
            Keycode.Num9 => Key.D9,

            Keycode.F1 => Key.F1,
            Keycode.F2 => Key.F2,
            Keycode.F3 => Key.F3,
            Keycode.F4 => Key.F4,
            Keycode.F5 => Key.F5,
            Keycode.F6 => Key.F6,
            Keycode.F7 => Key.F7,
            Keycode.F8 => Key.F8,
            Keycode.F9 => Key.F9,
            Keycode.F10 => Key.F10,
            Keycode.F11 => Key.F11,
            Keycode.F12 => Key.F12,

            Keycode.Escape => Key.Escape,
            Keycode.Tab => Key.Tab,
            Keycode.CapsLock => Key.CapsLock,
            Keycode.Space => Key.Space,
            Keycode.Del => Key.Backspace,
            Keycode.Enter => Key.Enter,
            Keycode.Insert => Key.Insert,
            Keycode.ForwardDel => Key.Delete,
            Keycode.MoveHome => Key.Home,
            Keycode.MoveEnd => Key.End,
            Keycode.PageUp => Key.PageUp,
            Keycode.PageDown => Key.PageDown,
            Keycode.Sysrq => Key.PrintScreen,
            Keycode.Break => Key.Pause,

            Keycode.DpadLeft => Key.Left,
            Keycode.DpadRight => Key.Right,
            Keycode.DpadUp => Key.Up,
            Keycode.DpadDown => Key.Down,

            // A TV remote's or game controller's select button chooses, like Enter
            Keycode.DpadCenter => Key.Enter,

            Keycode.ShiftLeft => Key.LeftShift,
            Keycode.ShiftRight => Key.RightShift,
            Keycode.CtrlLeft => Key.LeftControl,
            Keycode.CtrlRight => Key.RightControl,
            Keycode.AltLeft => Key.LeftAlt,
            Keycode.AltRight => Key.RightAlt,
            Keycode.MetaLeft => Key.LeftMeta,
            Keycode.MetaRight => Key.RightMeta,

            Keycode.NumLock => Key.NumLock,
            Keycode.Numpad0 => Key.Numpad0,
            Keycode.Numpad1 => Key.Numpad1,
            Keycode.Numpad2 => Key.Numpad2,
            Keycode.Numpad3 => Key.Numpad3,
            Keycode.Numpad4 => Key.Numpad4,
            Keycode.Numpad5 => Key.Numpad5,
            Keycode.Numpad6 => Key.Numpad6,
            Keycode.Numpad7 => Key.Numpad7,
            Keycode.Numpad8 => Key.Numpad8,
            Keycode.Numpad9 => Key.Numpad9,
            Keycode.NumpadAdd => Key.NumpadAdd,
            Keycode.NumpadSubtract => Key.NumpadSubtract,
            Keycode.NumpadMultiply => Key.NumpadMultiply,
            Keycode.NumpadDivide => Key.NumpadDivide,
            Keycode.NumpadDot => Key.NumpadDecimal,
            Keycode.NumpadEnter => Key.NumpadEnter,

            Keycode.Minus => Key.Minus,
            Keycode.Equals => Key.Equals,
            Keycode.LeftBracket => Key.LeftBracket,
            Keycode.RightBracket => Key.RightBracket,
            Keycode.Backslash => Key.Backslash,
            Keycode.Semicolon => Key.Semicolon,
            Keycode.Apostrophe => Key.Apostrophe,
            Keycode.Grave => Key.Grave,
            Keycode.Comma => Key.Comma,
            Keycode.Period => Key.Period,
            Keycode.Slash => Key.Slash,

            // Platform navigation
            Keycode.Back => Key.Back,

            _ => Key.None,
        };
    }

    /// <summary>Maps Android meta-state flags to the engine's <see cref="ModifierKeys"/>.</summary>
    public static ModifierKeys FromMetaState(
        MetaKeyStates metaState)
    {
        var result = ModifierKeys.None;

        if ((metaState & (MetaKeyStates.ShiftOn | MetaKeyStates.ShiftLeftOn | MetaKeyStates.ShiftRightOn)) != 0)
        {
            result |= ModifierKeys.Shift;
        }

        if ((metaState & (MetaKeyStates.CtrlOn | MetaKeyStates.CtrlLeftOn | MetaKeyStates.CtrlRightOn)) != 0)
        {
            result |= ModifierKeys.Control;
        }

        if ((metaState & (MetaKeyStates.AltOn | MetaKeyStates.AltLeftOn | MetaKeyStates.AltRightOn)) != 0)
        {
            result |= ModifierKeys.Alt;
        }

        if ((metaState & (MetaKeyStates.MetaOn | MetaKeyStates.MetaLeftOn | MetaKeyStates.MetaRightOn)) != 0)
        {
            result |= ModifierKeys.Meta;
        }

        if ((metaState & MetaKeyStates.CapsLockOn) != 0)
        {
            result |= ModifierKeys.CapsLock;
        }

        if ((metaState & MetaKeyStates.NumLockOn) != 0)
        {
            result |= ModifierKeys.NumLock;
        }

        return result;
    }
}
