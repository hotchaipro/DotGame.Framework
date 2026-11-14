// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Input;
using UIKit;

namespace DotGame.Framework.iOS.Input;

internal static class KeyMap
{
    /// <summary>
    /// Maps a <see cref="UIKeyboardHidUsage"/> to the engine's layout-independent <see cref="Key"/>.
    /// Returns <see cref="Key.None"/> for HID usages not modeled by the engine.
    /// </summary>
    public static Key FromHIDUsage(
        UIKeyboardHidUsage usage)
    {
        return usage switch
        {
            UIKeyboardHidUsage.KeyboardA => Key.A,
            UIKeyboardHidUsage.KeyboardB => Key.B,
            UIKeyboardHidUsage.KeyboardC => Key.C,
            UIKeyboardHidUsage.KeyboardD => Key.D,
            UIKeyboardHidUsage.KeyboardE => Key.E,
            UIKeyboardHidUsage.KeyboardF => Key.F,
            UIKeyboardHidUsage.KeyboardG => Key.G,
            UIKeyboardHidUsage.KeyboardH => Key.H,
            UIKeyboardHidUsage.KeyboardI => Key.I,
            UIKeyboardHidUsage.KeyboardJ => Key.J,
            UIKeyboardHidUsage.KeyboardK => Key.K,
            UIKeyboardHidUsage.KeyboardL => Key.L,
            UIKeyboardHidUsage.KeyboardM => Key.M,
            UIKeyboardHidUsage.KeyboardN => Key.N,
            UIKeyboardHidUsage.KeyboardO => Key.O,
            UIKeyboardHidUsage.KeyboardP => Key.P,
            UIKeyboardHidUsage.KeyboardQ => Key.Q,
            UIKeyboardHidUsage.KeyboardR => Key.R,
            UIKeyboardHidUsage.KeyboardS => Key.S,
            UIKeyboardHidUsage.KeyboardT => Key.T,
            UIKeyboardHidUsage.KeyboardU => Key.U,
            UIKeyboardHidUsage.KeyboardV => Key.V,
            UIKeyboardHidUsage.KeyboardW => Key.W,
            UIKeyboardHidUsage.KeyboardX => Key.X,
            UIKeyboardHidUsage.KeyboardY => Key.Y,
            UIKeyboardHidUsage.KeyboardZ => Key.Z,

            UIKeyboardHidUsage.Keyboard0 => Key.D0,
            UIKeyboardHidUsage.Keyboard1 => Key.D1,
            UIKeyboardHidUsage.Keyboard2 => Key.D2,
            UIKeyboardHidUsage.Keyboard3 => Key.D3,
            UIKeyboardHidUsage.Keyboard4 => Key.D4,
            UIKeyboardHidUsage.Keyboard5 => Key.D5,
            UIKeyboardHidUsage.Keyboard6 => Key.D6,
            UIKeyboardHidUsage.Keyboard7 => Key.D7,
            UIKeyboardHidUsage.Keyboard8 => Key.D8,
            UIKeyboardHidUsage.Keyboard9 => Key.D9,

            UIKeyboardHidUsage.KeyboardF1 => Key.F1,
            UIKeyboardHidUsage.KeyboardF2 => Key.F2,
            UIKeyboardHidUsage.KeyboardF3 => Key.F3,
            UIKeyboardHidUsage.KeyboardF4 => Key.F4,
            UIKeyboardHidUsage.KeyboardF5 => Key.F5,
            UIKeyboardHidUsage.KeyboardF6 => Key.F6,
            UIKeyboardHidUsage.KeyboardF7 => Key.F7,
            UIKeyboardHidUsage.KeyboardF8 => Key.F8,
            UIKeyboardHidUsage.KeyboardF9 => Key.F9,
            UIKeyboardHidUsage.KeyboardF10 => Key.F10,
            UIKeyboardHidUsage.KeyboardF11 => Key.F11,
            UIKeyboardHidUsage.KeyboardF12 => Key.F12,

            UIKeyboardHidUsage.KeyboardEscape => Key.Escape,
            UIKeyboardHidUsage.KeyboardTab => Key.Tab,
            UIKeyboardHidUsage.KeyboardCapsLock => Key.CapsLock,
            UIKeyboardHidUsage.KeyboardSpacebar => Key.Space,
            UIKeyboardHidUsage.KeyboardDeleteOrBackspace => Key.Backspace,
            UIKeyboardHidUsage.KeyboardReturnOrEnter => Key.Enter,
            UIKeyboardHidUsage.KeyboardInsert => Key.Insert,
            UIKeyboardHidUsage.KeyboardDeleteForward => Key.Delete,
            UIKeyboardHidUsage.KeyboardHome => Key.Home,
            UIKeyboardHidUsage.KeyboardEnd => Key.End,
            UIKeyboardHidUsage.KeyboardPageUp => Key.PageUp,
            UIKeyboardHidUsage.KeyboardPageDown => Key.PageDown,
            UIKeyboardHidUsage.KeyboardPrintScreen => Key.PrintScreen,
            UIKeyboardHidUsage.KeyboardPause => Key.Pause,

            UIKeyboardHidUsage.KeyboardLeftArrow => Key.Left,
            UIKeyboardHidUsage.KeyboardRightArrow => Key.Right,
            UIKeyboardHidUsage.KeyboardUpArrow => Key.Up,
            UIKeyboardHidUsage.KeyboardDownArrow => Key.Down,

            UIKeyboardHidUsage.KeyboardLeftShift => Key.LeftShift,
            UIKeyboardHidUsage.KeyboardRightShift => Key.RightShift,
            UIKeyboardHidUsage.KeyboardLeftControl => Key.LeftControl,
            UIKeyboardHidUsage.KeyboardRightControl => Key.RightControl,
            UIKeyboardHidUsage.KeyboardLeftAlt => Key.LeftAlt,
            UIKeyboardHidUsage.KeyboardRightAlt => Key.RightAlt,
            UIKeyboardHidUsage.KeyboardLeftGui => Key.LeftMeta,
            UIKeyboardHidUsage.KeyboardRightGui => Key.RightMeta,

            UIKeyboardHidUsage.KeypadNumLock => Key.NumLock,
            UIKeyboardHidUsage.Keypad0 => Key.Numpad0,
            UIKeyboardHidUsage.Keypad1 => Key.Numpad1,
            UIKeyboardHidUsage.Keypad2 => Key.Numpad2,
            UIKeyboardHidUsage.Keypad3 => Key.Numpad3,
            UIKeyboardHidUsage.Keypad4 => Key.Numpad4,
            UIKeyboardHidUsage.Keypad5 => Key.Numpad5,
            UIKeyboardHidUsage.Keypad6 => Key.Numpad6,
            UIKeyboardHidUsage.Keypad7 => Key.Numpad7,
            UIKeyboardHidUsage.Keypad8 => Key.Numpad8,
            UIKeyboardHidUsage.Keypad9 => Key.Numpad9,
            UIKeyboardHidUsage.KeypadPlus => Key.NumpadAdd,
            UIKeyboardHidUsage.KeypadHyphen => Key.NumpadSubtract,
            UIKeyboardHidUsage.KeypadAsterisk => Key.NumpadMultiply,
            UIKeyboardHidUsage.KeypadSlash => Key.NumpadDivide,
            UIKeyboardHidUsage.KeypadPeriod => Key.NumpadDecimal,
            UIKeyboardHidUsage.KeypadEnter => Key.NumpadEnter,

            UIKeyboardHidUsage.KeyboardHyphen => Key.Minus,
            UIKeyboardHidUsage.KeyboardEqualSign => Key.Equals,
            UIKeyboardHidUsage.KeyboardOpenBracket => Key.LeftBracket,
            UIKeyboardHidUsage.KeyboardCloseBracket => Key.RightBracket,
            UIKeyboardHidUsage.KeyboardBackslash => Key.Backslash,
            UIKeyboardHidUsage.KeyboardSemicolon => Key.Semicolon,
            UIKeyboardHidUsage.KeyboardQuote => Key.Apostrophe,
            UIKeyboardHidUsage.KeyboardGraveAccentAndTilde => Key.Grave,
            UIKeyboardHidUsage.KeyboardComma => Key.Comma,
            UIKeyboardHidUsage.KeyboardPeriod => Key.Period,
            UIKeyboardHidUsage.KeyboardSlash => Key.Slash,

            _ => Key.None,
        };
    }

    /// <summary>Maps <see cref="UIKeyModifierFlags"/> to the engine's <see cref="ModifierKeys"/>.</summary>
    public static ModifierKeys FromModifierFlags(
        UIKeyModifierFlags flags)
    {
        var result = ModifierKeys.None;

        if ((flags & UIKeyModifierFlags.Shift) != 0)
        {
            result |= ModifierKeys.Shift;
        }

        if ((flags & UIKeyModifierFlags.Control) != 0)
        {
            result |= ModifierKeys.Control;
        }

        if ((flags & UIKeyModifierFlags.Alternate) != 0)
        {
            result |= ModifierKeys.Alt;
        }

        if ((flags & UIKeyModifierFlags.Command) != 0)
        {
            result |= ModifierKeys.Meta;
        }

        if ((flags & UIKeyModifierFlags.AlphaShift) != 0)
        {
            result |= ModifierKeys.CapsLock;
        }

        if ((flags & UIKeyModifierFlags.NumericPad) != 0)
        {
            result |= ModifierKeys.NumLock;
        }

        return result;
    }
}
