// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;

namespace DotGame.Framework;

/// <summary>
/// A color as four bytes, RGBA in memory: the form of texture pixels, as
/// <see cref="Graphics.Texture.SetData"/> and <c>CreateTexture</c> take them. For drawing, use
/// <see cref="Color"/>.
/// </summary>
/// <remarks>
/// Textures take premultiplied pixels. <see cref="FromRgba"/> makes one from straight alpha, and
/// <see cref="FromPremultiplied"/> stores its bytes as they are. The one place pixels aren't
/// premultiplied is where you ask for that, from an image decoder.
/// </remarks>
public readonly struct Color32 : IEquatable<Color32>
{
    /// <summary>Red, from 0 to 255.</summary>
    public byte R { get; }

    /// <summary>Green, from 0 to 255.</summary>
    public byte G { get; }

    /// <summary>Blue, from 0 to 255.</summary>
    public byte B { get; }

    /// <summary>Alpha, from 0 (transparent) to 255 (opaque).</summary>
    public byte A { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Color32(byte r, byte g, byte b, byte a)
    {
        this.R = r;
        this.G = g;
        this.B = b;
        this.A = a;
    }

    /// <summary>
    /// Makes a pixel from bytes whose color isn't premultiplied, multiplying it by alpha:
    /// <c>Color32.FromRgba(255, 0, 0, 128)</c> is half-transparent red.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color32 FromRgba(byte r, byte g, byte b, byte a)
    {
        return new Color32(Premultiply(r, a), Premultiply(g, a), Premultiply(b, a), a);
    }

    /// <summary>
    /// Makes a pixel from its bytes as they are, whose color is already multiplied by its alpha.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color32 FromPremultiplied(byte r, byte g, byte b, byte a)
    {
        return new Color32(r, g, b, a);
    }

    /// <summary>The pixel as a <see cref="Color"/>, which holds it exactly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Color ToColor()
    {
        return Color.FromPremultiplied(this.R / 255f, this.G / 255f, this.B / 255f, this.A / 255f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Premultiply(
        byte component,
        byte alpha)
    {
        return (byte)(((component * alpha) + 127) / 255);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Color32 FromPremultipliedComponents(
        float r,
        float g,
        float b,
        float a)
    {
        return new Color32(ToByte(r), ToByte(g), ToByte(b), ToByte(a));
    }

    // NOTE: Clamps, so an over-bright tint such as Color.White * 1.2f stays white rather than
    // wrapping around to a dark color, as the sprite path's color packing does
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByte(
        float component)
    {
        return (byte)Math.Clamp((component * 255f) + 0.5f, 0f, 255f);
    }

    /// <summary>Whether two pixels have the same bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Color32 a, Color32 b)
    {
        return a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
    }

    /// <summary>Whether two pixels have different bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Color32 a, Color32 b)
    {
        return a.R != b.R || a.G != b.G || a.B != b.B || a.A != b.A;
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return this.R | (this.G << 8) | (this.B << 16) | (this.A << 24);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is Color32 other && this.Equals(other);
    }

    /// <summary>Whether this pixel has the same bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Color32 other)
    {
        return this.R == other.R && this.G == other.G && this.B == other.B && this.A == other.A;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return FormattableString.Invariant($"R: {this.R} G: {this.G} B: {this.B} A: {this.A}");
    }
}
