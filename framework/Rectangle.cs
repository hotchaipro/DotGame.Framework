// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;

namespace DotGame.Framework;

/// <summary>
/// Describes a 2D rectangle defined by a position and size.
/// </summary>
/// <remarks>
/// A rectangle with no positive area is empty, wherever it is and whatever its sizes: see
/// <see cref="IsEmpty"/>. An empty rectangle overlaps, contains and draws nothing. Make one from a drag
/// in any direction with <see cref="FromCorners"/>.
/// </remarks>
public readonly struct Rectangle : IEquatable<Rectangle>
{
    /// <summary>
    /// Creates a new <see cref="Rectangle"/> with the specified position and size.
    /// </summary>
    /// <param name="x">The x coordinate of the top-left corner.</param>
    /// <param name="y">The y coordinate of the top-left corner.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public Rectangle(
        float x,
        float y,
        float width,
        float height)
    {
        this.X = x;
        this.Y = y;
        this.Width = width;
        this.Height = height;
    }

    /// <summary>
    /// Creates a new <see cref="Rectangle"/> with the specified location and size. For two corners, use
    /// <see cref="FromCorners"/>.
    /// </summary>
    /// <param name="position">The position of the top-left corner.</param>
    /// <param name="size">The width and height.</param>
    public Rectangle(
        Vector2 position,
        Vector2 size)
    {
        this.X = position.X;
        this.Y = position.Y;
        this.Width = size.X;
        this.Height = size.Y;
    }

    /// <summary>
    /// Makes the rectangle between two opposite corners, in either order, so its size is never negative.
    /// </summary>
    /// <param name="corner">One corner, such as where a drag started.</param>
    /// <param name="oppositeCorner">The opposite corner, such as where it is now.</param>
    public static Rectangle FromCorners(
        Vector2 corner,
        Vector2 oppositeCorner)
    {
        return new Rectangle(
            MathF.Min(corner.X, oppositeCorner.X),
            MathF.Min(corner.Y, oppositeCorner.Y),
            MathF.Abs(oppositeCorner.X - corner.X),
            MathF.Abs(oppositeCorner.Y - corner.Y));
    }

    /// <summary>
    /// The x coordinate of the top-left corner.
    /// </summary>
    public float X { get; init; }

    /// <summary>
    /// The y coordinate of the top-left corner.
    /// </summary>
    public float Y { get; init; }

    /// <summary>
    /// The width.
    /// </summary>
    public float Width { get; init; }

    /// <summary>
    /// The height.
    /// </summary>
    public float Height { get; init; }

    /// <summary>
    /// Gets the x coordinate of the left edge.
    /// </summary>
    public float Left => this.X;

    /// <summary>
    /// Gets the x coordinate of the right edge.
    /// </summary>
    public float Right => this.X + this.Width;

    /// <summary>
    /// Gets the y coordinate of the top edge.
    /// </summary>
    public float Top => this.Y;

    /// <summary>
    /// Gets the y coordinate of the bottom edge.
    /// </summary>
    public float Bottom => this.Y + this.Height;

    /// <summary>
    /// Gets the top-left position.
    /// </summary>
    public Vector2 Position
    {
        get => new(this.X, this.Y);
        init
        {
            this.X = value.X;
            this.Y = value.Y;
        }
    }

    /// <summary>
    /// Gets the size.
    /// </summary>
    public Vector2 Size
    {
        get => new(this.Width, this.Height);
        init
        {
            this.Width = value.X;
            this.Height = value.Y;
        }
    }

    /// <summary>
    /// Gets whether this rectangle has no positive area, wherever it is: a width or height that's zero,
    /// negative or not a number.
    /// </summary>
    public bool IsEmpty => !((this.Width > 0) && (this.Height > 0));

    /// <summary>
    /// Gets the center position.
    /// </summary>
    public Vector2 Center => new(this.X + (this.Width * 0.5f), this.Y + (this.Height * 0.5f));

    /// <summary>
    /// Compares whether two <see cref="Rectangle"/> instances are equal.
    /// </summary>
    /// <param name="a">The first <see cref="Rectangle"/>.</param>
    /// <param name="b">The second <see cref="Rectangle"/>.</param>
    /// <returns><c>true</c> if the instances are equal; <c>false</c> otherwise.</returns>
    public static bool operator ==(Rectangle a, Rectangle b)
    {
        return (a.X == b.X) && (a.Y == b.Y) && (a.Width == b.Width) && (a.Height == b.Height);
    }

    /// <summary>
    /// Compares whether two <see cref="Rectangle"/> instances are not equal.
    /// </summary>
    /// <param name="a">The first <see cref="Rectangle"/>.</param>
    /// <param name="b">The second <see cref="Rectangle"/>.</param>
    /// <returns><c>true</c> if the instances are not equal; <c>false</c> otherwise.</returns>
    public static bool operator !=(Rectangle a, Rectangle b)
    {
        return !(a == b);
    }

    /// <summary>
    /// Gets whether the specified point lies within the bounds of this rectangle.
    /// </summary>
    /// <param name="point">The point to test.</param>
    /// <returns><c>true</c> if the point lies inside this rectangle; <c>false</c> otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(Vector2 point)
    {
        return (this.X <= point.X) && (point.X < this.Right) && (this.Y <= point.Y) && (point.Y < this.Bottom);
    }

    /// <summary>
    /// Gets whether the specified rectangle lies entirely within the bounds of this rectangle.
    /// </summary>
    /// <param name="other">The rectangle to test.</param>
    /// <returns><c>true</c> if the other rectangle lies entirely inside this rectangle; <c>false</c> otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(Rectangle other)
    {
        return !other.IsEmpty
            && (this.X <= other.X)
            && (other.Right <= this.Right)
            && (this.Y <= other.Y)
            && (other.Bottom <= this.Bottom);
    }

    /// <summary>
    /// Compares whether this instance is equal to the specified object.
    /// </summary>
    /// <param name="obj">The object to compare.</param>
    /// <returns><c>true</c> if the instances are equal; <c>false</c> otherwise.</returns>
    public override bool Equals(object? obj)
    {
        return (obj is Rectangle rectangle) && this.Equals(rectangle);
    }

    /// <summary>
    /// Compares whether this instance is equal to the specified <see cref="Rectangle"/>.
    /// </summary>
    /// <param name="other">The <see cref="Rectangle"/> to compare.</param>
    /// <returns><c>true</c> if the instances are equal; <c>false</c> otherwise.</returns>
    public bool Equals(Rectangle other)
    {
        return this.X.Equals(other.X) && this.Y.Equals(other.Y) && this.Width.Equals(other.Width) && this.Height.Equals(other.Height);
    }

    /// <summary>
    /// Gets the hash code of this rectangle.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        return HashCode.Combine(this.X, this.Y, this.Width, this.Height);
    }

    /// <summary>
    /// Gets a rectangle with its edges moved by the specified amounts, so its size changes by twice each one.
    /// Positive values expand it; negative values shrink it, to an empty one if they shrink it past nothing.
    /// </summary>
    /// <param name="rectangle">The rectangle, which stays as it is.</param>
    /// <param name="xPerSide">How far to move the left edge and the right edge, each.</param>
    /// <param name="yPerSide">How far to move the top edge and the bottom edge, each.</param>
    /// <returns>The adjusted rectangle.</returns>
    public static Rectangle Inflate(Rectangle rectangle, float xPerSide, float yPerSide)
    {
        return new Rectangle(
            rectangle.X - xPerSide,
            rectangle.Y - yPerSide,
            rectangle.Width + (xPerSide * 2f),
            rectangle.Height + (yPerSide * 2f));
    }

    /// <summary>
    /// Gets whether the specified rectangle intersects with this rectangle.
    /// </summary>
    /// <param name="other">The other rectangle to test.</param>
    /// <returns><c>true</c> if the rectangles intersect; <c>false</c> otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Intersects(Rectangle other)
    {
        return !this.IsEmpty
            && !other.IsEmpty
            && (other.X < this.Right)
            && (this.X < other.Right)
            && (other.Y < this.Bottom)
            && (this.Y < other.Bottom);
    }

    /// <summary>
    /// Creates a new rectangle representing the overlapping region of two rectangles.
    /// Returns the default rectangle if they do not intersect.
    /// </summary>
    /// <param name="a">The first rectangle.</param>
    /// <param name="b">The second rectangle.</param>
    /// <returns>The overlapping region of the two rectangles.</returns>
    public static Rectangle Intersection(Rectangle a, Rectangle b)
    {
        if (!a.Intersects(b))
        {
            return new Rectangle();
        }

        float left = MathF.Max(a.X, b.X);
        float top = MathF.Max(a.Y, b.Y);
        float right = MathF.Min(a.Right, b.Right);
        float bottom = MathF.Min(a.Bottom, b.Bottom);

        return new Rectangle(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Gets a rectangle moved by the specified amount.
    /// </summary>
    /// <param name="rectangle">The rectangle, which stays as it is.</param>
    /// <param name="amount">The offset.</param>
    /// <returns>The moved rectangle.</returns>
    public static Rectangle Offset(Rectangle rectangle, Vector2 amount)
    {
        return new Rectangle(rectangle.X + amount.X, rectangle.Y + amount.Y, rectangle.Width, rectangle.Height);
    }

    /// <summary>
    /// Returns a string representation of this rectangle.
    /// </summary>
    /// <returns>A string representation of this rectangle.</returns>
    public override string ToString()
    {
        return FormattableString.Invariant($"X: {this.X} Y: {this.Y} Width: {this.Width} Height: {this.Height}");
    }

    /// <summary>
    /// Creates a new rectangle that completely contains both specified rectangles. An empty one adds
    /// nothing, so a total can start from <c>default</c>.
    /// </summary>
    /// <param name="a">The first rectangle.</param>
    /// <param name="b">The second rectangle.</param>
    /// <returns>A new <see cref="Rectangle"/> that contains both input rectangles.</returns>
    public static Rectangle Union(Rectangle a, Rectangle b)
    {
        if (a.IsEmpty)
        {
            return b;
        }

        if (b.IsEmpty)
        {
            return a;
        }

        float left = MathF.Min(a.X, b.X);
        float top = MathF.Min(a.Y, b.Y);
        float right = MathF.Max(a.Right, b.Right);
        float bottom = MathF.Max(a.Bottom, b.Bottom);

        return new Rectangle(left, top, right - left, bottom - top);
    }
}
