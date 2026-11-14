// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using DotGame.Framework.Input;
using DotGame.Framework.Platform.Input;

namespace DotGame.Framework.WebGL.Input;

[SupportedOSPlatform("browser")]
internal sealed partial class InputServiceStrategy : IInputServiceStrategy
{
    private const string ModuleName = "input.js";

    // Per-instance lookup of active text-input handles. Indexed by the
    // JS-side instance ID so JSExport callbacks can route back to the
    // correct .NET handle.
    private static readonly Dictionary<int, TextInputHandle> s_handles = new();
    private static int s_nextHandleId;

    public InputServiceStrategy()
    {
    }

    public Task OnInitialize()
    {
        InitializeInput(FeatureSwitches.TraceInput);
        return Task.CompletedTask;
    }

    [JSImport("inputService.initializeInput", ModuleName)]
    internal static partial void InitializeInput(bool traceInput);

    public ITextInputHandle CreateTextInputHandle(
        ITextInputSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        int id = ++s_nextHandleId;
        var handle = new TextInputHandle(id, sink);
        s_handles[id] = handle;
        CreateTextInputJS(id);
        return handle;
    }

    private static void RemoveHandle(int id)
    {
        s_handles.Remove(id);
        DestroyTextInputJS(id);
    }

    [JSImport("inputService.createTextInput", ModuleName)]
    internal static partial void CreateTextInputJS(int instanceId);

    [JSImport("inputService.destroyTextInput", ModuleName)]
    internal static partial void DestroyTextInputJS(int instanceId);

    [JSImport("inputService.updateTextInput", ModuleName)]
    internal static partial void UpdateTextInputJS(
        int instanceId,
        float x,
        float y,
        float width,
        float height,
        string text,
        int inputType,
        int maxLength,
        bool multiline);

    [JSImport("inputService.focusTextInput", ModuleName)]
    internal static partial void FocusTextInputJS(int instanceId);

    [JSImport("inputService.blurTextInput", ModuleName)]
    internal static partial void BlurTextInputJS(int instanceId);

