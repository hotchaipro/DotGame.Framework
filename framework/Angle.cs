// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework;

/// <summary>An angle, stored in radians. It keeps degrees and radians apart: make one with <see cref="FromDegrees"/> or <see cref="FromRadians"/>, and read it with <see cref="Degrees"/> or <see cref="Radians"/>.</summary>
/// <remarks>
/// It's an amount, not a direction, so <c>==</c> and <c>&lt;</c> compare amounts: 360 degrees isn't equal to
/// 0. Use <see cref="Wrap"/> to compare directions, allowing for the rounding of floats.
/// </remarks>
public readonly struct Angle : IEquatable<Angle>
{
    /// <summary>No rotation.</summary>
    public static Angle Zero => new(0f);
    /// <summary>A whole turn: 360 degrees.</summary>
    public static Angle FullCircle => new(MathF.PI * 2);
    /// <summary>Half a turn: 180 degrees.</summary>
    public static Angle HalfCircle => new(MathF.PI);
    /// <summary>A quarter of a turn: 90 degrees.</summary>
    public static Angle QuarterCircle => new(MathF.PI / 2f);

    private readonly float _radians;

    private Angle(
        float radians)
    {
        this._radians = radians;
    }

    /// <summary>Makes an angle from degrees.</summary>
    public static Angle FromDegrees(
        float degrees)
    {
        return new Angle(float.DegreesToRadians(degrees));
    }

    /// <summary>Makes an angle from radians.</summary>
    public static Angle FromRadians(
        float radians)
    {
        return new Angle(radians);
    }

    /// <summary>The direction of a vector: 0 points along positive X, and a positive angle turns toward positive Y, which is clockwise on screen. A zero vector gives 0.</summary>
    public static Angle FromVector(
        Vector2 vector)
    {
        return new Angle(MathF.Atan2(vector.Y, vector.X));
    }

    /// <summary>
    /// Gets the angle partway between two angles, the shortest way round: from 350 to 10 degrees goes
    /// through 360. An amount outside 0 to 1 overshoots, which is meaningful for an angle. For a spin of
    /// more than half a turn, use <c>from + ((to - from) * amount)</c>.
    /// </summary>
    public static Angle Lerp(
        Angle from,
        Angle to,
        float amount)
    {
        return from + (Wrap(to - from) * amount);
    }

    /// <summary>
    /// Gets the angle's sine.
    /// </summary>
    public float Sin => MathF.Sin(this._radians);

    /// <summary>
    /// Gets the angle's cosine.
    /// </summary>
    public float Cos => MathF.Cos(this._radians);

    /// <summary>
    /// Gets the angle's sine and cosine, which together cost less than asking for each:
    /// <c>var (sin, cos) = angle.SinCos;</c>
    /// </summary>
    public (float Sin, float Cos) SinCos => MathF.SinCos(this._radians);

    /// <summary>
    /// Gets the unit vector pointing at this angle, the inverse of <see cref="FromVector"/>.
    /// </summary>
    public Vector2 ToVector()
    {
        (float sin, float cos) = this.SinCos;
        return new Vector2(cos, sin);
    }

    /// <summary>The same direction, with its value brought above -180 degrees and up to 180.</summary>
    public static Angle Wrap(
        Angle angle)
    {
        float wrapped = MathF.IEEERemainder(angle._radians, MathF.Tau);
        return new Angle((wrapped <= -MathF.PI) ? MathF.PI : wrapped);
    }

    /// <summary>The angle in degrees.</summary>
    public float Degrees => float.RadiansToDegrees(this._radians);

    /// <summary>The angle in radians.</summary>
    public float Radians => this._radians;

    /// <inheritdoc/>
    public override bool Equals(
        object? obj)
    {
        return (obj is Angle other) && this.Equals(other);
    }

    /// <summary>Whether this is the same amount. Unlike <c>==</c>, not-a-number equals itself, as for a <see cref="float"/>.</summary>
    public bool Equals(
        Angle other)
    {
        return this._radians.Equals(other._radians);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return this._radians.GetHashCode();
    }

    /// <summary>Whether two amounts are the same. 360 degrees isn't equal to 0: see <see cref="Wrap"/>.</summary>
    public static bool operator ==(Angle a, Angle b)
    {
        return (a._radians == b._radians);
    }

    /// <summary>Whether two amounts differ.</summary>
    public static bool operator !=(Angle a, Angle b)
    {
        return (a._radians != b._radians);
    }

    /// <summary>The two angles added.</summary>
    public static Angle operator +(Angle a, Angle b)
    {
        return new Angle(a._radians + b._radians);
    }

    /// <summary>The second angle taken from the first.</summary>
    public static Angle operator -(Angle a, Angle b)
    {
        return new Angle(a._radians - b._radians);
    }

    /// <summary>The angle scaled.</summary>
    public static Angle operator *(Angle a, float b)
    {
        return new Angle(a._radians * b);
    }

    /// <summary>The angle scaled.</summary>
    public static Angle operator *(float b, Angle a)
    {
        return new Angle(a._radians * b);
    }

    /// <summary>The angle divided.</summary>
    public static Angle operator /(Angle a, float b)
    {
        return new Angle(a._radians / b);
    }

    /// <summary>The angle the other way round.</summary>
    public static Angle operator -(Angle a)
    {
        return new Angle(-a._radians);
    }

    /// <summary>Whether the first amount is larger.</summary>
    public static bool operator >(Angle a, Angle b)
    {
        return a._radians > b._radians;
    }

    /// <summary>Whether the first amount is larger or the same.</summary>
    public static bool operator >=(Angle a, Angle b)
    {
        return a._radians >= b._radians;
    }

    /// <summary>Whether the first amount is smaller.</summary>
    public static bool operator <(Angle a, Angle b)
    {
        return a._radians < b._radians;
    }

    /// <summary>Whether the first amount is smaller or the same.</summary>
    public static bool operator <=(Angle a, Angle b)
    {
        return a._radians <= b._radians;
    }

    /// <summary>The angle in degrees, such as <c>90°</c>.</summary>
    public override string ToString()
    {
        return FormattableString.Invariant($"{this.Degrees}°");
    }
}
