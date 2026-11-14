// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework;

/// <summary>A position or size in whole numbers: pixels, usually. It's an immutable value, and converts implicitly to <c>Vector2</c>.</summary>
public readonly struct Vector2Int : IEquatable<Vector2Int>
{
    /// <summary>(0, 0).</summary>
    public static Vector2Int Zero => new(0, 0);
    /// <summary>(1, 1).</summary>
    public static Vector2Int One => new(1, 1);

    /// <summary>Makes a vector from its components.</summary>
    public Vector2Int(
        int x,
        int y)
    {
        this.X = x;
        this.Y = y;
    }

    /// <summary>Makes a vector with one value for both components.</summary>
    public Vector2Int(
        int xy)
    {
        this.X = xy;
        this.Y = xy;
    }

    /// <summary>The horizontal component.</summary>
    public int X { get; init; }

    /// <summary>The vertical component.</summary>
    public int Y { get; init; }

    /// <summary>The two added, component by component.</summary>
    public static Vector2Int operator +(Vector2Int a, Vector2Int b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>The second taken from the first, component by component.</summary>
    public static Vector2Int operator -(Vector2Int a, Vector2Int b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>The vector pointing the other way.</summary>
    public static Vector2Int operator -(Vector2Int value) => new(-value.X, -value.Y);

    /// <summary>The vector scaled.</summary>
    public static Vector2Int operator *(Vector2Int value, int scale) => new(value.X * scale, value.Y * scale);

    /// <summary>The vector scaled.</summary>
    public static Vector2Int operator *(int scale, Vector2Int value) => new(value.X * scale, value.Y * scale);

    /// <summary>The vector divided, each component rounding toward zero.</summary>
    public static Vector2Int operator /(Vector2Int value, int divisor) => new(value.X / divisor, value.Y / divisor);

    /// <inheritdoc/>
    public override string ToString() => FormattableString.Invariant($"X: {this.X} Y: {this.Y}");

    /// <summary>Whether two vectors have the same components.</summary>
    public static bool operator ==(Vector2Int a, Vector2Int b)
    {
        return (a.X == b.X) && (a.Y == b.Y);
    }

    /// <summary>Whether two vectors have different components.</summary>
    public static bool operator !=(Vector2Int a, Vector2Int b)
    {
        return (a.X != b.X) || (a.Y != b.Y);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        if (obj is Vector2Int v)
        {
            return this == v;
        }
        return false;
    }

    /// <summary>Whether this has the same components.</summary>
    public bool Equals(Vector2Int other)
    {
        return this == other;
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return HashCode.Combine(this.X, this.Y);
    }

    /// <summary>
    /// Converts a <see cref="Vector2Int"/> to a <see cref="Vector2"/> (widening conversion).
    /// </summary>
    public static implicit operator Vector2(Vector2Int other)
    {
        return new Vector2(other.X, other.Y);
    }
}