    [JSExport]
    public static void OnTextInputChanged(int instanceId, string text)
    {
        try
        {
            if (s_handles.TryGetValue(instanceId, out var handle))
            {
                handle.Sink.OnTextChanged(text ?? string.Empty);
            }
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x1e5d7a92, e);
        }
    }

    [JSExport]
    public static void OnTextInputCompositionChanged(int instanceId, string composition)
    {
        if (s_handles.TryGetValue(instanceId, out var handle))
        {
            handle.Sink.OnCompositionChanged(composition ?? string.Empty);
        }
    }

    [JSExport]
    public static void OnTextInputGotFocus(int instanceId)
    {
        try
        {
            if (s_handles.TryGetValue(instanceId, out var handle))
            {
                handle.Sink.OnGotFocus();
            }
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x2f6e8b03, e);
        }
    }

    [JSExport]
    public static void OnTextInputLostFocus(int instanceId)
    {
        try
        {
            if (s_handles.TryGetValue(instanceId, out var handle))
            {
                handle.Sink.OnLostFocus();
            }
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x3a7f9c14, e);
        }
    }

    [JSExport]
    public static void OnTextInputSubmit(int instanceId)
    {
        try
        {
            if (s_handles.TryGetValue(instanceId, out var handle))
            {
                handle.Sink.OnSubmit();
            }
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x4b801d25, e);
        }
    }

    [JSExport]
    public static void OnTextInputCancel(int instanceId)
    {
        try
        {
            if (s_handles.TryGetValue(instanceId, out var handle))
            {
                handle.Sink.OnCancel();
            }
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x5c912e36, e);
        }
    }

    private sealed class TextInputHandle : ITextInputHandle
    {
        private readonly int _id;
        private readonly ITextInputSink _sink;
        private bool _isDisposed;

        public TextInputHandle(int id, ITextInputSink sink)
        {
            this._id = id;
            this._sink = sink;
        }

        internal ITextInputSink Sink => this._sink;

        public void Update(in TextInputState state)
        {
            if (this._isDisposed)
            {
                return;
            }
            UpdateTextInputJS(
                this._id,
                state.Bounds.X,
                state.Bounds.Y,
                state.Bounds.Width,
                state.Bounds.Height,
                state.Text ?? string.Empty,
                (int)state.InputType,
                state.MaxLength,
                state.Multiline);
        }

        public void Focus()
        {
            if (this._isDisposed)
            {
                return;
            }
            FocusTextInputJS(this._id);
        }

        public void Blur()
        {
            if (this._isDisposed)
            {
                return;
            }
            BlurTextInputJS(this._id);
        }

        public void Dispose()
        {
            if (this._isDisposed)
            {
                return;
            }
            this._isDisposed = true;
            RemoveHandle(this._id);
        }
    }

    [JSExport]
    public static void OnPointerDown(
        int instanceId,
        int pointerId,
        int pointerType,
        float clientX,
        float clientY,
        int button = -1,
        int buttons = 0)
    {
        try
        {
            if (FeatureSwitches.TraceInput)
            {
                Trace.Write(0x1a3c5e91, $"[Web] OnPointerDown inst={instanceId} id={pointerId} type={pointerType} pos=({clientX:0},{clientY:0}) btn={button} btns={buttons}");
            }
            GameBindings.GetInstance(instanceId).Host.QueuePointerEvent(new PointerEventData
            {
                State = PointerEventKind.Began,
                PointerId = pointerId,
                PointerType = (PointerDevice)pointerType,
                Position = new Vector2(clientX, clientY),
                Button = MapPointerButton(button),
                HeldButtons = MapHeldButtons(buttons)
            });
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x6da23f47, e);
        }
    }

    [JSExport]
    public static void OnPointerMove(
        int instanceId,
        int pointerId,
        int pointerType,
        float clientX,
        float clientY,
        int buttons = 0)
    {
        // Note: do NOT log every move at the JSExport boundary -- the
        // ring buffer would be saturated by move events and the most
        // recent Began/Ended would scroll out before a postmortem can
        // capture them. Move events are summarized at the recognizer
        // layer (slop crossings, Recognize) instead.
        try
        {
            GameBindings.GetInstance(instanceId).Host.QueuePointerEvent(new PointerEventData
            {
                State = PointerEventKind.Moved,
                PointerId = pointerId,
                PointerType = (PointerDevice)pointerType,
                Position = new Vector2(clientX, clientY),
                HeldButtons = MapHeldButtons(buttons)
            });
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x7eb34058, e);
        }
    }

    [JSExport]
    public static void OnPointerUp(
        int instanceId,
        int pointerId,
        int pointerType,
        float clientX,
        float clientY,
        int button = -1,
        int buttons = 0)
    {
        try
        {
            if (FeatureSwitches.TraceInput)
            {
                Trace.Write(0x2b4d6fa2, $"[Web] OnPointerUp inst={instanceId} id={pointerId} type={pointerType} pos=({clientX:0},{clientY:0}) btn={button} btns={buttons}");
            }
            GameBindings.GetInstance(instanceId).Host.QueuePointerEvent(new PointerEventData
            {
                State = PointerEventKind.Ended,
                PointerId = pointerId,
                PointerType = (PointerDevice)pointerType,
                Position = new Vector2(clientX, clientY),
                Button = MapPointerButton(button),
                HeldButtons = MapHeldButtons(buttons)
            });
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x8fc45169, e);
        }
    }

    [JSExport]
    public static void OnPointerCancel(
        int instanceId,
        int pointerId,
        int pointerType,
        float clientX,
        float clientY,
        int buttons = 0)
    {
        // Browser-canceled pointer (capture loss, system gesture preempt,
        // popover, scroll, focus shift). Must be reported as Canceled, NOT
        // Ended -- previously this was mapped to Ended which produced
        // spurious Tapped events. Recognizers gate Canceled on their own
        // pointer-id history, so this is safe and necessary for the
        // arena to fail-clean rather than fire a stale recognition.
        try
        {
            if (FeatureSwitches.TraceInput)
            {
                Trace.Write(0x3c5e80b3, $"[Web] OnPointerCancel inst={instanceId} id={pointerId} type={pointerType} pos=({clientX:0},{clientY:0}) btns={buttons}");
            }
            GameBindings.GetInstance(instanceId).Host.QueuePointerEvent(new PointerEventData
            {
                State = PointerEventKind.Canceled,
                PointerId = pointerId,
                PointerType = (PointerDevice)pointerType,
                Position = new Vector2(clientX, clientY),
                HeldButtons = MapHeldButtons(buttons)
            });
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x90d5627a, e);
        }
    }

    [JSExport]
    public static void OnWheel(
        int instanceId,
        float clientX,
        float clientY,
        float deltaX,
        float deltaY)
    {
        try
        {
            GameBindings.GetInstance(instanceId).Host.QueuePointerEvent(new PointerEventData
            {
                State = PointerEventKind.Wheel,
                PointerId = -1,
                PointerType = PointerDevice.Mouse,
                Position = new Vector2(clientX, clientY),
                ScrollWheelDelta = new Vector2(deltaX, deltaY)
            });
        }
        catch (Exception e)
        {
            GameHost.Terminate(0xb2f7849c, e);
        }
    }

    [JSExport]
    public static void OnKeyDown(
        int instanceId,
        string code,
        int modifiers,
        bool isRepeat)
    {
        try
        {
            GameBindings.GetInstance(instanceId).Host.QueueKeyEvent(new KeyEventData
            {
                State = KeyEventKind.Pressed,
                Key = KeyMap.FromDomCode(code),
                Modifiers = (ModifierKeys)modifiers,
                IsRepeat = isRepeat
            });
        }
        catch (Exception e)
        {
            GameHost.Terminate(0xc30895ad, e);
        }
    }

    [JSExport]
    public static void OnKeyUp(
        int instanceId,
        string code,
        int modifiers)
    {
        try
        {
            GameBindings.GetInstance(instanceId).Host.QueueKeyEvent(new KeyEventData
            {
                State = KeyEventKind.Released,
                Key = KeyMap.FromDomCode(code),
                Modifiers = (ModifierKeys)modifiers
            });
        }
        catch (Exception e)
        {
            GameHost.Terminate(0xd419a6be, e);
        }
    }

    private static PointerButton MapPointerButton(
        int button)
    {
        return button switch
        {
            0 => PointerButton.Left,
            1 => PointerButton.Middle,
            2 => PointerButton.Right,
            3 => PointerButton.X1,
            4 => PointerButton.X2,
            _ => PointerButton.None,
        };
    }

    private static PointerButton MapHeldButtons(
        int buttons)
    {
        var result = PointerButton.None;

        if ((buttons & 1) != 0)
        {
            result |= PointerButton.Left;
        }
        if ((buttons & 2) != 0)
        {
            result |= PointerButton.Right;
        }
        if ((buttons & 4) != 0)
        {
            result |= PointerButton.Middle;
        }
        if ((buttons & 8) != 0)
        {
            result |= PointerButton.X1;
        }
        if ((buttons & 16) != 0)
        {
            result |= PointerButton.X2;
        }

        return result;
    }
}
