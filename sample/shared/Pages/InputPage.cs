// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;

namespace Sample.Pages;

/// <summary>
/// Pointer events (mouse, touch and pen, each finger tracked by its id), keyboard events and
/// the keyboard's state, and <see cref="TextInput"/>, which edits text with the platform's own
/// editor and soft keyboard.
/// </summary>
internal sealed class InputPage : SamplePage
{
    private const int MaxLogLines = 6;

    private readonly Dictionary<int, PointerEvent> _pointers = new();
    private readonly List<string> _log = new();
    private Texture _circle = null!;
    private TextInput? _textInput;
    private string _submitted = string.Empty;

    // When the caret's blink last restarted: it stays on while the text changes, as in a native editor
    private double _caretStart;
    private float _wheel;

    public InputPage(
        FrameworkSample game)
        : base(game)
    {
    }

    public override string Title => "Input";

    public override string Hint => "Touch, click, scroll and press keys. Tap the field to type.";

    public override async Task Load()
    {
        this._circle = await this.Game.Content.LoadAsync<Texture>("Textures/circle");
    }

    public override void Enter()
    {
        // A text input is a live control, registered with the platform until it's disposed
        this._textInput = this.Game.Input.CreateTextInput();
        this._textInput.InputType = TextInputType.Text;
        this._textInput.MaxLength = 24;
        this._textInput.TextChanged += (_, _) =>
        {
            this.Log("Text changed");
            this.RestartCaret();
        };
        this._textInput.CompositionChanged += (_, _) => this.RestartCaret();
        this._textInput.GotFocus += (_, _) => this.RestartCaret();
        this._textInput.Submitted += (_, _) =>
        {
            this._submitted = this._textInput.Text;
            this._textInput.Unfocus();
        };
        this._textInput.Canceled += (_, _) => this._textInput.Unfocus();
    }

    public override void Leave()
    {
        this._textInput?.Dispose();
        this._textInput = null;
        this._pointers.Clear();
    }

    private void Log(
        string line)
    {
        this._log.Add(line);
        if (this._log.Count > MaxLogLines)
        {
            this._log.RemoveAt(0);
        }
    }

    public override void OnPointerEvent(
        PointerEvent e)
    {
        // A pointer's events share its id from Began to Ended or Canceled
        switch (e.Kind)
        {
            case PointerEventKind.Began:
            case PointerEventKind.Moved:
                this._pointers[e.Id] = e;
                break;

            case PointerEventKind.Ended:
            case PointerEventKind.Canceled:
                this._pointers.Remove(e.Id);
                break;
        }

        this._wheel += e.ScrollWheelDelta.Y;

        if (e.Kind != PointerEventKind.Moved)
        {
            this.Log($"{e.Device} {e.Kind} id {e.Id} {e.Button} touches {e.TouchCount}");
        }

        // Tapping the field focuses the editor, which brings up the soft keyboard. On the web
        // and iOS it has to happen during the tap, which is why the field has Bounds: the
        // platform puts its editor over them.
        if ((this._textInput is not null) && (e.Kind == PointerEventKind.Began))
        {
            if (this._textInput.Bounds.Contains(e.Position))
            {
                this._textInput.Focus();
            }
            else if (this._textInput.IsFocused)
            {
                this._textInput.Unfocus();
            }
        }
    }

    public override void OnKeyEvent(
        KeyEvent e)
    {
        if (!e.IsRepeat)
        {
            this.Log($"Key {e.Key} {e.Kind} {e.Modifiers}");
        }
    }

