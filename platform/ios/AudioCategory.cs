// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.iOS;

/// <summary>
/// How a game's audio sits with other apps' and the silent switch.
/// </summary>
public enum AudioCategory
{
    /// <summary>
    /// Mixes with other apps, so the player's own music keeps playing, and respects the silent
    /// switch: the usual choice for a casual game.
    /// </summary>
    Ambient,

    /// <summary>
    /// Silences other apps' audio, and respects the silent switch.
    /// </summary>
    SoloAmbient,

    /// <summary>
    /// Silences other apps' audio and plays through the silent switch: for a game whose audio is
    /// the point of it.
    /// </summary>
    Playback,
}
