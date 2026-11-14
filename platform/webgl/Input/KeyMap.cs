// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Generic;
using System.Runtime.Versioning;
using DotGame.Framework.Input;

namespace DotGame.Framework.WebGL.Input;

[SupportedOSPlatform("browser")]
internal static class KeyMap
{
    /// <summary>
    /// Maps a DOM <c>KeyboardEvent.code</c> string (e.g. <c>"KeyW"</c>,
    /// <c>"ArrowLeft"</c>) to the engine's layout-independent <see cref="Key"/>.
    /// Returns <see cref="Key.None"/> for codes not modeled by the engine.
    /// </summary>
    /// <remarks>
    /// The DOM <c>code</c> property is physical-position-based (like SDL
    /// scancodes), making it the correct source for gameplay bindings.
    /// </remarks>
    public static Key FromDomCode(
        string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return Key.None;
        }

        return s_codeToKey.TryGetValue(code, out var key) ? key : Key.None;
    }

    private static readonly Dictionary<string, Key> s_codeToKey = new()
    {
        // Letters
        ["KeyA"] = Key.A,
        ["KeyB"] = Key.B,
        ["KeyC"] = Key.C,
        ["KeyD"] = Key.D,
        ["KeyE"] = Key.E,
        ["KeyF"] = Key.F,
        ["KeyG"] = Key.G,
        ["KeyH"] = Key.H,
        ["KeyI"] = Key.I,
        ["KeyJ"] = Key.J,
        ["KeyK"] = Key.K,
        ["KeyL"] = Key.L,
        ["KeyM"] = Key.M,
        ["KeyN"] = Key.N,
        ["KeyO"] = Key.O,
        ["KeyP"] = Key.P,
        ["KeyQ"] = Key.Q,
        ["KeyR"] = Key.R,
        ["KeyS"] = Key.S,
        ["KeyT"] = Key.T,
        ["KeyU"] = Key.U,
        ["KeyV"] = Key.V,
        ["KeyW"] = Key.W,
        ["KeyX"] = Key.X,
        ["KeyY"] = Key.Y,
        ["KeyZ"] = Key.Z,

        // Top-row digits
        ["Digit0"] = Key.D0,
        ["Digit1"] = Key.D1,
        ["Digit2"] = Key.D2,
        ["Digit3"] = Key.D3,
        ["Digit4"] = Key.D4,
        ["Digit5"] = Key.D5,
        ["Digit6"] = Key.D6,
        ["Digit7"] = Key.D7,
        ["Digit8"] = Key.D8,
        ["Digit9"] = Key.D9,

        // Function keys
        ["F1"] = Key.F1,
        ["F2"] = Key.F2,
        ["F3"] = Key.F3,
        ["F4"] = Key.F4,
        ["F5"] = Key.F5,
        ["F6"] = Key.F6,
        ["F7"] = Key.F7,
        ["F8"] = Key.F8,
        ["F9"] = Key.F9,
        ["F10"] = Key.F10,
        ["F11"] = Key.F11,
        ["F12"] = Key.F12,

        // Control keys
        ["Escape"] = Key.Escape,
        ["Tab"] = Key.Tab,
        ["CapsLock"] = Key.CapsLock,
        ["Space"] = Key.Space,
        ["Backspace"] = Key.Backspace,
        ["Enter"] = Key.Enter,
        ["Insert"] = Key.Insert,
        ["Delete"] = Key.Delete,
        ["Home"] = Key.Home,
        ["End"] = Key.End,
        ["PageUp"] = Key.PageUp,
        ["PageDown"] = Key.PageDown,
        ["PrintScreen"] = Key.PrintScreen,
        ["Pause"] = Key.Pause,

        // Arrows
        ["ArrowLeft"] = Key.Left,
        ["ArrowRight"] = Key.Right,
        ["ArrowUp"] = Key.Up,
        ["ArrowDown"] = Key.Down,

        // Modifiers
        ["ShiftLeft"] = Key.LeftShift,
        ["ShiftRight"] = Key.RightShift,
        ["ControlLeft"] = Key.LeftControl,
        ["ControlRight"] = Key.RightControl,
        ["AltLeft"] = Key.LeftAlt,
        ["AltRight"] = Key.RightAlt,
        ["MetaLeft"] = Key.LeftMeta,
        ["MetaRight"] = Key.RightMeta,
        ["OSLeft"] = Key.LeftMeta,   // Firefox legacy
        ["OSRight"] = Key.RightMeta, // Firefox legacy

        // Numpad
        ["NumLock"] = Key.NumLock,
        ["Numpad0"] = Key.Numpad0,
        ["Numpad1"] = Key.Numpad1,
        ["Numpad2"] = Key.Numpad2,
        ["Numpad3"] = Key.Numpad3,
        ["Numpad4"] = Key.Numpad4,
        ["Numpad5"] = Key.Numpad5,
        ["Numpad6"] = Key.Numpad6,
        ["Numpad7"] = Key.Numpad7,
        ["Numpad8"] = Key.Numpad8,
        ["Numpad9"] = Key.Numpad9,
        ["NumpadAdd"] = Key.NumpadAdd,
        ["NumpadSubtract"] = Key.NumpadSubtract,
        ["NumpadMultiply"] = Key.NumpadMultiply,
        ["NumpadDivide"] = Key.NumpadDivide,
        ["NumpadDecimal"] = Key.NumpadDecimal,
        ["NumpadEnter"] = Key.NumpadEnter,

        // OEM / punctuation
        ["Minus"] = Key.Minus,
        ["Equal"] = Key.Equals,
        ["BracketLeft"] = Key.LeftBracket,
        ["BracketRight"] = Key.RightBracket,
        ["Backslash"] = Key.Backslash,
        ["Semicolon"] = Key.Semicolon,
        ["Quote"] = Key.Apostrophe,
        ["Backquote"] = Key.Grave,
        ["Comma"] = Key.Comma,
        ["Period"] = Key.Period,
        ["Slash"] = Key.Slash,

        // Platform navigation
        ["BrowserBack"] = Key.Back,
        ["GoBack"] = Key.Back,      // Android WebView variant
    };
}
