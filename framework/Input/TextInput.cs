// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Platform.Input;

namespace DotGame.Framework.Input;

/// <summary>
/// A live, framework-owned text-input control backed by the platform's
/// native text editor (browser <c>&lt;input&gt;</c>, iOS
/// <c>UITextField</c>, Android <c>EditText</c>, SDL3 text-input).
/// Created via <see cref="InputService.CreateTextInput"/>; the handle
/// is registered continuously with the platform so that user gestures
/// (taps on the canvas-projected <see cref="Bounds"/>) can focus the
/// editor synchronously inside the trusted gesture handler — required
/// by iOS Safari to bring up the soft keyboard.
/// </summary>
/// <remarks>
/// Mutating <see cref="Bounds"/>, <see cref="Text"/>,
/// <see cref="InputType"/>, <see cref="MaxLength"/>, or
/// <see cref="IsMultiline"/> pushes the new state to the platform editor.
/// Events are raised on the game thread.
/// </remarks>
public sealed class TextInput : IDisposable
{
    private readonly InputService _service;
    private ITextInputHandle? _handle;
    private bool _isDisposed;
    private bool _isFocused;

    private string _text = string.Empty;
    private Rectangle _bounds;
    private TextInputType _inputType;
    private int? _maxLength;
    private bool _multiline;

    // Last state pushed to the platform; used to skip redundant updates.
    private TextInputState _lastPushed;
    private bool _hasPushed;

    internal TextInput(
        InputService service)
    {
        this._service = service;
    }

    internal void AttachHandle(
        ITextInputHandle handle)
    {
        this._handle = handle;
        this.PushStateIfChanged();
    }

    /// <summary>
    /// Soft-keyboard / IME hint.
    /// </summary>
    public TextInputType InputType
    {
        get => this._inputType;
        set
        {
            if (this._inputType == value)
            {
                return;
            }
            this._inputType = value;
            this.PushStateIfChanged();
        }
    }

    /// <summary>
    /// The most characters it takes, or null for no limit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? MaxLength
    {
        get => this._maxLength;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The length can't be negative, or null for no limit.");
            }

