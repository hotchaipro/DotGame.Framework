// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Input;

namespace DotGame.Framework.Desktop.Input;

internal static class KeyMap
{
    /// <summary>
    /// Maps an SDL3 scancode to the engine's layout-independent <see cref="Key"/>.
    /// Returns <see cref="Key.None"/> for scancodes not modeled by the engine.
    /// </summary>
    public static Key FromScancode(
        SDL.Scancode scancode)
    {
        return scancode switch
        {
            SDL.Scancode.A => Key.A,
            SDL.Scancode.B => Key.B,
            SDL.Scancode.C => Key.C,
            SDL.Scancode.D => Key.D,
            SDL.Scancode.E => Key.E,
            SDL.Scancode.F => Key.F,
            SDL.Scancode.G => Key.G,
            SDL.Scancode.H => Key.H,
            SDL.Scancode.I => Key.I,
            SDL.Scancode.J => Key.J,
            SDL.Scancode.K => Key.K,
            SDL.Scancode.L => Key.L,
            SDL.Scancode.M => Key.M,
            SDL.Scancode.N => Key.N,
            SDL.Scancode.O => Key.O,
            SDL.Scancode.P => Key.P,
            SDL.Scancode.Q => Key.Q,
            SDL.Scancode.R => Key.R,
            SDL.Scancode.S => Key.S,
            SDL.Scancode.T => Key.T,
            SDL.Scancode.U => Key.U,
            SDL.Scancode.V => Key.V,
            SDL.Scancode.W => Key.W,
            SDL.Scancode.X => Key.X,
            SDL.Scancode.Y => Key.Y,
            SDL.Scancode.Z => Key.Z,

            SDL.Scancode.Alpha1 => Key.D1,
            SDL.Scancode.Alpha2 => Key.D2,
            SDL.Scancode.Alpha3 => Key.D3,
            SDL.Scancode.Alpha4 => Key.D4,
            SDL.Scancode.Alpha5 => Key.D5,
            SDL.Scancode.Alpha6 => Key.D6,
            SDL.Scancode.Alpha7 => Key.D7,
            SDL.Scancode.Alpha8 => Key.D8,
            SDL.Scancode.Alpha9 => Key.D9,
            SDL.Scancode.Alpha0 => Key.D0,

            SDL.Scancode.F1 => Key.F1,
            SDL.Scancode.F2 => Key.F2,
            SDL.Scancode.F3 => Key.F3,
            SDL.Scancode.F4 => Key.F4,
            SDL.Scancode.F5 => Key.F5,
            SDL.Scancode.F6 => Key.F6,
            SDL.Scancode.F7 => Key.F7,
            SDL.Scancode.F8 => Key.F8,
            SDL.Scancode.F9 => Key.F9,
            SDL.Scancode.F10 => Key.F10,
            SDL.Scancode.F11 => Key.F11,
            SDL.Scancode.F12 => Key.F12,

            SDL.Scancode.Escape => Key.Escape,
            SDL.Scancode.Tab => Key.Tab,
            SDL.Scancode.Capslock => Key.CapsLock,
            SDL.Scancode.Space => Key.Space,
            SDL.Scancode.Backspace => Key.Backspace,
            SDL.Scancode.Return => Key.Enter,
            SDL.Scancode.Insert => Key.Insert,
            SDL.Scancode.Delete => Key.Delete,
            SDL.Scancode.Home => Key.Home,
            SDL.Scancode.End => Key.End,
            SDL.Scancode.Pageup => Key.PageUp,
            SDL.Scancode.Pagedown => Key.PageDown,
            SDL.Scancode.Printscreen => Key.PrintScreen,
            SDL.Scancode.Pause => Key.Pause,

            SDL.Scancode.Left => Key.Left,
            SDL.Scancode.Right => Key.Right,
            SDL.Scancode.Up => Key.Up,
            SDL.Scancode.Down => Key.Down,

            SDL.Scancode.LShift => Key.LeftShift,
            SDL.Scancode.RShift => Key.RightShift,
            SDL.Scancode.LCtrl => Key.LeftControl,
            SDL.Scancode.RCtrl => Key.RightControl,
            SDL.Scancode.LAlt => Key.LeftAlt,
            SDL.Scancode.RAlt => Key.RightAlt,
            SDL.Scancode.LGUI => Key.LeftMeta,
            SDL.Scancode.RGUI => Key.RightMeta,

            SDL.Scancode.NumLockClear => Key.NumLock,
            SDL.Scancode.Kp0 => Key.Numpad0,
            SDL.Scancode.Kp1 => Key.Numpad1,
            SDL.Scancode.Kp2 => Key.Numpad2,
            SDL.Scancode.Kp3 => Key.Numpad3,
            SDL.Scancode.Kp4 => Key.Numpad4,
            SDL.Scancode.Kp5 => Key.Numpad5,
            SDL.Scancode.Kp6 => Key.Numpad6,
            SDL.Scancode.Kp7 => Key.Numpad7,
            SDL.Scancode.Kp8 => Key.Numpad8,
            SDL.Scancode.Kp9 => Key.Numpad9,
            SDL.Scancode.KpPlus => Key.NumpadAdd,
            SDL.Scancode.KpMinus => Key.NumpadSubtract,
            SDL.Scancode.KpMultiply => Key.NumpadMultiply,
            SDL.Scancode.KpDivide => Key.NumpadDivide,
            SDL.Scancode.KpPeriod => Key.NumpadDecimal,
            SDL.Scancode.KpEnter => Key.NumpadEnter,

            SDL.Scancode.Minus => Key.Minus,
            SDL.Scancode.Equals => Key.Equals,
            SDL.Scancode.Leftbracket => Key.LeftBracket,
            SDL.Scancode.Rightbracket => Key.RightBracket,
            SDL.Scancode.Backslash => Key.Backslash,
            SDL.Scancode.Semicolon => Key.Semicolon,
            SDL.Scancode.Apostrophe => Key.Apostrophe,
            SDL.Scancode.Grave => Key.Grave,
            SDL.Scancode.Comma => Key.Comma,
            SDL.Scancode.Period => Key.Period,
            SDL.Scancode.Slash => Key.Slash,

            // Platform navigation
            SDL.Scancode.ACBack => Key.Back,

            _ => Key.None,
        };
    }

    /// <summary>Maps SDL3 keymod flags to the engine's <see cref="ModifierKeys"/>.</summary>
    public static ModifierKeys FromKeymod(
        SDL.Keymod mod)
    {
        var result = ModifierKeys.None;

        if ((mod & SDL.Keymod.Shift) != 0)
        {
            result |= ModifierKeys.Shift;
        }

        if ((mod & SDL.Keymod.Ctrl) != 0)
        {
            result |= ModifierKeys.Control;
        }

        if ((mod & SDL.Keymod.Alt) != 0)
        {
            result |= ModifierKeys.Alt;
        }

        if ((mod & SDL.Keymod.GUI) != 0)
        {
            result |= ModifierKeys.Meta;
        }

        if ((mod & SDL.Keymod.Caps) != 0)
        {
            result |= ModifierKeys.CapsLock;
        }

        if ((mod & SDL.Keymod.Num) != 0)
        {
            result |= ModifierKeys.NumLock;
        }

        return result;
    }
}
