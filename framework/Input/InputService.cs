// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Input;

namespace DotGame.Framework.Input;

/// <summary>Reads the keyboard and gamepads, and manages text input. Reach it as <c>Game.Input</c>. Pointers arrive as events on <c>Game</c>.</summary>
public sealed class InputService
{
    private bool _isInitialized;

    // Pointer state, guarded by _bufferLock: platforms queue events from their own threads.
    // The touches down, in the order they began.
    private readonly List<PointerEvent> _multiTouchState = new();
    // The mouse and pen pointers with a button held, by their latest event.
    private readonly List<PointerEvent> _pressedPointers = new();
    // The pointers whose press CancelPressedPointers ended. The platform may still deliver
    // the rest of that press (a release once the window is back); it's dropped, since the
    // game already saw the press canceled.
    private readonly List<PointerEvent> _canceledPointers = new();
    private List<PointerEvent> _eventQueueBuffer;
    private List<PointerEvent> _eventDequeueBuffer;
    private readonly Lock _bufferLock = new();

    // Keyboard state
    private List<KeyEvent> _keyQueueBuffer;
    private List<KeyEvent> _keyDequeueBuffer;

    // Committed keyboard state
    // Updated by DequeueKeyEvents() by replaying the dequeued events, so no
    // lock or allocation is required in GetKeyboardState().
    private ulong _committedKeysDownLow;
    private ulong _committedKeysDownHigh;

    // Keys pressed and not released among the events queued so far. Guarded by _bufferLock.
    private readonly bool[] _queuedKeysDown = new bool[KeyInfo.Count];
    private ModifierKeys _committedModifiers;

    /// <summary>
    /// The gamepads the game can tell apart.
    /// </summary>
    public const int MaxGamepads = 8;

    private List<GamepadEvent> _gamepadQueueBuffer = new();
    private List<GamepadEvent> _gamepadDequeueBuffer = new();

    // As they are among the events queued so far, to release on focus loss. Guarded by _bufferLock.
    private readonly uint[] _queuedGamepadButtons = new uint[MaxGamepads];
    private readonly float[][] _queuedGamepadAxes = NewAxes();

    // As the game last saw them: only the game thread touches these
    private readonly bool[] _committedGamepadConnected = new bool[MaxGamepads];
    private readonly uint[] _committedGamepadButtons = new uint[MaxGamepads];
    private readonly float[][] _committedGamepadAxes = NewAxes();

    private static float[][] NewAxes()
    {
        var axes = new float[MaxGamepads][];
        for (int i = 0; i < axes.Length; i++)
        {
            axes[i] = new float[GamepadState.AxisCount];
        }

        return axes;
    }

    internal InputService(
        Strategy<IInputServiceStrategy> inputServiceStrategy)
    {
        ArgumentNullException.ThrowIfNull(inputServiceStrategy);

        // A key added to the enum without updating the count would never be down
        if (FeatureSwitches.IsDebug && (DiagnosticInfo.EnumValueCount<Key>() != KeyInfo.Count))
        {
            throw new InvalidOperationException(
                $"Key defines {DiagnosticInfo.EnumValueCount<Key>()} keys but KeyInfo.Count is {KeyInfo.Count}; update the count in Key.cs.").WithTag(0x5c2e8f17);
        }

        this._eventQueueBuffer = new();
        this._eventDequeueBuffer = new();
        this._keyQueueBuffer = new();
        this._keyDequeueBuffer = new();
        this.Strategy = inputServiceStrategy;
    }

    private readonly Strategy<IInputServiceStrategy> Strategy;

