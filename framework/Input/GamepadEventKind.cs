// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>What a gamepad event reports.</summary>
public enum GamepadEventKind
{
    /// <summary>A gamepad was connected.</summary>
    Connected,
    /// <summary>A gamepad was disconnected.</summary>
    Disconnected,
    /// <summary>A button went down.</summary>
    ButtonPressed,
    /// <summary>A button came up.</summary>
    ButtonReleased,
    /// <summary>A stick or trigger moved.</summary>
    AxisMoved,
}
