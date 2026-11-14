// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Numerics;
using DotGame.Framework;
using DotGame.Framework.Graphics;

namespace Sample;

/// <summary>
/// Draws ASCII text from a texture of fixed-size character cells.
/// </summary>
/// <remarks>
/// The framework draws sprites, not text; a real game wants a font renderer, with kerning, any
/// character set, and outlines and shadows. This is the smallest thing that
/// works with the framework alone, and an example of drawing part of a texture with a source
/// rectangle: <c>Content/Textures/ascii.png</c> holds characters 32 to 127 in 16 columns of
/// 20 x 32 pixel cells, so each character's rectangle follows from its code.
/// </remarks>
internal sealed class PixelText
{
    internal const int CellWidth = 20;
    internal const int LineHeight = 32;
    private const int Columns = 16;
    private const int FirstCharacter = 32;
    private const int LastCharacter = 127;

    private readonly Texture _texture;

    internal PixelText(
        Texture texture)
    {
        this._texture = texture;
    }

    /// <summary>
    /// Gets the width of the longest line of the text.
    /// </summary>
    internal static float Measure(
        ReadOnlySpan<char> text,
        float scale = 1)
    {
        int longest = 0;
        int length = 0;
        foreach (var c in text)
        {
            length = (c == '\n') ? 0 : length + 1;
            longest = Math.Max(longest, length);
        }

        return longest * CellWidth * scale;
    }

    internal void Draw(
        SpriteBatch spriteBatch,
        ReadOnlySpan<char> text,
        Vector2 position,
        Color color,
        float scale = 1)
    {
        var pen = position;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                pen = new Vector2(position.X, pen.Y + (LineHeight * scale));
                continue;
            }

            int code = ((c >= FirstCharacter) && (c <= LastCharacter)) ? c : '?';
            if (code != ' ')
            {
                int cell = code - FirstCharacter;
                var source = new Rectangle((cell % Columns) * CellWidth, (cell / Columns) * LineHeight, CellWidth, LineHeight);
                spriteBatch.Draw(this._texture, pen, color, scale: new Vector2(scale), sourceRectangle: source);
            }

            pen.X += CellWidth * scale;
        }
    }
}
