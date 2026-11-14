// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DotGame.Framework.Input;
using DotGame.Framework.Platform.Common;
using DotGame.Framework.Platform.Common.Input;
using DotGame.Framework.Platform.Input;

namespace DotGame.Framework.Desktop.Input;

internal sealed class InputServiceStrategy : IInputServiceStrategy
{
    private readonly GameHost _host;
    private readonly nint _window;
    private PointerButton _mouseHeldButtons;

    // Keep references alive for the lifetime of this service so the GC
    // does not collect the delegate that the OS holds via SetWindowLongPtr.
    private WndProcDelegate? _wndProcDelegate;
    private nint _wndProcDelegatePtr;
    private nint _originalWndProc;
    private nint _subclassedHwnd;

    // SDL only allows one active text-input session per window. Track
    // which TextInputHandle (if any) currently owns it.
    private TextInputHandle? _focusedHandle;

    internal InputServiceStrategy(GameHost host, nint window)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentOutOfRangeException.ThrowIfZero(window);

        this._host = host;
        this._window = window;

        if (OperatingSystem.IsWindows())
        {
            this.InstallWindowsTabletGestureFilter(window);
        }
    }

    // ---------------- Windows tablet/touch gesture filter ----------------

    private const int GWLP_WNDPROC = -4;
    private const uint WM_TABLET_QUERYSYSTEMGESTURESTATUS = 0x02CC;

    private const int TabletDisableFlags =
        0x00000001 | // TABLET_DISABLE_PRESSANDHOLD
        0x00000008 | // TABLET_DISABLE_PENTAPFEEDBACK
        0x00000010 | // TABLET_DISABLE_PENBARRELFEEDBACK
        0x00000100 | // TABLET_DISABLE_TOUCHUIFORCEON
        0x00000200 | // TABLET_DISABLE_TOUCHUIFORCEOFF
        0x00008000 | // TABLET_DISABLE_TOUCHSWITCH
        0x00010000 | // TABLET_DISABLE_FLICKS
        0x00080000 | // TABLET_DISABLE_SMOOTHSCROLLING
        0x00100000;  // TABLET_DISABLE_FLICKFALLBACKKEYS

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private void InstallWindowsTabletGestureFilter(nint sdlWindow)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            uint props = SDL.GetWindowProperties(sdlWindow);
            if (props == 0)
            {
                return;
            }

            nint hwnd = SDL.GetPointerProperty(props, SDL.Props.WindowWin32HWNDPointer, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            this._wndProcDelegate = this.SubclassedWndProc;
            this._wndProcDelegatePtr = Marshal.GetFunctionPointerForDelegate(this._wndProcDelegate);
            this._subclassedHwnd = hwnd;

            this._originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, this._wndProcDelegatePtr);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SDL3] InstallWindowsTabletGestureFilter failed: {ex.Message}");
        }
    }

    private nint SubclassedWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TABLET_QUERYSYSTEMGESTURESTATUS)
        {
            return TabletDisableFlags;
        }

        return CallWindowProc(this._originalWndProc, hwnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hwnd, uint msg, nint wParam, nint lParam);

    public Task OnInitialize()
    {
        return Task.CompletedTask;
    }

    // ---------------- Text input strategy ----------------

    public ITextInputHandle CreateTextInputHandle(
        ITextInputSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return new TextInputHandle(this, sink);
    }

    private void RequestFocus(TextInputHandle handle)
    {
        var current = this._focusedHandle;
        if (ReferenceEquals(current, handle))
        {
            return;
        }
        if (current is not null)
        {
            current.NotifyLostFocus();
            SDL.StopTextInput(this._window);
        }
        this._focusedHandle = handle;
        this.SetTextInputArea(handle.Bounds);
        SDL.StartTextInput(this._window);
        handle.NotifyGotFocus();
    }

    private void RequestBlur(TextInputHandle handle)
    {
        if (!ReferenceEquals(this._focusedHandle, handle))
        {
            return;
        }
        this._focusedHandle = null;
        SDL.StopTextInput(this._window);
        handle.NotifyLostFocus();
    }

    private void HandleDisposed(TextInputHandle handle)
    {
        if (ReferenceEquals(this._focusedHandle, handle))
        {
            this._focusedHandle = null;
            SDL.StopTextInput(this._window);
        }
    }

    private sealed class TextInputHandle : ITextInputHandle
    {
        private readonly InputServiceStrategy _service;
        private readonly ITextInputSink _sink;
        private TextInputState _state;
        private string _value = string.Empty;
        private bool _isFocused;
        private bool _isDisposed;

        public TextInputHandle(InputServiceStrategy service, ITextInputSink sink)
        {
            this._service = service;
            this._sink = sink;
        }

        internal ITextInputSink Sink => this._sink;
        internal bool IsFocused => this._isFocused;
        internal TextInputType InputType => this._state.InputType;
        internal int MaxLength => this._state.MaxLength;
        internal bool Multiline => this._state.Multiline;
        internal string Value => this._value;

        internal Rectangle Bounds => this._state.Bounds;

        public void Update(in TextInputState state)
        {
            if (this._isDisposed)
            {
                return;
            }
            this._state = state;
            if (ReferenceEquals(this._service._focusedHandle, this))
            {
                this._service.SetTextInputArea(state.Bounds);
            }

            // Sync internal buffer to programmatic Text changes.
            if (this._value != (state.Text ?? string.Empty))
            {
                this._value = state.Text ?? string.Empty;
            }
        }

        public void Focus()
        {
            if (this._isDisposed)
            {
                return;
            }
            this._service.RequestFocus(this);
        }

        public void Blur()
        {
            if (this._isDisposed)
            {
                return;
            }
            this._service.RequestBlur(this);
        }

        public void Dispose()
        {
            if (this._isDisposed)
            {
                return;
            }
            this._isDisposed = true;
            this._service.HandleDisposed(this);
        }

        internal void NotifyGotFocus()
        {
            if (this._isFocused)
            {
                return;
            }
            this._isFocused = true;
            this._sink.OnGotFocus();
        }

        internal void NotifyLostFocus()
        {
            if (!this._isFocused)
            {
                return;
            }
            this._isFocused = false;
            this._sink.OnLostFocus();
        }

        internal void SetValue(string value)
        {
            this._value = value ?? string.Empty;
            this._sink.OnTextChanged(this._value);
        }

        internal void RaiseSubmit() => this._sink.OnSubmit();
        internal void RaiseCancel() => this._sink.OnCancel();
    }

    private readonly GamepadSlots _gamepadSlots = new();
    private readonly nint[] _gamepads = new nint[InputService.MaxGamepads];

    private static bool TryMapGamepadButton(
        SDL.GamepadButton button,
        out GamepadButton result)
    {
        result = button switch
        {
            SDL.GamepadButton.South => GamepadButton.South,
            SDL.GamepadButton.East => GamepadButton.East,
            SDL.GamepadButton.West => GamepadButton.West,
            SDL.GamepadButton.North => GamepadButton.North,
            SDL.GamepadButton.LeftShoulder => GamepadButton.LeftShoulder,
            SDL.GamepadButton.RightShoulder => GamepadButton.RightShoulder,
            SDL.GamepadButton.Back => GamepadButton.Back,
            SDL.GamepadButton.Start => GamepadButton.Start,
            SDL.GamepadButton.Guide => GamepadButton.Guide,
            SDL.GamepadButton.LeftStick => GamepadButton.LeftStick,
            SDL.GamepadButton.RightStick => GamepadButton.RightStick,
            SDL.GamepadButton.DPadUp => GamepadButton.DPadUp,
            SDL.GamepadButton.DPadDown => GamepadButton.DPadDown,
            SDL.GamepadButton.DPadLeft => GamepadButton.DPadLeft,
            SDL.GamepadButton.DPadRight => GamepadButton.DPadRight,
            _ => (GamepadButton)(-1),
        };

        return (int)result >= 0;
    }

    private static bool TryMapGamepadAxis(
        SDL.GamepadAxis axis,
        out GamepadAxis result)
    {
        result = axis switch
        {
            SDL.GamepadAxis.LeftX => GamepadAxis.LeftX,
            SDL.GamepadAxis.LeftY => GamepadAxis.LeftY,
            SDL.GamepadAxis.RightX => GamepadAxis.RightX,
            SDL.GamepadAxis.RightY => GamepadAxis.RightY,
            SDL.GamepadAxis.LeftTrigger => GamepadAxis.LeftTrigger,
            SDL.GamepadAxis.RightTrigger => GamepadAxis.RightTrigger,
            _ => (GamepadAxis)(-1),
        };

        return (int)result >= 0;
    }

    internal void ProcessEvent(
        ref SDL.Event ev)
    {
        switch ((SDL.EventType)ev.Type)
        {
            case SDL.EventType.GamepadAdded:
                int connectedIndex = this._gamepadSlots.Connect(ev.GDevice.Which);
                if (connectedIndex >= 0)
                {
                    this._gamepads[connectedIndex] = SDL.OpenGamepad(ev.GDevice.Which);
                    this._host.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Connected, connectedIndex));
                }
                break;

            case SDL.EventType.GamepadRemoved:
                int disconnectedIndex = this._gamepadSlots.Disconnect(ev.GDevice.Which);
                if (disconnectedIndex >= 0)
                {
                    SDL.CloseGamepad(this._gamepads[disconnectedIndex]);
                    this._gamepads[disconnectedIndex] = nint.Zero;
                    this._host.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Disconnected, disconnectedIndex));
                }
                break;

            case SDL.EventType.GamepadButtonDown:
            case SDL.EventType.GamepadButtonUp:
                int buttonIndex = this._gamepadSlots.IndexOf(ev.GButton.Which);
                if ((buttonIndex >= 0) && TryMapGamepadButton((SDL.GamepadButton)ev.GButton.Button, out var gamepadButton))
                {
                    var buttonKind = ((SDL.EventType)ev.Type == SDL.EventType.GamepadButtonDown)
                        ? GamepadEventKind.ButtonPressed
                        : GamepadEventKind.ButtonReleased;
                    this._host.QueueGamepadEvent(new GamepadEvent(buttonKind, buttonIndex, gamepadButton));
                }
                break;

            case SDL.EventType.GamepadAxisMotion:
                int axisIndex = this._gamepadSlots.IndexOf(ev.GAxis.Which);
                if ((axisIndex >= 0) && TryMapGamepadAxis((SDL.GamepadAxis)ev.GAxis.Axis, out var gamepadAxis))
                {
                    // Sticks run from -32768 to 32767, and triggers from 0 to 32767
                    float axisValue = Math.Max(ev.GAxis.Value / 32767f, -1f);
                    this._host.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.AxisMoved, axisIndex, axis: gamepadAxis, value: axisValue));
                }
                break;

            case SDL.EventType.MouseButtonDown:
                if (IsSynthesizedMouseEvent(ev.Button.Which))
                {
                    break;
                }
                this._mouseHeldButtons |= MapMouseButton(ev.Button.Button);
                this._host.QueuePointerEvent(new PointerEventData
                {
                    AgeMs = EventAgeMs(ref ev),
                    State = PointerEventKind.Began,
                    PointerId = 0,
                    PointerType = (ev.Button.Which == SDL.PenMouseID) ? PointerDevice.Pen : PointerDevice.Mouse,
                    Position = new Vector2(ev.Button.X, ev.Button.Y),
                    Button = MapMouseButton(ev.Button.Button),
                    HeldButtons = this._mouseHeldButtons
                });
                break;

            case SDL.EventType.MouseButtonUp:
                if (IsSynthesizedMouseEvent(ev.Button.Which))
                {
                    break;
                }
                this._mouseHeldButtons &= ~MapMouseButton(ev.Button.Button);
                this._host.QueuePointerEvent(new PointerEventData
                {
                    AgeMs = EventAgeMs(ref ev),
                    State = PointerEventKind.Ended,
                    PointerId = 0,
                    PointerType = (ev.Button.Which == SDL.PenMouseID) ? PointerDevice.Pen : PointerDevice.Mouse,
                    Position = new Vector2(ev.Button.X, ev.Button.Y),
                    Button = MapMouseButton(ev.Button.Button),
                    HeldButtons = this._mouseHeldButtons
                });
                break;

            case SDL.EventType.MouseMotion:
                if (IsSynthesizedMouseEvent(ev.Motion.Which))
                {
                    break;
                }
                this._mouseHeldButtons = MapMouseButtonMask(ev.Motion.State);
                this._host.QueuePointerEvent(new PointerEventData
                {
                    AgeMs = EventAgeMs(ref ev),
                    State = PointerEventKind.Moved,
                    PointerId = 0,
                    PointerType = (ev.Motion.Which == SDL.PenMouseID) ? PointerDevice.Pen : PointerDevice.Mouse,
                    Position = new Vector2(ev.Motion.X, ev.Motion.Y),
                    HeldButtons = this._mouseHeldButtons
                });
                break;

            case SDL.EventType.MouseWheel:
                if (IsSynthesizedMouseEvent(ev.Wheel.Which))
                {
                    break;
                }
                this._host.QueuePointerEvent(new PointerEventData
                {
                    AgeMs = EventAgeMs(ref ev),
                    State = PointerEventKind.Wheel,
                    PointerId = 0,
                    PointerType = (ev.Wheel.Which == SDL.PenMouseID) ? PointerDevice.Pen : PointerDevice.Mouse,
                    Position = new Vector2(ev.Wheel.MouseX, ev.Wheel.MouseY),
                    HeldButtons = this._mouseHeldButtons,
                    // SDL reports positive Y for wheel-up; the engine follows the
                    // DOM convention where positive Y scrolls content down.
                    ScrollWheelDelta = new Vector2(ev.Wheel.X, -ev.Wheel.Y)
                });
                break;

            case SDL.EventType.FingerDown:
                if (IsSynthesizedTouchEvent(ev.TFinger.TouchID))
                {
                    break;
                }
                SDL.GetWindowSize(this._window, out int fdW, out int fdH);
                this._host.QueuePointerEvent(new PointerEventData
                {
                    AgeMs = EventAgeMs(ref ev),
                    State = PointerEventKind.Began,
                    PointerId = (int)ev.TFinger.FingerID,
                    PointerType = PointerDevice.Touch,
                    Position = new Vector2(ev.TFinger.X * fdW, ev.TFinger.Y * fdH)
                });
                break;

            // SDL reports a touch the system took away (the window lost focus) as canceled: a
            // cancel, not a release, so nothing acts on it
            case SDL.EventType.FingerUp:
            case SDL.EventType.FingerCanceled:
                if (IsSynthesizedTouchEvent(ev.TFinger.TouchID))
                {
                    break;
                }
                SDL.GetWindowSize(this._window, out int fuW, out int fuH);
                this._host.QueuePointerEvent(new PointerEventData
                {
                    AgeMs = EventAgeMs(ref ev),
                    State = ((SDL.EventType)ev.Type == SDL.EventType.FingerUp) ? PointerEventKind.Ended : PointerEventKind.Canceled,
                    PointerId = (int)ev.TFinger.FingerID,
                    PointerType = PointerDevice.Touch,
                    Position = new Vector2(ev.TFinger.X * fuW, ev.TFinger.Y * fuH)
                });
                break;

            case SDL.EventType.FingerMotion:
                if (IsSynthesizedTouchEvent(ev.TFinger.TouchID))
                {
                    break;
                }
                SDL.GetWindowSize(this._window, out int fmW, out int fmH);
                this._host.QueuePointerEvent(new PointerEventData
                {
                    AgeMs = EventAgeMs(ref ev),
                    State = PointerEventKind.Moved,
                    PointerId = (int)ev.TFinger.FingerID,
                    PointerType = PointerDevice.Touch,
                    Position = new Vector2(ev.TFinger.X * fmW, ev.TFinger.Y * fmH)
                });
                break;

            case SDL.EventType.KeyDown:
                if (!this.HandleTextInputKey(ev.Key.Scancode) && !this.IsTypingKey(ref ev))
                {
                    this._host.QueueKeyEvent(new KeyEventData
                    {
                        State = KeyEventKind.Pressed,
                        Key = KeyMap.FromScancode(ev.Key.Scancode),
                        Modifiers = KeyMap.FromKeymod(ev.Key.Mod),
                        IsRepeat = ev.Key.Repeat,
                        VirtualKey = (int)ev.Key.Key
                    });
                }
                break;

            case SDL.EventType.KeyUp:
                if (!this.IsTextInputKey(ev.Key.Scancode) && !this.IsTypingKey(ref ev))
                {
                    this._host.QueueKeyEvent(new KeyEventData
                    {
                        State = KeyEventKind.Released,
                        Key = KeyMap.FromScancode(ev.Key.Scancode),
                        Modifiers = KeyMap.FromKeymod(ev.Key.Mod),
                        VirtualKey = (int)ev.Key.Key
                    });
                }
                break;

            case SDL.EventType.TextEditing:
                this._focusedHandle?.Sink.OnCompositionChanged(Marshal.PtrToStringUTF8(ev.Edit.Text) ?? string.Empty);
                break;

            case SDL.EventType.TextInput:
                {
                    var handle = this._focusedHandle;
                    if (handle is null)
                    {
                        break;
                    }
                    string? text = Marshal.PtrToStringUTF8(ev.Text.Text);
                    if (string.IsNullOrEmpty(text))
                    {
                        break;
                    }
                    var appended = AppendFiltered(handle, text);
                    if (appended != handle.Value)
                    {
                        handle.SetValue(appended);
                    }
                }
                break;
        }
    }

    // A key that produces text while a text field has focus: it's for the field, not a hotkey
    private bool IsTypingKey(
        ref SDL.Event ev)
    {
        if (this._focusedHandle is null)
        {
            return false;
        }

        const SDL.Keymod NonTypingModifiers = SDL.Keymod.Ctrl | SDL.Keymod.GUI;

        uint key = (uint)ev.Key.Key;
        return ((ev.Key.Mod & NonTypingModifiers) == 0)
            && (key >= 0x20) && (key < 0x40000000) && (key != 0x7F);
    }

    /// <summary>
    /// Intercepts Backspace / Enter / Escape while a text-input session
    /// is active so they edit the buffer instead of (or in addition to)
    /// being delivered as gameplay key events.
    /// </summary>
    private bool HandleTextInputKey(
        SDL.Scancode scancode)
    {
        var handle = this._focusedHandle;
        if (handle is null)
        {
            return false;
        }

        switch (scancode)
        {
            case SDL.Scancode.Backspace:
                if (handle.Value.Length > 0)
                {
                    var current = handle.Value;
                    Rune.DecodeLastFromUtf16(current, out _, out int charsConsumed);
                    handle.SetValue(current.Substring(0, current.Length - charsConsumed));
                }
                return true;

            case SDL.Scancode.Return:
            case SDL.Scancode.KpEnter:
                if (handle.Multiline)
                {
                    if ((handle.MaxLength < 0) || (handle.Value.Length < handle.MaxLength))
                    {
                        handle.SetValue(handle.Value + '\n');
                    }
                }
                else
                {
                    handle.RaiseSubmit();
                }
                return true;

            case SDL.Scancode.Escape:
                handle.RaiseCancel();
                return true;
        }

        return false;
    }

    private bool IsTextInputKey(
        SDL.Scancode scancode)
    {
        if (this._focusedHandle is null)
        {
            return false;
        }

        return scancode is
            SDL.Scancode.Backspace or
            SDL.Scancode.Return or
            SDL.Scancode.KpEnter or
            SDL.Scancode.Escape;
    }

    private static string AppendFiltered(
        TextInputHandle handle,
        string text)
    {
        var current = handle.Value;
        foreach (var codePoint in text.EnumerateCodePoints())
        {
            if (!TextInputCodePoints.IsValid(handle.InputType, codePoint, handle.Multiline))
            {
                continue;
            }
            if ((handle.MaxLength >= 0) && (current.Length >= handle.MaxLength))
            {
                break;
            }
            current += codePoint.GetUtf8String();
        }
        return current;
    }

    private static PointerButton MapMouseButton(
        byte button)
    {
        return button switch
        {
            1 => PointerButton.Left,
            2 => PointerButton.Middle,
            3 => PointerButton.Right,
            4 => PointerButton.X1,
            5 => PointerButton.X2,
            _ => PointerButton.None,
        };
    }

    // NOTE: Events are polled once a frame, so the time they were polled says nothing
    private static long EventAgeMs(
        ref SDL.Event ev)
    {
        return (long)(SDL.GetTicksNS() - ev.Common.Timestamp) / 1_000_000;
    }

    private void SetTextInputArea(
        Rectangle bounds)
    {
        var area = new SDL.Rect { X = (int)bounds.X, Y = (int)bounds.Y, W = (int)bounds.Width, H = (int)bounds.Height };
        SDL.SetTextInputArea(this._window, in area, 0);
    }

    private static PointerButton MapMouseButtonMask(SDL.MouseButtonFlags sdlMask)
    {
        uint mask = (uint)sdlMask;
        var result = PointerButton.None;
        if ((mask & (1u << 0)) != 0)
        {
            result |= PointerButton.Left;
        }
        if ((mask & (1u << 1)) != 0)
        {
            result |= PointerButton.Middle;
        }
        if ((mask & (1u << 2)) != 0)
        {
            result |= PointerButton.Right;
        }
        if ((mask & (1u << 3)) != 0)
        {
            result |= PointerButton.X1;
        }
        if ((mask & (1u << 4)) != 0)
        {
            result |= PointerButton.X2;
        }
        return result;
    }

    private static bool IsSynthesizedMouseEvent(uint mouseId)
    {
        // NOTE: Not the pen's, which reaches the game this way, reported as a pen
        return mouseId == SDL.TouchMouseID;
    }

    private static bool IsSynthesizedTouchEvent(ulong touchId)
    {
        return (touchId == (ulong)SDL.MouseTouchID) || (touchId == (ulong)SDL.PenTouchID);
    }
}
