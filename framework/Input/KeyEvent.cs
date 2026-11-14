// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Input;

/// <summary>
/// An immutable keyboard input event, handed to <c>Game.OnKeyEvent</c>.
/// </summary>
public readonly record struct KeyEvent
{
    internal KeyEvent(
        KeyEventKind kind,
        Key key,
        ModifierKeys modifiers = ModifierKeys.None,
        bool isRepeat = false,
        int virtualKey = 0)
    {
        this.Kind = kind;
        this.Key = key;
        this.Modifiers = modifiers;
        this.IsRepeat = isRepeat;
        this.VirtualKey = virtualKey;
    }

    /// <summary>Whether the key was pressed or released.</summary>
    public KeyEventKind Kind { get; }

    /// <summary>Layout-independent physical key. Use this for gameplay bindings.</summary>
    public Key Key { get; }

    /// <summary>Modifier keys held at the moment the event was generated.</summary>
    public ModifierKeys Modifiers { get; }

    /// <summary>
    /// True if this <see cref="KeyEventKind.Pressed"/> event was produced by
    /// OS auto-repeat rather than a fresh keystroke.
    /// </summary>
    public bool IsRepeat { get; }

    /// <summary>
    /// Raw platform virtual-key / keysym for advanced scenarios (e.g. debug
    /// overlays). Opaque across platforms; prefer <see cref="Key"/>.
    /// </summary>
    internal int VirtualKey { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return FormattableString.Invariant($"Kind: {this.Kind} Key: {this.Key} Modifiers: {this.Modifiers} IsRepeat: {this.IsRepeat}");
    }
}
