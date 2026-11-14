// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>
/// A layout-independent physical key identifier (scancode).
/// </summary>
/// <remarks>
/// Values correspond to the physical position of the key on a US QWERTY
/// keyboard, matching SDL's <c>SDL_Scancode</c> and the W3C UI Events
/// <c>KeyboardEvent.code</c> property. Gameplay bindings should generally
/// use <see cref="Key"/> so that, e.g., WASD remains in the same physical
/// position on AZERTY or Dvorak layouts.
/// </remarks>
public enum Key
{
    /// <summary>No key.</summary>
    None = 0,

    // Letters (physical positions)
    /// <summary>The A key.</summary>
    A,
    /// <summary>The B key.</summary>
    B,
    /// <summary>The C key.</summary>
    C,
    /// <summary>The D key.</summary>
    D,
    /// <summary>The E key.</summary>
    E,
    /// <summary>The F key.</summary>
    F,
    /// <summary>The G key.</summary>
    G,
    /// <summary>The H key.</summary>
    H,
    /// <summary>The I key.</summary>
    I,
    /// <summary>The J key.</summary>
    J,
    /// <summary>The K key.</summary>
    K,
    /// <summary>The L key.</summary>
    L,
    /// <summary>The M key.</summary>
    M,
    /// <summary>The N key.</summary>
    N,
    /// <summary>The O key.</summary>
    O,
    /// <summary>The P key.</summary>
    P,
    /// <summary>The Q key.</summary>
    Q,
    /// <summary>The R key.</summary>
    R,
    /// <summary>The S key.</summary>
    S,
    /// <summary>The T key.</summary>
    T,
    /// <summary>The U key.</summary>
    U,
    /// <summary>The V key.</summary>
    V,
    /// <summary>The W key.</summary>
    W,
    /// <summary>The X key.</summary>
    X,
    /// <summary>The Y key.</summary>
    Y,
    /// <summary>The Z key.</summary>
    Z,

    // Top-row digits
    /// <summary>The 0 key in the top row.</summary>
    D0,
    /// <summary>The 1 key in the top row.</summary>
    D1,
    /// <summary>The 2 key in the top row.</summary>
    D2,
    /// <summary>The 3 key in the top row.</summary>
    D3,
    /// <summary>The 4 key in the top row.</summary>
    D4,
    /// <summary>The 5 key in the top row.</summary>
    D5,
    /// <summary>The 6 key in the top row.</summary>
    D6,
    /// <summary>The 7 key in the top row.</summary>
    D7,
    /// <summary>The 8 key in the top row.</summary>
    D8,
    /// <summary>The 9 key in the top row.</summary>
    D9,

    // Function keys
    /// <summary>The F1 key.</summary>
    F1,
    /// <summary>The F2 key.</summary>
    F2,
    /// <summary>The F3 key.</summary>
    F3,
    /// <summary>The F4 key.</summary>
    F4,
    /// <summary>The F5 key.</summary>
    F5,
    /// <summary>The F6 key.</summary>
    F6,
    /// <summary>The F7 key.</summary>
    F7,
    /// <summary>The F8 key.</summary>
    F8,
    /// <summary>The F9 key.</summary>
    F9,
    /// <summary>The F10 key.</summary>
    F10,
    /// <summary>The F11 key.</summary>
    F11,
    /// <summary>The F12 key.</summary>
    F12,

    // Control keys
    /// <summary>The escape key.</summary>
    Escape,
    /// <summary>The tab key.</summary>
    Tab,
    /// <summary>The caps lock key.</summary>
    CapsLock,
    /// <summary>The space key.</summary>
    Space,
    /// <summary>The backspace key.</summary>
    Backspace,
    /// <summary>The enter key.</summary>
    Enter,
    /// <summary>The insert key.</summary>
    Insert,
    /// <summary>The delete key.</summary>
    Delete,
    /// <summary>The home key.</summary>
    Home,
    /// <summary>The end key.</summary>
    End,
    /// <summary>The page up key.</summary>
    PageUp,
    /// <summary>The page down key.</summary>
    PageDown,
    /// <summary>The print screen key.</summary>
    PrintScreen,
    /// <summary>The pause key.</summary>
    Pause,

    // Arrows
    /// <summary>The left key.</summary>
    Left,
    /// <summary>The right key.</summary>
    Right,
    /// <summary>The up key.</summary>
    Up,
    /// <summary>The down key.</summary>
    Down,

    // Modifiers (physical - left/right distinguished)
    /// <summary>The left shift key.</summary>
    LeftShift,
    /// <summary>The right shift key.</summary>
    RightShift,
    /// <summary>The left control key.</summary>
    LeftControl,
    /// <summary>The right control key.</summary>
    RightControl,
    /// <summary>The left alt key.</summary>
    LeftAlt,
    /// <summary>The right alt key.</summary>
    RightAlt,
    /// <summary>The left meta key.</summary>
    LeftMeta,
    /// <summary>The right meta key.</summary>
    RightMeta,

    // Numpad
    /// <summary>The num lock key.</summary>
    NumLock,
    /// <summary>The numpad 0 key.</summary>
    Numpad0,
    /// <summary>The numpad 1 key.</summary>
    Numpad1,
    /// <summary>The numpad 2 key.</summary>
    Numpad2,
    /// <summary>The numpad 3 key.</summary>
    Numpad3,
    /// <summary>The numpad 4 key.</summary>
    Numpad4,
    /// <summary>The numpad 5 key.</summary>
    Numpad5,
    /// <summary>The numpad 6 key.</summary>
    Numpad6,
    /// <summary>The numpad 7 key.</summary>
    Numpad7,
    /// <summary>The numpad 8 key.</summary>
    Numpad8,
    /// <summary>The numpad 9 key.</summary>
    Numpad9,
    /// <summary>The numpad add key.</summary>
    NumpadAdd,
    /// <summary>The numpad subtract key.</summary>
    NumpadSubtract,
    /// <summary>The numpad multiply key.</summary>
    NumpadMultiply,
    /// <summary>The numpad divide key.</summary>
    NumpadDivide,
    /// <summary>The numpad decimal key.</summary>
    NumpadDecimal,
    /// <summary>The numpad enter key.</summary>
    NumpadEnter,

    // OEM / punctuation (physical US-layout positions)
    /// <summary>The minus key.</summary>
    Minus,
    /// <summary>The equals key.</summary>
    Equals,
    /// <summary>The left bracket key.</summary>
    LeftBracket,
    /// <summary>The right bracket key.</summary>
    RightBracket,
    /// <summary>The backslash key.</summary>
    Backslash,
    /// <summary>The semicolon key.</summary>
    Semicolon,
    /// <summary>The apostrophe key.</summary>
    Apostrophe,
    /// <summary>The grave key.</summary>
    Grave,
    /// <summary>The comma key.</summary>
    Comma,
    /// <summary>The period key.</summary>
    Period,
    /// <summary>The slash key.</summary>
    Slash,

    // Platform navigation
    /// <summary>
    /// Android Back button / SDL_SCANCODE_AC_BACK. Maps to the same semantic
    /// as <see cref="Escape"/> on desktop — "cancel / navigate back".
    /// </summary>
    Back,

    // NOTE: When adding a key after Back, update KeyInfo.Count below
}