            if (this._maxLength == value)
            {
                return;
            }
            this._maxLength = value;
            this.PushStateIfChanged();
        }
    }

    /// <summary>
    /// When <c>true</c>, the editor accepts newlines and Enter inserts
    /// a line break instead of raising <see cref="Submitted"/>.
    /// </summary>
    public bool IsMultiline
    {
        get => this._multiline;
        set
        {
            if (this._multiline == value)
            {
                return;
            }
            this._multiline = value;
            this.PushStateIfChanged();
        }
    }

    /// <summary>
    /// The current text value. Setting this property pushes the new
    /// value into the platform editor (e.g. for clamping/sanitization).
    /// </summary>
    public string Text
    {
        get => this._text;
        set
        {
            value ??= string.Empty;
            if (this._text == value)
            {
                return;
            }
            this._text = value;
            this.PushStateIfChanged();
        }
    }

    /// <summary>
    /// Position and size of the on-canvas text field in viewport pixels.
    /// The platform editor's transparent overlay is positioned to match
    /// so that user taps inside this rectangle are captured natively by
    /// the browser and focus the editor synchronously (required by iOS
    /// Safari to show the soft keyboard).
    /// </summary>
    /// <remarks>
    /// On non-overlay platforms (iOS/Android/SDL3) the bounds are
    /// recorded but the visible representation is drawn by the game.
    /// Setting an empty rectangle hides the overlay.
    /// </remarks>
    public Rectangle Bounds
    {
        get => this._bounds;
        set
        {
            if (this._bounds == value)
            {
                return;
            }
            this._bounds = value;
            this.PushStateIfChanged();
        }
    }

    /// <summary>
    /// <c>true</c> while the platform editor reports keyboard focus.
    /// </summary>
    public bool IsFocused => this._isFocused;

    /// <summary>
    /// Raised when the platform editor's text changes.
    /// </summary>
    public event EventHandler? TextChanged;

    /// <summary>
    /// The text an input method is composing (Japanese or Chinese input, say), not yet part of
    /// <see cref="Text"/>, for a game to show at the end of it; empty when there is none. On
    /// Android and iOS, the system's editor keeps composing text in <see cref="Text"/> itself, so
    /// this stays empty.
    /// </summary>
    public string Composition { get; private set; } = string.Empty;

    /// <summary>
    /// Raised when <see cref="Composition"/> changes.
    /// </summary>
    public event EventHandler? CompositionChanged;

    /// <summary>
    /// Raised when the platform editor gains keyboard focus (user tap,
    /// programmatic <see cref="Focus"/>, etc.).
    /// </summary>
    public event EventHandler? GotFocus;

    /// <summary>
    /// Raised when the platform editor loses keyboard focus.
    /// </summary>
    public event EventHandler? LostFocus;

    /// <summary>
    /// Raised when the user commits the edit (Enter / IME-Done).
    /// Does not automatically blur the editor.
    /// </summary>
    public event EventHandler? Submitted;

    /// <summary>
    /// Raised when the user dismisses the edit (Escape / Back).
    /// Does not automatically blur the editor.
    /// </summary>
    public event EventHandler? Canceled;

    /// <summary>
    /// Programmatically gives keyboard focus to the platform editor.
    /// </summary>
    /// <remarks>
    /// On iOS Safari and Chrome-iOS the soft keyboard only appears
    /// when <see cref="Focus"/> is invoked synchronously inside a
    /// trusted user-gesture handler (e.g. in response to a tap). When called outside of a gesture (for example from
    /// a scene's <c>OnNavigatedTo</c>), WebKit moves keyboard focus
    /// silently but suppresses the soft keyboard. The WebGL platform
    /// detects this and defers raising <see cref="GotFocus"/> until
    /// real input is observed (a user gesture, key, or pointer event
    /// on the overlay), unless a hardware keyboard is detected via
    /// the platform's precision-input media query, in which case
    /// focus is confirmed immediately. This matches the behavior
    /// documented by Flutter Web, Unity WebGL, Godot Web, Blazor,
    /// and MAUI Blazor Hybrid.
    /// <para>
    /// A focus request the platform muted is not retried: on the web the
    /// user's tap on the field's transparent overlay focuses it natively
    /// and raises the soft keyboard, which is the expected mobile
    /// interaction.
    /// </para>
    /// </remarks>
    public void Focus()
    {
        if (this._isDisposed)
        {
            return;
        }
        this._handle?.Focus();
    }

    /// <summary>
    /// Programmatically removes keyboard focus from the platform editor.
    /// </summary>
    public void Unfocus()
    {
        if (this._isDisposed)
        {
            return;
        }
        this._handle?.Blur();
    }

    /// <summary>
    /// Releases the platform editor.
    /// </summary>
    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }
        this._isDisposed = true;
        var handle = this._handle;
        this._handle = null;
        handle?.Dispose();
    }

    internal bool IsDisposed => this._isDisposed;

    private void PushStateIfChanged()
    {
        if ((this._isDisposed) || (this._handle is null))
        {
            return;
        }

        var state = new TextInputState(
            this._bounds,
            this._text,
            this._inputType,
            this._maxLength ?? -1,
            this._multiline);

        if ((this._hasPushed) && (state.Equals(this._lastPushed)))
        {
            return;
        }

        this._lastPushed = state;
        this._hasPushed = true;
        this._handle.Update(in state);
    }

    // --- Sink callbacks (invoked on game thread by InputService dispatch) ---

    internal void RaiseTextChanged(
        string text)
    {
        text ??= string.Empty;
        if (this._text == text)
        {
            return;
        }
        // The native value already matches `text`; record it locally
        // without echoing it back to the platform.
        this._text = text;
        // Update the cached pushed-state so a subsequent property
        // change correctly diff-detects.
        this._lastPushed = new TextInputState(
            this._bounds,
            this._text,
            this._inputType,
            this._maxLength ?? -1,
            this._multiline);
        this.TextChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseCompositionChanged(
        string composition)
    {
        if (composition != this.Composition)
        {
            this.Composition = composition;
            this.CompositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void RaiseGotFocus()
    {
        if (this._isFocused)
        {
            return;
        }
        this._isFocused = true;
        this.GotFocus?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseLostFocus()
    {
        if (!this._isFocused)
        {
            return;
        }
        this._isFocused = false;
        this.LostFocus?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseSubmitted()
    {
        this.Submitted?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseCanceled()
    {
        this.Canceled?.Invoke(this, EventArgs.Empty);
    }
}
