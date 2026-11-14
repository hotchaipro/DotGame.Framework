// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DotGame.Framework;

/// <summary>
/// A color with float components, stored with premultiplied alpha.
/// </summary>
/// <remarks>
/// <para>
/// Make one with <c>new Color(0.2f, 0.4f, 0.6f, 0.5f)</c>, from components from 0 to 1, or
/// <see cref="FromRgb"/> for a color written as a number. Both take straight alpha, so the first is
/// half-transparent. Only <see cref="FromPremultiplied"/> doesn't.
/// </para>
/// <para>
/// What a color holds, and <see cref="R"/>, <see cref="G"/> and <see cref="B"/> read back, is always
/// premultiplied, which is why <c>color * 0.5f</c> fades one.
/// </para>
/// </remarks>
public readonly struct Color : IEquatable<Color>
{
    /// <summary>Fully transparent.</summary>
    public static Color Transparent => new(0f, 0f, 0f, 0f);
    /// <summary>Black.</summary>
    public static Color Black => new(0f, 0f, 0f);
    /// <summary>Blue: full blue only.</summary>
    public static Color Blue => new(0f, 0f, 1f);
    /// <summary>Cyan: full green and blue.</summary>
    public static Color Cyan => new(0f, 1f, 1f);
    /// <summary>Mid gray: 128 of 255 in each channel.</summary>
    public static Color Gray => new(128 / 255f, 128 / 255f, 128 / 255f);
    /// <summary>Green: full green only.</summary>
    public static Color Green => new(0f, 1f, 0f);
    /// <summary>Magenta: full red and blue.</summary>
    public static Color Magenta => new(1f, 0f, 1f);
    /// <summary>Red: full red only.</summary>
    public static Color Red => new(1f, 0f, 0f);
    /// <summary>White.</summary>
    public static Color White => new(1f, 1f, 1f);
    /// <summary>Yellow: full red and green.</summary>
    public static Color Yellow => new(1f, 1f, 0f);

    /// <summary>
    /// Gets the premultiplied red component, normally from 0 to 1. Above 1 is over-bright.
    /// </summary>
    public float R { get; private init; }

    /// <summary>
    /// Gets the premultiplied green component, normally from 0 to 1. Above 1 is over-bright.
    /// </summary>
    public float G { get; private init; }

    /// <summary>
    /// Gets the premultiplied blue component, normally from 0 to 1. Above 1 is over-bright.
    /// </summary>
    public float B { get; private init; }

    /// <summary>
    /// Gets the alpha component, from 0 to 1.
    /// </summary>
    public float A { get; private init; }

    /// <summary>
    /// Makes a color from components from 0 to 1, whose color isn't premultiplied:
    /// <c>new Color(1f, 0f, 0f, 0.5f)</c> is half-transparent red.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Color(float r, float g, float b, float a = 1f)
    {
        this.R = r * a;
        this.G = g * a;
        this.B = b * a;
        this.A = a;
    }

    /// <summary>Whole numbers aren't colors: this stops <c>new Color(255, 128, 0)</c> compiling.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Color takes floats from 0 to 1. Write 0.5f, or use Color.FromRgb(0xFF8000) for a color written as a number.", error: true)]
    public Color(int r, int g, int b, int a = 255)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Creates a color from components as they're stored, whose color is already multiplied by its
    /// alpha. An alpha below the color components is how additive sprites and particles are made.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color FromPremultiplied(float r, float g, float b, float a)
    {
        return new Color { R = r, G = g, B = b, A = a };
    }

    /// <summary>
    /// Makes a color from a number written as <c>0xRRGGBB</c>, so <c>255</c> is blue, not gray, and an alpha,
    /// from 0 to 1, which isn't premultiplied.
    /// </summary>
    /// <remarks>Build rule DG0007 catches a number written out that can't be what was meant.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The number is above <c>0xFFFFFF</c>, as one with alpha
    /// written in it would be, or the alpha isn't from 0 to 1.</exception>
    public static Color FromRgb(uint rgb, float alpha = 1f)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rgb, 0xFFFFFFu);
        CheckAlpha(alpha);

        return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
    }

    /// <summary>
    /// Gets this color with its alpha set to a value from 0 to 1, which isn't premultiplied. To fade a
    /// color by an amount, whatever its alpha, multiply it instead.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The alpha isn't from 0 to 1.</exception>
    /// <exception cref="InvalidOperationException">This color adds to what's behind it, with color but no
    /// alpha, so it has no color to give an alpha to. <see cref="Transparent"/> holds no color, and gives black.</exception>
    public Color WithAlpha(float alpha)
    {
        CheckAlpha(alpha);

        if (this.A <= 0f)
        {
            if ((this.R != 0f) || (this.G != 0f) || (this.B != 0f))
            {
                throw new InvalidOperationException("A color with no alpha that adds to what's behind it has no color to give an alpha to.");
            }

            return FromPremultiplied(0f, 0f, 0f, alpha);
        }

        float scale = alpha / this.A;
        return FromPremultiplied(this.R * scale, this.G * scale, this.B * scale, alpha);
    }

    private static void CheckAlpha(float alpha, [CallerArgumentExpression(nameof(alpha))] string? name = null)
    {
        if (!((alpha >= 0f) && (alpha <= 1f)))
        {
            throw new ArgumentOutOfRangeException(name, alpha, "Alpha is from 0 to 1.");
        }
    }

    /// <summary>Whether two colors have the same components.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Color a, Color b)
    {
        return a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
    }

    /// <summary>Whether two colors have different components.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Color a, Color b)
    {
        return a.R != b.R || a.G != b.G || a.B != b.B || a.A != b.A;
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return HashCode.Combine(this.R, this.G, this.B, this.A);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is Color other && this.Equals(other);
    }

    /// <summary>Whether this has the same components. Unlike <c>==</c>, not-a-number equals itself, as for a <see cref="float"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Color other)
    {
        return this.R.Equals(other.R) && this.G.Equals(other.G) && this.B.Equals(other.B) && this.A.Equals(other.A);
    }

    /// <summary>
    /// A color part of the way from one to another: 0 gives the first, and 1 the second. The amount is
    /// clamped to that range, as colors beyond the two have no meaning.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color Lerp(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return FromPremultiplied(
            from.R + ((to.R - from.R) * amount),
            from.G + ((to.G - from.G) * amount),
            from.B + ((to.B - from.B) * amount),
            from.A + ((to.A - from.A) * amount));
    }

    /// <summary>The color faded by <paramref name="scale"/>: <c>color * 0.5f</c> is half as opaque.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color operator *(Color value, float scale)
    {
        return FromPremultiplied(value.R * scale, value.G * scale, value.B * scale, value.A * scale);
    }

    /// <summary>The color faded by <paramref name="scale"/>: <c>0.5f * color</c> is half as opaque.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color operator *(float scale, Color value)
    {
        return FromPremultiplied(value.R * scale, value.G * scale, value.B * scale, value.A * scale);
    }

    /// <summary>The two colors multiplied, component by component, which is how a tint works.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color operator *(Color color1, Color color2)
    {
        return FromPremultiplied(
            color1.R * color2.R,
            color1.G * color2.G,
            color1.B * color2.B,
            color1.A * color2.A);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return FormattableString.Invariant($"R: {this.R} G: {this.G} B: {this.B} A: {this.A}");
    }

    /// <summary>
    /// Gets this color as bytes, rounded and clamped to 0 to 255, so an over-bright tint stays white
    /// rather than wrapping around to a dark color.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Color32 ToColor32()
    {
        return Color32.FromPremultipliedComponents(this.R, this.G, this.B, this.A);
    }
}
