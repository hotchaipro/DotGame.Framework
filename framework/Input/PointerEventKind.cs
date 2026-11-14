// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>What a pointer event reports.</summary>
public enum PointerEventKind
{
    /// <summary>A finger came down, or a button was pressed.</summary>
    Began = 1,
    /// <summary>It moved. A mouse moves with no button held too.</summary>
    Moved = 2,
    /// <summary>A finger lifted, or a button was released.</summary>
    Ended = 3,
    /// <summary>The press ended without acting: the game lost the focus or the system took over. Drop what it started.</summary>
    Canceled = 4,

    /// <summary>The wheel turned, by <see cref="PointerEvent.ScrollWheelDelta"/>, with the pointer where it is.</summary>
    Wheel = 5,
}