    /// <summary>
    /// Cancels every press in progress, when the game loses focus: queues a
    /// <see cref="PointerEventKind.Canceled"/> event for each touch down and each mouse or pen
    /// pointer with a button held, so controls and drags end without acting. A platform may
    /// never report the end of a press it took away (the window lost focus mid-press, the app
    /// was paused with a finger down), which otherwise leaves it held forever. The rest of a
    /// canceled press, if the platform does deliver it, is dropped; a canceled mouse still
    /// moves, without buttons held, so it hovers. Keys held down are released, as a browser
    /// delivers no keyup once its window has lost focus.
    /// </summary>
    internal void CancelPressedPointers()
    {
        lock (this._bufferLock)
        {
            // NOTE: Each touch's cancel reports the touches still down after it, like an
            // Ended, so the last one reports TouchCount == 0
            while (this._multiTouchState.Count > 0)
            {
                int last = this._multiTouchState.Count - 1;
                var touch = this._multiTouchState[last];
                this._multiTouchState.RemoveAt(last);
                this.QueueCanceledPointer(touch, touch.Button, last);
            }

            foreach (var pressed in this._pressedPointers)
            {
                this.QueueCanceledPointer(pressed, pressed.HeldButtons, 0);
            }

            this._pressedPointers.Clear();

            for (int pad = 0; pad < MaxGamepads; pad++)
            {
                this.QueueGamepadRelease(pad);
            }

            for (int key = 0; key < this._queuedKeysDown.Length; key++)
            {
                if (this._queuedKeysDown[key])
                {
                    this._queuedKeysDown[key] = false;
                    this._keyQueueBuffer.Add(new KeyEvent(KeyEventKind.Released, (Key)key, ModifierKeys.None, false, 0));
                }
            }
        }
    }

    // Queues the cancel of a pointer's press and remembers it, so the rest of the press is
    // dropped. The caller holds _bufferLock.
    private void QueueCanceledPointer(
        PointerEvent pressed,
        PointerButton pointerButton,
        int touchIndex)
    {
        var canceled = new PointerEvent(
            PointerEventKind.Canceled,
            pressed.Id,
            pressed.Position,
            pressed.Device,
            button: pointerButton,
            heldButtons: PointerButton.None,
            scrollWheelDelta: default,
            touchCount: this._multiTouchState.Count,
            touchIndex: touchIndex);

        if (FeatureSwitches.TraceInput)
        {
            Trace.Write(0xcdf45da3, $"[Input] Cancel id={pressed.Id} type={pressed.Device} pos=({pressed.Position.X:0},{pressed.Position.Y:0}) btn={pointerButton}");
        }

        this._eventQueueBuffer.Add(canceled);

        if (IndexOfPointer(this._canceledPointers, pressed.Id, pressed.Device) < 0)
        {
            this._canceledPointers.Add(canceled);
        }
    }

    private static int IndexOfPointer(
        List<PointerEvent> pointers,
        int pointerId,
        PointerDevice pointerType)
    {
        for (int i = 0; i < pointers.Count; i += 1)
        {
            if ((pointers[i].Id == pointerId)
                && (pointers[i].Device == pointerType))
            {
                return i;
            }
        }

        return -1;
    }

    internal async Task Initialize()
    {
        if (this._isInitialized)
        {
            return;
        }

        this._isInitialized = true;

        await this.Strategy.Interface.OnInitialize();
    }

    /// <summary>
    /// Atomically dequeues all pointer events that have been queued since the
    /// last call.
    /// </summary>
    /// <remarks>
    /// The returned list is reused internally and must not be referenced
    /// beyond the current frame.
    /// </remarks>
    /// <returns>A list of <c>PointerEventArgs</c> objects.</returns>
    internal List<PointerEvent> DequeuePointerEvents()
    {
        // Prepare the dequeue buffer to be reused as the new enqueue buffer
        this._eventDequeueBuffer.Clear();

        // Swap the enqueue buffer with the cleared dequeue buffer
        lock (this._bufferLock)
        {
            (this._eventDequeueBuffer, this._eventQueueBuffer) = (this._eventQueueBuffer, this._eventDequeueBuffer);
        }

        return this._eventDequeueBuffer;
    }

