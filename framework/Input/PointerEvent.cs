// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Input;

/// <summary>A touch, mouse or pen event.</summary>
public readonly record struct PointerEvent
{
    internal PointerEvent(
        PointerEventKind kind,
        int id,
        Vector2 position,
        PointerDevice device = PointerDevice.Mouse,
        PointerButton button = PointerButton.None,
        PointerButton heldButtons = PointerButton.None,
        Vector2 scrollWheelDelta = default,
        int touchCount = 1,
        int touchIndex = 0,
        TimeSpan? timestamp = null)
    {
        this.Kind = kind;
        this.Id = id;
        this.Position = position;
        this.Device = device;
        this.Button = button;
        this.HeldButtons = heldButtons;
        this.ScrollWheelDelta = scrollWheelDelta;
        this.TouchCount = touchCount;
        this.TouchIndex = touchIndex;
        this.Timestamp = timestamp ?? TimeSpan.FromMilliseconds(NowMs());
    }

    /// <summary>What happened.</summary>
    public PointerEventKind Kind { get; }

    /// <summary>Identifies the pointer through its events: one finger, or the mouse.</summary>
    public int Id { get; }

    /// <summary>What kind of pointer it is.</summary>
    public PointerDevice Device { get; }

    /// <summary>The button this event is about, for a press or release.</summary>
    public PointerButton Button { get; }

    /// <summary>All the buttons held during this event.</summary>
    public PointerButton HeldButtons { get; }

    /// <summary>Where it is, in the window's pixels.</summary>
    public Vector2 Position { get; }

    /// <summary>How far the wheel moved, for a wheel event.</summary>
    public Vector2 ScrollWheelDelta { get; }

    /// <summary>
    /// The number of touches down after this event: a touch's Began counts itself, and its
    /// Ended or Canceled doesn't, so the last touch to lift reports 0. Only touches count: a
    /// mouse or pen event always reports 0, even with a button held, so on a Began, games tell
    /// touch input from mouse input by it.
    /// </summary>
    public int TouchCount { get; }

    /// <summary>Which of the touches down this one is, from 0.</summary>
    public int TouchIndex { get; }

    /// <summary>
    /// When the event happened, on a monotonic clock, as near as the
    /// platform says: on desktop, when the system saw it; elsewhere, when the
    /// platform delivered it. Gesture recognizers measure press durations from
    /// this value so classification is immune to game-thread dispatch delays,
    /// and it's fine enough for velocities. Only differences between timestamps
    /// are meaningful.
    /// </summary>
    public TimeSpan Timestamp { get; }

    internal static long NowMs()
    {
        return System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return FormattableString.Invariant($"Id: {this.Id} Button: {this.Button} HeldButtons: {this.HeldButtons} TouchIndex: {this.TouchIndex} ScrollWheelDelta: {this.ScrollWheelDelta}");
    }
}
