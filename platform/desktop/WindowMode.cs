// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Desktop;

/// <summary>
/// The state the window is in. Each state excludes the others, so one value says it all.
/// </summary>
public enum WindowMode
{
    /// <summary>A window with a title bar, at the size the game or the player gave it. The default.</summary>
    Windowed,

    /// <summary>A window with a title bar, filling the screen's usable area.</summary>
    Maximized,

    /// <summary>
    /// Covering the whole screen with no title bar, without changing the display's resolution.
    /// </summary>
    FullScreen,
}