    public override void Draw(
        SpriteBatch spriteBatch,
        RectangleInt bounds)
    {
        var game = this.Game;
        var text = game.Text;
        using var batch = spriteBatch.Begin();

        // A circle under every pointer, sized by its button
        foreach (var (id, e) in this._pointers)
        {
            var color = e.Button switch
            {
                PointerButton.Right => Color.FromRgb(0xF0785A),
                PointerButton.Middle => Color.FromRgb(0xF0DC5A),
                _ => Color.FromRgb(0x5AB4F0),
            };
            spriteBatch.Draw(this._circle, e.Position, color * 0.7f, Angle.Zero, (Vector2)this._circle.Size * 0.5f, new Vector2(1.2f));
            text.Draw(spriteBatch, id.ToString(), e.Position + new Vector2(-10, -16), Color.White, 0.8f);
        }

        // The keyboard's state, read any time rather than from events
        var keyboard = game.Input.GetKeyboardState();
        var x = bounds.X + 16;
        var y = bounds.Y + 16;
        text.Draw(spriteBatch, "Held:", new Vector2(x, y), Color.Gray, 0.7f);
        DrawKey(spriteBatch, text, keyboard, Key.Up, "Up", x + 90, y);
        DrawKey(spriteBatch, text, keyboard, Key.Down, "Down", x + 150, y);
        DrawKey(spriteBatch, text, keyboard, Key.Left, "Left", x + 230, y);
        DrawKey(spriteBatch, text, keyboard, Key.Right, "Right", x + 310, y);
        DrawKey(spriteBatch, text, keyboard, Key.Space, "Space", x + 400, y);
        text.Draw(spriteBatch, $"Modifiers: {keyboard.Modifiers}   Wheel: {this._wheel:0}", new Vector2(x, y + 28), Color.Gray, 0.7f);

        // The text field: drawn by the game, edited by the platform
        if (this._textInput is { } input)
        {
            var field = new RectangleInt(x, y + 72, Math.Min(bounds.Width - 32, 480), 48);
            input.Bounds = new Rectangle(field.X, field.Y, field.Width, field.Height);
            game.FillRectangle(spriteBatch, field, input.IsFocused ? Color.FromRgb(0xFAFAFF) : Color.FromRgb(0xC8CDDC));
            var textPosition = new Vector2(field.X + 10, field.Y + 10);
            if ((input.Text.Length > 0) || input.IsFocused)
            {
                text.Draw(spriteBatch, input.Text, textPosition, Color.Black, 0.8f);
            }
            else
            {
                text.Draw(spriteBatch, "Tap to type", textPosition, Color.Gray, 0.8f);
            }

            // The game draws the caret too. The platform's editor is invisible, and edits only at the
            // end of the text, so the caret goes there, after any text an input method is composing
            // (Japanese or Chinese input, say), which is underlined as it isn't part of the text yet
            var caretX = textPosition.X + PixelText.Measure(input.Text, 0.8f);
            if (input.Composition.Length > 0)
            {
                text.Draw(spriteBatch, input.Composition, new Vector2(caretX, textPosition.Y), Color.FromRgb(0x3C5AB4), 0.8f);
                var compositionWidth = PixelText.Measure(input.Composition, 0.8f);
                game.FillRectangle(spriteBatch, new RectangleInt((int)caretX, field.Bottom - 10, (int)compositionWidth, 2), Color.FromRgb(0x3C5AB4));
                caretX += compositionWidth;
            }

            // On for half a second, off for half a second
            if (input.IsFocused && (((game.Time.Total.TotalSeconds - this._caretStart) % 1) < 0.5))
            {
                game.FillRectangle(spriteBatch, new RectangleInt((int)caretX + 1, field.Y + 10, 2, field.Height - 20), Color.Black);
            }

            if (this._submitted.Length > 0)
            {
                text.Draw(spriteBatch, $"Submitted: {this._submitted}", new Vector2(x, field.Bottom + 8), Color.FromRgb(0x78DC8C), 0.7f);
            }
        }

        // The latest events
        var logY = bounds.Bottom - 24 - (MaxLogLines * 22);
        foreach (var line in this._log)
        {
            text.Draw(spriteBatch, line, new Vector2(x, logY), Color.FromRgb(0xAAB4C8), 0.6f);
            logY += 22;
        }

        // The first gamepad's state, read the same way
        var gamepad = game.Input.GetGamepadState(0);
        if (gamepad.IsConnected)
        {
            text.Draw(spriteBatch, FormattableString.Invariant($"Gamepad: stick {gamepad.GetAxis(GamepadAxis.LeftX):F2}, {gamepad.GetAxis(GamepadAxis.LeftY):F2}  trigger {gamepad.GetAxis(GamepadAxis.RightTrigger):F2}  South {(gamepad.IsButtonDown(GamepadButton.South) ? "down" : "up")}"), new Vector2(x, y + 28), Color.White, 0.6f);
        }
    }

    private static void DrawKey(
        SpriteBatch spriteBatch,
        PixelText text,
        in KeyboardState keyboard,
        Key key,
        string label,
        float x,
        float y)
    {
        text.Draw(spriteBatch, label, new Vector2(x, y), keyboard.IsKeyDown(key) ? Color.Yellow : Color.FromRgb(0x505A6E), 0.7f);
    }

    private void RestartCaret()
    {
        this._caretStart = this.Game.Time.Total.TotalSeconds;
    }
}
