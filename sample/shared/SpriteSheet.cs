// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework;

namespace Sample;

/// <summary>
/// The layout of <c>Content/Textures/spritesheet.png</c>: sixteen sprites, each a variant of the
/// sample's character, in a 4 x 4 grid of 256-pixel cells. A sprite is a rectangle of the one
/// texture, which a <c>SpriteBatch.Draw</c> call names as its source rectangle.
/// </summary>
internal static class SpriteSheet
{
    /// <summary>The number of sprites.</summary>
    public const int Count = 16;

    /// <summary>The sprites in a row of the sheet.</summary>
    public const int Columns = 4;

    /// <summary>The size of a cell, in the sheet's pixels.</summary>
    public const int CellSize = 256;

    /// <summary>The plain character, with the red bandana: the third in the third row.</summary>
    public const int Hero = 10;

    /// <summary>
    /// Sprite <paramref name="index"/>'s rectangle in the sheet: its cell, less the margin around
    /// the sprite (make-content.py fits each in 240 x 248 at (8, 4) in its cell).
    /// </summary>
    public static Rectangle Source(
        int index)
    {
        return new Rectangle(((index % Columns) * CellSize) + 8, ((index / Columns) * CellSize) + 4, 240, 248);
    }
}
