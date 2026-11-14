// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Input;

namespace DotGame.Framework.Platform.Input;

/// <summary>
/// A pointer event (mouse, touch, pen) as a platform host reports it to
/// <see cref="GameHost.QueuePointerEvent"/>.
/// </summary>
internal readonly struct PointerEventData
{
    /// <summary>
    /// How long ago the event happened, for a platform that delivers events later than they
    /// happened and knows when they did.
    /// </summary>
    public long AgeMs { get; init; }

    public required PointerEventKind State { get; init; }

    /// <summary>
    /// Identifies the pointer across its events: a touch's identity, or 0 for the mouse.
    /// </summary>
    public required int PointerId { get; init; }

    public required PointerDevice PointerType { get; init; }

    /// <summary>
    /// The pointer's position, in the window's client pixels.
    /// </summary>
    public required Vector2 Position { get; init; }

    /// <summary>
    /// The button whose press or release this event reports, if any.
    /// </summary>
    public PointerButton Button { get; init; }

    /// <summary>
    /// The buttons held down during this event.
    /// </summary>
    public PointerButton HeldButtons { get; init; }

    /// <summary>
    /// The scroll wheel's movement, for a scroll event.
    /// </summary>
    public Vector2 ScrollWheelDelta { get; init; }
}
