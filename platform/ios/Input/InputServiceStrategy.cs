// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using CoreGraphics;
using DotGame.Framework.Input;
using DotGame.Framework.Platform.Common;
using DotGame.Framework.Platform.Common.Input;
using DotGame.Framework.Platform.Input;
using Foundation;
using UIKit;

namespace DotGame.Framework.iOS.Input;

internal sealed class InputServiceStrategy : IInputServiceStrategy
{
    private readonly GameHost _host;
    private readonly UIWindow _window;

    internal InputServiceStrategy(
        GameHost host,
        UIWindow window)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(window);

        this._host = host;
        this._window = window;
    }

    internal void QueueKeyEvent(
        KeyEventKind state,
        Key key,
        ModifierKeys modifiers,
        bool isRepeat = false,
        int virtualKey = 0)
    {
        if (key == Key.None)
        {
            return;
        }

        this._host.QueueKeyEvent(new KeyEventData
        {
            State = state,
            Key = key,
            Modifiers = modifiers,
            IsRepeat = isRepeat,
            VirtualKey = virtualKey
        });
    }

    public Task OnInitialize()
    {
        return Task.CompletedTask;
    }

    public ITextInputHandle CreateTextInputHandle(
        ITextInputSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        return new TextInputHandle(this, sink);
    }

    private static bool IsValidCodePoint(
        TextInputType inputType,
        int codePoint,
        bool allowNewline)
    {
        return TextInputCodePoints.IsValid(inputType, codePoint, allowNewline);
    }

    private sealed class TextInputHandle : ITextInputHandle
    {
        private readonly ITextInputSink _sink;
        private NativeTextField? _field;
        private TextInputState _state;
        private bool _isDisposed;

        public TextInputHandle(
            InputServiceStrategy service,
            ITextInputSink sink)
        {
            this._sink = sink;
            this._field = new NativeTextField(this);
            this._field.SetInputType(this._state.InputType, this._state.Multiline);
            service._window.RootViewController?.View?.AddSubview(this._field);
        }

        internal ITextInputSink Sink => this._sink;
        internal int MaxLength => this._state.MaxLength;
        internal TextInputType InputType => this._state.InputType;
        internal bool Multiline => this._state.Multiline;

        public void Update(in TextInputState state)
        {
            if (this._isDisposed || this._field is null)
            {
                return;
            }

            var prev = this._state;
            this._state = state;

            if ((prev.InputType != state.InputType) || (prev.Multiline != state.Multiline))
            {
                this._field.SetInputType(state.InputType, state.Multiline);
            }

            if (this._field.Text != state.Text)
            {
                this._field.Text = state.Text ?? string.Empty;
            }
        }

        public void Focus()
        {
            if (this._isDisposed || this._field is null)
            {
                return;
            }
            this._field.BecomeFirstResponder();
        }

        public void Blur()
        {
            if (this._isDisposed || this._field is null)
            {
                return;
            }
            if (this._field.IsFirstResponder)
            {
                this._field.ResignFirstResponder();
            }
        }

        public void Dispose()
        {
            if (this._isDisposed)
            {
                return;
            }
            this._isDisposed = true;
            var field = this._field;
            this._field = null;
            if (field is not null)
            {
                if (field.IsFirstResponder)
                {
                    field.ResignFirstResponder();
                }
                field.RemoveFromSuperview();
                field.Dispose();
            }
        }
    }

    private sealed class NativeTextField : UITextField
    {
        private readonly TextInputHandle _handle;

        public NativeTextField(
            TextInputHandle handle)
            : base(CGRect.Empty)
        {
            this._handle = handle;
            this.Hidden = true;
            this.ShouldChangeCharacters = this.OnChangeCharacters;
            this.EditingChanged += this.OnEditingChanged;
            this.EditingDidBegin += this.OnEditingDidBegin;
            this.EditingDidEnd += this.OnEditingDidEnd;
            this.ShouldReturn = this.OnReturn;
        }

        public override bool CanBecomeFirstResponder => true;

        public void SetInputType(
            TextInputType inputType,
            bool multiline)
        {
            this.SecureTextEntry = false;
            switch (inputType)
            {
                case TextInputType.Email:
                    this.KeyboardType = UIKeyboardType.EmailAddress;
                    break;
                case TextInputType.Url:
                    this.KeyboardType = UIKeyboardType.Url;
                    break;
                case TextInputType.Search:
                    this.KeyboardType = UIKeyboardType.WebSearch;
                    break;
                case TextInputType.Password:
                    this.KeyboardType = UIKeyboardType.Default;
                    this.SecureTextEntry = true;
                    break;
                case TextInputType.Number:
                    this.KeyboardType = UIKeyboardType.NumberPad;
                    break;
                case TextInputType.Decimal:
                    this.KeyboardType = UIKeyboardType.DecimalPad;
                    break;
                case TextInputType.Phone:
                    this.KeyboardType = UIKeyboardType.PhonePad;
                    break;
                default:
                    this.KeyboardType = UIKeyboardType.Default;
                    break;
            }
            this.AutocapitalizationType = UITextAutocapitalizationType.None;
            this.AutocorrectionType = UITextAutocorrectionType.No;
        }

        private bool OnChangeCharacters(
            UITextField textField,
            NSRange range,
            string replacementString)
        {
            var currentText = textField.Text ?? string.Empty;
            if ((long)(range.Location + range.Length) > currentText.Length)
            {
                return false;
            }

            var newLength = currentText.Length + replacementString.Length - (int)range.Length;
            if ((this._handle.MaxLength >= 0) && (newLength > this._handle.MaxLength))
            {
                return false;
            }

            foreach (var codePoint in replacementString.EnumerateCodePoints())
            {
                if (!IsValidCodePoint(this._handle.InputType, codePoint, this._handle.Multiline))
                {
                    return false;
                }
            }
            return true;
        }

        private void OnEditingChanged(
            object? sender,
            EventArgs e)
        {
            this._handle.Sink.OnTextChanged(this.Text ?? string.Empty);
        }

        private void OnEditingDidBegin(
            object? sender,
            EventArgs e)
        {
            this._handle.Sink.OnGotFocus();
        }

        private bool OnReturn(
            UITextField textField)
        {
            this._handle.Sink.OnSubmit();
            return false;
        }

        private void OnEditingDidEnd(
            object? sender,
            EventArgs e)
        {
            this._handle.Sink.OnLostFocus();

            // Give a hardware keyboard back to the game view
            this.Window?.RootViewController?.BecomeFirstResponder();
        }
    }
}