    /// <summary>
    /// Called to queue a pointer event.
    /// </summary>
    internal void QueuePointerEvent(
        PointerEventKind state,
        int pointerId,
        PointerDevice pointerType,
        float clientX,
        float clientY,
        PointerButton pointerButton = PointerButton.None,
        PointerButton heldButtons = PointerButton.None,
        float scrollWheelDeltaX = 0,
        float scrollWheelDeltaY = 0,
        long ageMs = 0)
    {
        long timestampMs = PointerEvent.NowMs() - ageMs;

        // Create a Vector2 for the viewport position
        var position = new Vector2((float)clientX, (float)clientY);

        // Create scroll wheel delta
        var scrollWheelDelta = new Vector2(scrollWheelDeltaX, scrollWheelDeltaY);

        lock (this._bufferLock)
        {
            if (!this.FilterCanceledPointer(state, pointerId, pointerType, ref heldButtons))
            {
                return;
            }

            // Track multi-touch.
            //
            // The multi-touch set must be mutated BEFORE reading touchCount,
            // so that touchCount on every event reflects the post-mutation
            // membership. In particular, the Ended/Canceled event for the
            // last finger must report TouchCount == 0 — consumers like
            // TapSequenceGestureRecognizer key on that to commit a gesture.
            // (Previously the removal happened after event construction, so
            // the final Ended always reported TouchCount >= 1 and the
            // terminating signal was never observed.)
            int touchCount;
            int touchIndex;
            if (pointerType != PointerDevice.Touch)
            {
                touchCount = 0;
                touchIndex = 0;
            }
            else
            {
                int existingIndex = IndexOfPointer(this._multiTouchState, pointerId, pointerType);
                if (state == PointerEventKind.Began)
                {
                    if (existingIndex >= 0)
                    {
                        // Duplicate Began for an already-tracked pointer: keep
                        // the original slot rather than double-adding.
                        touchIndex = existingIndex;
                        touchCount = this._multiTouchState.Count;
                    }
                    else
                    {
                        touchIndex = this._multiTouchState.Count;
                        touchCount = this._multiTouchState.Count + 1;
                    }
                }
                else if ((state == PointerEventKind.Ended)
                    || (state == PointerEventKind.Canceled))
                {
                    if (existingIndex >= 0)
                    {
                        this._multiTouchState.RemoveAt(existingIndex);
                    }
                    touchIndex = (existingIndex >= 0) ? existingIndex : 0;
                    touchCount = this._multiTouchState.Count;
                }
                else
                {
                    // Moved (or any other future state): report current
                    // membership; touchIndex defaults to 0 if the pointer
                    // is unknown (e.g. an out-of-band Move with no Began).
                    touchIndex = (existingIndex >= 0) ? existingIndex : 0;
                    touchCount = this._multiTouchState.Count;
                }
            }

            // Create the PointerEvent using the existing constructor
            var pointerEvent = new PointerEvent(
                state,
                pointerId,
                position,
                pointerType,
                button: pointerButton,
                heldButtons: heldButtons,
                scrollWheelDelta: scrollWheelDelta,
                touchCount: touchCount,
                touchIndex: touchIndex,
                timestamp: TimeSpan.FromMilliseconds(timestampMs));

            // Commit the new touch into the multi-touch set AFTER the event
            // is constructed (so this Began carries the post-add count) but
            // BEFORE it is enqueued. Removal for Ended/Canceled already
            // happened above so that those events also report the
            // post-mutation count.
            //
            // NOTE: do NOT gate on (pointerId >= 0) here. Browser pointer
            // IDs are spec'd as arbitrary integers; iOS Chrome (CriOS) and
            // iOS Safari WebKit emit negative values for touch pointers,
            // and gating on a non-negative id silently dropped every real
            // multi-touch finger from the membership set, causing every
            // Began/Ended to report TouchCount == 1/0 regardless of how
            // many fingers were physically down. Synthetic non-touch
            // pointers (e.g. the wheel pseudo-pointer with id == -1) are
            // already excluded by the PointerDevice.Touch check.
            if (pointerType == PointerDevice.Touch)
            {
                int trackedIndex = IndexOfPointer(this._multiTouchState, pointerId, pointerType);
                if ((state == PointerEventKind.Began) && (trackedIndex < 0))
                {
                    this._multiTouchState.Add(pointerEvent);
                }
                else if ((state == PointerEventKind.Moved) && (trackedIndex >= 0))
                {
                    // A cancel reports where the touch is, not where it began
                    this._multiTouchState[trackedIndex] = pointerEvent;
                }
            }
            else
            {
                this.TrackPressedPointer(pointerEvent);
            }

            // Hot-path breadcrumb (ring buffer only, no console mirror) so a
            // postmortem can correlate platform-layer events with downstream
            // recognizer state. Move events are deliberately summarized
            // rather than logged per event to avoid saturating the ring.
            if (state != PointerEventKind.Moved)
            {
                if (FeatureSwitches.TraceInput)
                {
                    Trace.Write(0xcdf45da2, $"[Input] Queue {state} id={pointerId} type={pointerType} touch={touchIndex}/{touchCount} pos=({clientX:0},{clientY:0}) btn={pointerButton}");
                }
            }

            // Add the event to the active event queue buffer
            this._eventQueueBuffer.Add(pointerEvent);
        }
    }

