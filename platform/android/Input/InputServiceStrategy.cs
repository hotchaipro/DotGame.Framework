// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using DotGame.Framework.Input;
using DotGame.Framework.Platform.Common.Input;
using DotGame.Framework.Platform.Input;
using AndroidKeyEvent = Android.Views.KeyEvent;

namespace DotGame.Framework.Android.Input;

internal sealed class InputServiceStrategy : IInputServiceStrategy
{
    private readonly GameHost _host;
    private readonly Activity _activity;
    private readonly ViewGroup _container;
    private InputMethodManager? _imm;

    internal InputServiceStrategy(
        GameHost host,
        Activity activity,
        ViewGroup container)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(container);

        this._host = host;
        this._activity = activity;
        this._container = container;
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

        if (this._imm is null)
        {
            this._imm = (InputMethodManager?)this._activity.GetSystemService(Activity.InputMethodService);
        }

        return new TextInputHandle(this, sink);
    }

    private static bool IsValidCodePoint(
        TextInputType inputType,
        int codePoint,
        bool multiline)
    {
        return TextInputCodePoints.IsValid(inputType, codePoint, multiline);
    }

    private sealed class TextInputHandle : ITextInputHandle
    {
        private readonly InputServiceStrategy _service;
        private readonly ITextInputSink _sink;
        private NativeTextBox? _box;
        private TextInputState _state;
        private bool _hasState;
        private bool _isDisposed;
        private bool _suppressTextChanged;

        public TextInputHandle(
            InputServiceStrategy service,
            ITextInputSink sink)
        {
            this._service = service;
            this._sink = sink;

            service._activity.RunOnUiThread(() =>
            {
                if (this._isDisposed)
                {
                    return;
                }
                var box = new NativeTextBox(this, service._activity);
                var lp = new FrameLayout.MarginLayoutParams(
                    FrameLayout.LayoutParams.MatchParent,
                    FrameLayout.LayoutParams.WrapContent);
                lp.SetMargins(0, -200, 0, 0);
                box.TranslationY = -200;
                box.Visibility = ViewStates.Gone;
                service._container.AddView(box, lp);
                this._box = box;
            });
        }

        internal ITextInputSink Sink => this._sink;
        internal int MaxLength => this._state.MaxLength;
        internal TextInputType InputType => this._state.InputType;
        internal bool Multiline => this._state.Multiline;

        public void Update(in TextInputState state)
        {
            if (this._isDisposed)
            {
                return;
            }

            var prev = this._state;
            var hadState = this._hasState;
            var captured = state;
            this._state = state;
            this._hasState = true;

            this._service._activity.RunOnUiThread(() =>
            {
                var box = this._box;
                if ((box is null) || (this._isDisposed))
                {
                    return;
                }

                if ((!hadState) ||
                    (prev.InputType != captured.InputType) ||
                    (prev.Multiline != captured.Multiline))
                {
                    box.SetInputType(captured.InputType, captured.Multiline);
                }

                if (box.Text != captured.Text)
                {
                    this._suppressTextChanged = true;
                    try
                    {
                        box.SetText(captured.Text ?? string.Empty, TextView.BufferType.Editable);
                        if (box.Text != null)
                        {
                            box.SetSelection(box.Text.Length);
                        }
                    }
                    finally
                    {
                        this._suppressTextChanged = false;
                    }
                }
            });
        }

        public void Focus()
        {
            if (this._isDisposed)
            {
                return;
            }
            this._service._activity.RunOnUiThread(() =>
            {
                var box = this._box;
                if ((box is null) || (this._isDisposed))
                {
                    return;
                }
                box.Visibility = ViewStates.Visible;
                box.RequestFocus();
                this._service._imm?.ShowSoftInput(box, ShowFlags.Implicit);
            });
        }

        public void Blur()
        {
            if (this._isDisposed)
            {
                return;
            }
            this._service._activity.RunOnUiThread(() =>
            {
                var box = this._box;
                if (box is null)
                {
                    return;
                }
                this._service._imm?.HideSoftInputFromWindow(box.WindowToken, HideSoftInputFlags.None);
                box.ClearFocus();
                box.Visibility = ViewStates.Gone;
            });
        }

        public void Dispose()
        {
            if (this._isDisposed)
            {
                return;
            }
            this._isDisposed = true;
            this._service._activity.RunOnUiThread(() =>
            {
                var box = this._box;
                this._box = null;
                if (box is null)
                {
                    return;
                }
                this._service._imm?.HideSoftInputFromWindow(box.WindowToken, HideSoftInputFlags.None);
                this._service._container.RemoveView(box);
                box.Dispose();
            });
        }

        internal bool SuppressTextChanged => this._suppressTextChanged;

        internal bool IsValid(int codePoint)
            => IsValidCodePoint(this._state.InputType, codePoint, this._state.Multiline);
    }

    private sealed class NativeTextBox : EditText
    {
        private const ImeAction IME_FLAG_NO_EXTRACT_UI = (ImeAction)0x10000000;
        private readonly TextInputHandle _handle;

        public NativeTextBox(TextInputHandle handle, Context context)
            : base(context)
        {
            this._handle = handle;
            this.SetFilters(new[] { new NativeInputFilter(this) });
            this.SetMaxLines(1);
            this.ImeOptions = ImeAction.Done | IME_FLAG_NO_EXTRACT_UI;
        }

        public void SetInputType(
            TextInputType inputType,
            bool multiline)
        {
            InputTypes baseType;
            switch (inputType)
            {
                case TextInputType.Email:
                    baseType = InputTypes.ClassText | InputTypes.TextVariationWebEmailAddress;
                    break;
                case TextInputType.Url:
                    baseType = InputTypes.ClassText | InputTypes.TextVariationUri;
                    break;
                case TextInputType.Search:
                    baseType = InputTypes.ClassText;
                    break;
                case TextInputType.Password:
                    baseType = InputTypes.ClassText | InputTypes.TextVariationPassword;
                    break;
                case TextInputType.Number:
                    baseType = InputTypes.ClassNumber | InputTypes.NumberVariationNormal;
                    break;
                case TextInputType.Decimal:
                    baseType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned;
                    break;
                case TextInputType.Phone:
                    baseType = InputTypes.ClassPhone;
                    break;
                default:
                    baseType = InputTypes.ClassText;
                    break;
            }

            if (multiline && (baseType & InputTypes.MaskClass) == InputTypes.ClassText)
            {
                baseType |= InputTypes.TextFlagMultiLine;
                this.SetMaxLines(int.MaxValue);
                this.ImeOptions = IME_FLAG_NO_EXTRACT_UI;
            }
            else
            {
                this.SetMaxLines(1);
                this.ImeOptions = ImeAction.Done | IME_FLAG_NO_EXTRACT_UI;
            }

            this.InputType = baseType | InputTypes.TextFlagNoSuggestions;
        }

        protected override void OnFocusChanged(
            bool gainFocus,
            FocusSearchDirection direction,
            Rect? previouslyFocusedRect)
        {
            if (gainFocus)
            {
                this._handle.Sink.OnGotFocus();
            }
            else
            {
                this._handle.Sink.OnLostFocus();
            }
            base.OnFocusChanged(gainFocus, direction, previouslyFocusedRect);
        }

        protected override void OnTextChanged(
            Java.Lang.ICharSequence? text,
            int start,
            int lengthBefore,
            int lengthAfter)
        {
            // The base EditText constructor invokes this virtual (via its
            // internal setText) BEFORE our constructor body assigns _handle,
            // so _handle can still be null on the very first callback. Ignore
            // it until the handle is wired up.
            var handle = this._handle;
            if ((handle is not null) && (!handle.SuppressTextChanged))
            {
                handle.Sink.OnTextChanged(this.Text ?? string.Empty);
            }
            base.OnTextChanged(text, start, lengthBefore, lengthAfter);
        }

        public override bool OnKeyPreIme(
            Keycode keyCode,
            AndroidKeyEvent? e)
        {
            if (e?.KeyCode == Keycode.Back)
            {
                this._handle.Sink.OnCancel();
            }
            return base.OnKeyPreIme(keyCode, e);
        }

        public override void OnEditorAction(
            ImeAction actionCode)
        {
            this._handle.Sink.OnSubmit();
            base.OnEditorAction(actionCode);
        }

        internal bool IsValidCodePoint(int codePoint)
            => this._handle.IsValid(codePoint);

        internal int HandleMaxLength => this._handle.MaxLength;

        private sealed class NativeInputFilter : Java.Lang.Object, IInputFilter
        {
            private static readonly Java.Lang.String EmptyString = new Java.Lang.String(string.Empty);
            private readonly NativeTextBox _parent;

            internal NativeInputFilter(
                NativeTextBox parent)
            {
                this._parent = parent;
            }

            public Java.Lang.ICharSequence? FilterFormatted(
                Java.Lang.ICharSequence? source,
                int start,
                int end,
                ISpanned? dest,
                int dstart,
                int dend)
            {
                if (source is null || dest is null)
                {
                    return null;
                }

                Java.Lang.ICharSequence? filtered = null;
                int filteredEnd = end;
                Predicate<int> predicate = this._parent.IsValidCodePoint;

                if (source is SpannableStringBuilder ssb)
                {
                    for (int i = end - 1; i >= start; i--)
                    {
                        if (!predicate(source.CharAt(i)))
                        {
                            filtered ??= ssb;
                            ssb.Delete(i, i + 1);
                            filteredEnd -= 1;
                        }
                    }
                }
                else
                {
                    SpannableStringBuilder? builder = null;
                    for (int i = end - 1; i >= start; i--)
                    {
                        if (!predicate(source.CharAt(i)))
                        {
                            if (builder is null)
                            {
                                builder = new SpannableStringBuilder(source);
                                filtered = builder;
                            }
                            builder.Delete(i, i + 1);
                            filteredEnd -= 1;
                        }
                    }
                }

                int maxLength = this._parent.HandleMaxLength;
                if (maxLength >= 0)
                {
                    int newLength = dest.Length() - (dend - dstart) + (filteredEnd - start);
                    if (newLength > maxLength)
                    {
                        int truncate = newLength - maxLength;
                        filteredEnd -= truncate;
                        if (filteredEnd > start)
                        {
                            filtered = filtered is null
                                ? source.SubSequenceFormatted(start, filteredEnd)
                                : filtered.SubSequenceFormatted(start, filteredEnd);
                        }
                        else
                        {
                            filtered = EmptyString;
                        }
                    }
                }

                return filtered;
            }
        }
    }
}
