// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using DotGame.Framework.Input;
using Foundation;
using UIKit;

namespace DotGame.Framework.iOS.Input;

/// <summary>
/// A hardware keyboard (a Magic Keyboard, Smart Keyboard or Bluetooth keyboard), seen through
/// UIKit's press events. Reports presses and releases, repeats a held key as the other platforms'
/// systems do (UIKit doesn't), and releases every held key when the game stops receiving presses,
/// so none stays down.
/// </summary>
internal sealed class Keyboard : IDisposable
{
    // The usual system key repeat: the first repeat after half a second, then 20 a second
    private const double RepeatDelaySeconds = 0.5;
    private const double RepeatIntervalSeconds = 0.05;

    private readonly Func<InputServiceStrategy?> _inputService;
    private readonly Dictionary<UIKeyboardHidUsage, Key> _keysDown = new();
    private ModifierKeys _modifiers;
    private UIKeyboardHidUsage _repeatUsage;
    private Key _repeatKey;
    private NSTimer? _repeatTimer;

    public Keyboard(
        Func<InputServiceStrategy?> inputService)
    {
        ArgumentNullException.ThrowIfNull(inputService);

        this._inputService = inputService;
    }

    /// <summary>
    /// Reports the keyboard presses among <paramref name="presses"/>; false when there were none,
    /// so UIKit passes the event on.
    /// </summary>
    public bool HandlePresses(
        NSSet<UIPress> presses,
        KeyEventKind state)
    {
        var inputService = this._inputService();
        if (inputService is null)
        {
            return false;
        }

        bool handled = false;

        foreach (var press in presses)
        {
            var uiKey = press.Key;
            if (uiKey is null)
            {
                continue;
            }

            var key = KeyMap.FromHIDUsage(uiKey.KeyCode);
            if (key == Key.None)
            {
                continue;
            }

            this._modifiers = KeyMap.FromModifierFlags(uiKey.ModifierFlags);

            if (state == KeyEventKind.Pressed)
            {
                this._keysDown[uiKey.KeyCode] = key;
                if (Repeats(key))
                {
                    this.StartRepeat(uiKey.KeyCode, key);
                }
            }
            else
            {
                this._keysDown.Remove(uiKey.KeyCode);
                if (uiKey.KeyCode == this._repeatUsage)
                {
                    this.StopRepeat();
                }
            }

            inputService.QueueKeyEvent(state, key, this._modifiers, virtualKey: (int)uiKey.KeyCode);
            handled = true;
        }

        return handled;
    }

    /// <summary>
    /// Releases every held key, when presses stop arriving: the app resigns active, or the game
    /// view gives up the keyboard to a text field.
    /// </summary>
    public void ReleaseAll()
    {
        this.StopRepeat();

        var inputService = this._inputService();
        foreach (var held in this._keysDown)
        {
            inputService?.QueueKeyEvent(KeyEventKind.Released, held.Value, ModifierKeys.None, virtualKey: (int)held.Key);
        }

        this._keysDown.Clear();
        this._modifiers = ModifierKeys.None;
    }

    public void Dispose()
    {
        this.StopRepeat();
    }

    // Modifier and lock keys don't repeat
    private static bool Repeats(
        Key key)
    {
        return key switch
        {
            Key.LeftShift or Key.RightShift or Key.LeftControl or Key.RightControl or
            Key.LeftAlt or Key.RightAlt or Key.LeftMeta or Key.RightMeta or Key.CapsLock => false,
            _ => true,
        };
    }

    // The most recently pressed key repeats, as on other systems
    private void StartRepeat(
        UIKeyboardHidUsage usage,
        Key key)
    {
        this.StopRepeat();

        this._repeatUsage = usage;
        this._repeatKey = key;
        this._repeatTimer = NSTimer.CreateRepeatingScheduledTimer(RepeatIntervalSeconds, _ => this.Repeat());
        this._repeatTimer.FireDate = NSDate.FromTimeIntervalSinceNow(RepeatDelaySeconds);
    }

    private void StopRepeat()
    {
        this._repeatTimer?.Invalidate();
        this._repeatTimer?.Dispose();
        this._repeatTimer = null;
        this._repeatUsage = 0;
        this._repeatKey = Key.None;
    }

    private void Repeat()
    {
        if (this._repeatKey == Key.None)
        {
            return;
        }

        this._inputService()?.QueueKeyEvent(KeyEventKind.Pressed, this._repeatKey, this._modifiers, isRepeat: true, virtualKey: (int)this._repeatUsage);
    }
}