    // Applies a press canceled by CancelPressedPointers to an event for that pointer: returns
    // false to drop the event, the rest of the canceled press. A new press is the pointer's
    // own again; so is a mouse or pen once no button is held. A canceled mouse or pen keeps
    // moving, without the canceled press's buttons. The caller holds _bufferLock.
    private bool FilterCanceledPointer(
        PointerEventKind state,
        int pointerId,
        PointerDevice pointerType,
        ref PointerButton heldButtons)
    {
        if (this._canceledPointers.Count == 0)
        {
            return true;
        }

        int index = IndexOfPointer(this._canceledPointers, pointerId, pointerType);
        if (index < 0)
        {
            return true;
        }

        if (state == PointerEventKind.Began)
        {
            this._canceledPointers.RemoveAt(index);
            return true;
        }

        if (pointerType == PointerDevice.Touch)
        {
            if (state != PointerEventKind.Moved)
            {
                this._canceledPointers.RemoveAt(index);
            }

            return false;
        }

        if (heldButtons == PointerButton.None)
        {
            this._canceledPointers.RemoveAt(index);
        }

        heldButtons = PointerButton.None;
        return (state == PointerEventKind.Moved);
    }

    // Keeps the mouse and pen pointers with a button held, so CancelPressedPointers can cancel
    // them. The caller holds _bufferLock.
    private void TrackPressedPointer(
        in PointerEvent pointerEvent)
    {
        int index = IndexOfPointer(this._pressedPointers, pointerEvent.Id, pointerEvent.Device);
        switch (pointerEvent.Kind)
        {
            case PointerEventKind.Began:
                if (index >= 0)
                {
                    this._pressedPointers[index] = pointerEvent;
                }
                else
                {
                    this._pressedPointers.Add(pointerEvent);
                }
                break;

            case PointerEventKind.Moved:
                if (index >= 0)
                {
                    this._pressedPointers[index] = pointerEvent;
                }
                break;

            default:
                if (index >= 0)
                {
                    if (pointerEvent.HeldButtons == PointerButton.None)
                    {
                        this._pressedPointers.RemoveAt(index);
                    }
                    else
                    {
                        this._pressedPointers[index] = pointerEvent;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Atomically dequeues all keyboard events that have been queued since
    /// the last call.
    /// </summary>
    /// <remarks>
    /// The returned list is reused internally and must not be referenced
    /// beyond the current frame.
    /// </remarks>
    /// <returns>A list of <see cref="KeyEvent"/> objects.</returns>
    internal List<KeyEvent> DequeueKeyEvents()
    {
        // Prepare the dequeue buffer to be reused as the new enqueue buffer
        this._keyDequeueBuffer.Clear();

        // Swap the enqueue buffer with the cleared dequeue buffer
        lock (this._bufferLock)
        {
            (this._keyDequeueBuffer, this._keyQueueBuffer) = (this._keyQueueBuffer, this._keyDequeueBuffer);
        }

        // Replay dequeued events into the committed state.
        // Safe without a lock because committed state is only written here,
        // on the game thread, and _keyDequeueBuffer is no longer shared.
        foreach (var keyEvent in this._keyDequeueBuffer)
        {
            int index = (int)keyEvent.Key;
            if ((index >= 0) && (index < KeyInfo.Count))
            {
                ref ulong keysDown = ref ((index < 64) ? ref this._committedKeysDownLow : ref this._committedKeysDownHigh);
                ulong bit = 1UL << (index & 63);
                keysDown = (keyEvent.Kind == KeyEventKind.Pressed) ? (keysDown | bit) : (keysDown & ~bit);
            }

            this._committedModifiers = keyEvent.Modifiers;
        }

        return this._keyDequeueBuffer;
    }

    // Queues a release for everything a gamepad holds. The caller holds _bufferLock.
    private void QueueGamepadRelease(
        int index)
    {
        uint buttons = this._queuedGamepadButtons[index];
        for (int button = 0; buttons != 0; button++, buttons >>= 1)
        {
            if ((buttons & 1) != 0)
            {
                this._gamepadQueueBuffer.Add(new GamepadEvent(GamepadEventKind.ButtonReleased, index, (GamepadButton)button));
            }
        }

        this._queuedGamepadButtons[index] = 0;

        var axes = this._queuedGamepadAxes[index];
        for (int axis = 0; axis < axes.Length; axis++)
        {
            if (axes[axis] != 0)
            {
                axes[axis] = 0;
                this._gamepadQueueBuffer.Add(new GamepadEvent(GamepadEventKind.AxisMoved, index, axis: (GamepadAxis)axis));
            }
        }
    }

    /// <summary>
    /// Called by the platform to queue a gamepad event. Any thread may call it.
    /// </summary>
    internal void QueueGamepadEvent(
        in GamepadEvent gamepadEvent)
    {
        int index = gamepadEvent.Index;
        if ((index < 0) || (index >= MaxGamepads))
        {
            return;
        }

        lock (this._bufferLock)
        {
            switch (gamepadEvent.Kind)
            {
                case GamepadEventKind.Disconnected:
                    this.QueueGamepadRelease(index);
                    break;

                case GamepadEventKind.ButtonPressed:
                    this._queuedGamepadButtons[index] |= 1u << (int)gamepadEvent.Button;
                    break;

                case GamepadEventKind.ButtonReleased:
                    this._queuedGamepadButtons[index] &= ~(1u << (int)gamepadEvent.Button);
                    break;

                case GamepadEventKind.AxisMoved:
                    this._queuedGamepadAxes[index][(int)gamepadEvent.Axis] = gamepadEvent.Value;
                    break;
            }

            this._gamepadQueueBuffer.Add(gamepadEvent);
        }
    }

    /// <summary>
    /// Atomically dequeues the gamepad events since the last call. The list is reused, so it
    /// mustn't be kept beyond the frame.
    /// </summary>
    internal List<GamepadEvent> DequeueGamepadEvents()
    {
        this._gamepadDequeueBuffer.Clear();

        lock (this._bufferLock)
        {
            (this._gamepadDequeueBuffer, this._gamepadQueueBuffer) = (this._gamepadQueueBuffer, this._gamepadDequeueBuffer);
        }

        foreach (var gamepadEvent in this._gamepadDequeueBuffer)
        {
            int index = gamepadEvent.Index;
            switch (gamepadEvent.Kind)
            {
                case GamepadEventKind.Connected:
                    this._committedGamepadConnected[index] = true;
                    break;

                case GamepadEventKind.Disconnected:
                    this._committedGamepadConnected[index] = false;
                    break;

                case GamepadEventKind.ButtonPressed:
                    this._committedGamepadButtons[index] |= 1u << (int)gamepadEvent.Button;
                    break;

                case GamepadEventKind.ButtonReleased:
                    this._committedGamepadButtons[index] &= ~(1u << (int)gamepadEvent.Button);
                    break;

                case GamepadEventKind.AxisMoved:
                    this._committedGamepadAxes[index][(int)gamepadEvent.Axis] = gamepadEvent.Value;
                    break;
            }
        }

        return this._gamepadDequeueBuffer;
    }

    /// <summary>
    /// Returns a gamepad as it is this frame, for polling during the update phase. One that isn't
    /// connected reads as nothing held. No allocation is performed.
    /// </summary>
    /// <param name="index">Which gamepad, from 0 to one less than <see cref="MaxGamepads"/>.</param>
    public GamepadState GetGamepadState(
        int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxGamepads);

        return new GamepadState(this._committedGamepadConnected[index], this._committedGamepadButtons[index], this._committedGamepadAxes[index]);
    }

    /// <summary>
    /// Returns the keyboard as it is this frame, for polling during the update phase (e.g.
    /// continuous movement). No allocation is performed.
    /// </summary>
    public KeyboardState GetKeyboardState()
    {
        return new KeyboardState(this._committedKeysDownLow, this._committedKeysDownHigh, this._committedModifiers);
    }

    /// <summary>
    /// Called by the platform input strategy to queue a keyboard event.
    /// </summary>
    internal void QueueKeyEvent(
        KeyEventKind state,
        Key key,
        ModifierKeys modifiers,
        bool isRepeat = false,
        int virtualKey = 0)
    {
        var keyEvent = new KeyEvent(state, key, modifiers, isRepeat, virtualKey);

        lock (this._bufferLock)
        {
            int index = (int)key;
            if ((index >= 0) && (index < this._queuedKeysDown.Length))
            {
                this._queuedKeysDown[index] = (state == KeyEventKind.Pressed);
            }

            this._keyQueueBuffer.Add(keyEvent);
        }
    }

    // ---------------- Text input ----------------

    private List<TextInputEvent> _textInputQueueBuffer = new();
    private List<TextInputEvent> _textInputDequeueBuffer = new();

    /// <summary>
    /// Creates a new text-input control backed by the platform's native
    /// editor. The control remains registered with the platform until
    /// <see cref="TextInput.Dispose"/> is called. Subscribe to its
    /// events to observe edits, focus changes, submit, and cancel.
    /// </summary>
    /// <remarks>
    /// Use <see cref="TextInput.Bounds"/> to position the editor's
    /// transparent overlay over the on-canvas text field. Call
    /// <see cref="TextInput.Focus"/> from a tap-gesture handler to give
    /// keyboard focus synchronously inside the trusted gesture (this is
    /// required by iOS Safari to bring up the soft keyboard).
    /// </remarks>
    public TextInput CreateTextInput()
    {
        var input = new TextInput(this);
        var sink = new Sink(this, input);
        var handle = this.Strategy.Interface.CreateTextInputHandle(sink);
        input.AttachHandle(handle);
        return input;
    }

    /// <summary>
    /// Drains queued text-input callbacks and dispatches them on the
    /// game thread. Called from <c>Game.Update</c> before <c>OnUpdate</c>.
    /// </summary>
    internal void DispatchTextInputEvents()
    {
        // Prepare the dequeue buffer to be reused as the new enqueue buffer
        this._textInputDequeueBuffer.Clear();

        // Swap the enqueue buffer with the cleared dequeue buffer
        lock (this._bufferLock)
        {
            (this._textInputDequeueBuffer, this._textInputQueueBuffer) = (this._textInputQueueBuffer, this._textInputDequeueBuffer);
        }

        if (this._textInputDequeueBuffer.Count == 0)
        {
            return;
        }

        foreach (var ev in this._textInputDequeueBuffer)
        {
            var input = ev.Input;
            if (input.IsDisposed)
            {
                continue;
            }

            switch (ev.Kind)
            {
                case TextInputEventKind.TextChanged:
                    input.RaiseTextChanged(ev.Text!);
                    break;

                case TextInputEventKind.CompositionChanged:
                    input.RaiseCompositionChanged(ev.Text!);
                    break;

                case TextInputEventKind.GotFocus:
                    input.RaiseGotFocus();
                    break;

                case TextInputEventKind.LostFocus:
                    input.RaiseLostFocus();
                    break;

                case TextInputEventKind.Submit:
                    input.RaiseSubmitted();
                    break;

                case TextInputEventKind.Cancel:
                    input.RaiseCanceled();
                    break;
            }
        }
    }

    private void EnqueueTextInputEvent(
        TextInput input,
        TextInputEventKind kind,
        string? text)
    {
        var ev = new TextInputEvent(input, kind, text);
        lock (this._bufferLock)
        {
            this._textInputQueueBuffer.Add(ev);
        }
    }

    private enum TextInputEventKind
    {
        TextChanged,
        CompositionChanged,
        GotFocus,
        LostFocus,
        Submit,
        Cancel,
    }

    private readonly struct TextInputEvent
    {
        public TextInputEvent(TextInput input, TextInputEventKind kind, string? text)
        {
            this.Input = input;
            this.Kind = kind;
            this.Text = text;
        }

        public readonly TextInput Input;
        public readonly TextInputEventKind Kind;
        public readonly string? Text;
    }

    private sealed class Sink : ITextInputSink
    {
        private readonly InputService _service;
        private readonly TextInput _input;

        public Sink(InputService service, TextInput input)
        {
            this._service = service;
            this._input = input;
        }

        public void OnTextChanged(string fullText)
            => this._service.EnqueueTextInputEvent(this._input, TextInputEventKind.TextChanged, fullText ?? string.Empty);

        public void OnCompositionChanged(string composition)
            => this._service.EnqueueTextInputEvent(this._input, TextInputEventKind.CompositionChanged, composition);

        public void OnGotFocus()
            => this._service.EnqueueTextInputEvent(this._input, TextInputEventKind.GotFocus, null);

        public void OnLostFocus()
            => this._service.EnqueueTextInputEvent(this._input, TextInputEventKind.LostFocus, null);

        public void OnSubmit()
            => this._service.EnqueueTextInputEvent(this._input, TextInputEventKind.Submit, null);

        public void OnCancel()
            => this._service.EnqueueTextInputEvent(this._input, TextInputEventKind.Cancel, null);
    }
}
