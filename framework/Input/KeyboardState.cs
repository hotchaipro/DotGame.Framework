// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>
/// A snapshot of the current keyboard state, for code that prefers polling
/// over the event queue (e.g. continuous movement during update).
/// </summary>
public readonly struct KeyboardState
{
    private readonly ulong _keysDownLow;
    private readonly ulong _keysDownHigh;

    internal KeyboardState(
        ulong keysDownLow,
        ulong keysDownHigh,
        ModifierKeys modifiers)
    {
        this._keysDownLow = keysDownLow;
        this._keysDownHigh = keysDownHigh;
        this.Modifiers = modifiers;
    }

    /// <summary>Modifier keys currently held down.</summary>
    public ModifierKeys Modifiers { get; }

    /// <summary>Returns <c>true</c> if the specified key is currently held down.</summary>
    public bool IsKeyDown(Key key)
    {
        int index = (int)key;
        if ((index < 0) || (index >= KeyInfo.Count))
        {
            return false;
        }

        return (((index < 64) ? this._keysDownLow : this._keysDownHigh) & (1UL << (index & 63))) != 0;
    }
}
