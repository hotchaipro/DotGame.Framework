// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Desktop;

/// <summary>
/// How finished frames reach the screen.
/// </summary>
public enum PresentMode
{
    /// <summary>Paced to the display, with no tearing: the default.</summary>
    VSync,

    /// <summary>
    /// As fast as frames are made, replacing any not yet shown, with no tearing, where the
    /// graphics driver supports it; otherwise <see cref="VSync"/>.
    /// </summary>
    Mailbox,

    /// <summary>
    /// As fast as frames are made, with tearing, where the graphics driver supports it; otherwise
    /// <see cref="VSync"/>.
    /// </summary>
    Immediate,
}
