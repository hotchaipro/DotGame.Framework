// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using Xunit;

namespace DotGame.Framework.Tests.Values;

/// <summary>
/// <see cref="Angle"/> and <see cref="Vector2Int"/>.
/// </summary>
public sealed class AngleAndVectorTests
{
    private const float Tolerance = 1e-4f;

    // ── Angle ──────────────────────────────────────────────────────────

    [Fact]
    public void Angle_ConvertsBetweenDegreesAndRadians()
    {
        Assert.Equal(MathF.PI, Angle.FromDegrees(180f).Radians, Tolerance);
        Assert.Equal(90f, Angle.FromRadians(MathF.PI / 2f).Degrees, Tolerance);
        Assert.Equal(-45f, Angle.FromDegrees(-45f).Degrees, Tolerance);
        Assert.Equal(0f, default(Angle).Radians);
    }

    [Fact]
    public void Angle_HasItsNamedAngles()
    {
        Assert.Equal(0f, Angle.Zero.Degrees, Tolerance);
        Assert.Equal(90f, Angle.QuarterCircle.Degrees, Tolerance);
        Assert.Equal(180f, Angle.HalfCircle.Degrees, Tolerance);
        Assert.Equal(360f, Angle.FullCircle.Degrees, Tolerance);
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 1f, 90f)]
    [InlineData(-1f, 0f, 180f)]
    [InlineData(0f, -1f, -90f)]
    [InlineData(1f, 1f, 45f)]
    [InlineData(-3f, -3f, -135f)]
    [InlineData(0f, 0f, 0f)]
    public void Angle_FromVector_IsItsDirection(
        float x,
        float y,
        float degrees)
    {
        Assert.Equal(degrees, Angle.FromVector(new Vector2(x, y)).Degrees, Tolerance);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(90f, 90f)]
    [InlineData(-90f, -90f)]
    [InlineData(270f, -90f)]
    [InlineData(-270f, 90f)]
    [InlineData(360f, 0f)]
    [InlineData(450f, 90f)]
    [InlineData(-450f, -90f)]
    [InlineData(1080f, 0f)]
    [InlineData(180f, 180f)]
    [InlineData(-180f, 180f)]
    public void Angle_Wrap_ComesBackToWithinHalfACircle(
        float degrees,
        float wrapped)
    {
        Assert.Equal(wrapped, Angle.Wrap(Angle.FromDegrees(degrees)).Degrees, 0.01f);
    }

    [Fact]
    public void Angle_Equals_TreatsNotANumberTheSameEitherWay()
    {
        var notANumber = Angle.FromRadians(float.NaN);

        var same = Angle.FromRadians(float.NaN);

        Assert.True(notANumber.Equals(same));
        Assert.True(notANumber.Equals((object)same));
        Assert.False(notANumber == same);
    }

    [Fact]
    public void Angle_Arithmetic()
    {
        var a = Angle.FromRadians(1f);
        var b = Angle.FromRadians(0.5f);

        Assert.Equal(1.5f, (a + b).Radians, Tolerance);
        Assert.Equal(0.5f, (a - b).Radians, Tolerance);
        Assert.Equal(2f, (a * 2).Radians, Tolerance);
        Assert.Equal(2f, (2 * a).Radians, Tolerance);
        Assert.Equal(0.25f, (a * 0.25f).Radians, Tolerance);
        Assert.Equal(0.25f, (0.25f * a).Radians, Tolerance);
        Assert.Equal(0.5f, (a / 2).Radians, Tolerance);
        Assert.Equal(4f, (a / 0.25f).Radians, Tolerance);
        Assert.Equal(-1f, (-a).Radians, Tolerance);
    }

    [Fact]
    public void Angle_ComparesAndEquates()
    {
        var small = Angle.FromRadians(1f);
        var large = Angle.FromRadians(2f);

        Assert.True(small < large);
        Assert.True(small <= large);
        Assert.True(small <= Angle.FromRadians(1f));
        Assert.True(large > small);
        Assert.True(large >= small);
        Assert.True(large >= Angle.FromRadians(2f));
        Assert.False(small > large);
        Assert.False(large < small);

        Assert.True(small == Angle.FromRadians(1f));
        Assert.True(small != large);
        Assert.True(small.Equals((object)Angle.FromRadians(1f)));
        Assert.False(small.Equals((object)large));
        Assert.False(small.Equals("not an angle"));
        Assert.Equal(small.GetHashCode(), Angle.FromRadians(1f).GetHashCode());
    }

    [Fact]
    public void Angle_IsEquatableWithoutBoxing()
    {
        object angle = Angle.Zero;

        Assert.True(angle is IEquatable<Angle>);
    }

    [Fact]
    public void Angle_ToString_DoesNotDependOnTheCulture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal("22.5°", Angle.FromDegrees(22.5f).ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    // ── Vector2Int ─────────────────────────────────────────────────────

    [Fact]
    public void Angle_SinCos_IsItsSineAndCosine()
    {
        var angle = Angle.FromDegrees(30);

        var (sin, cos) = angle.SinCos;

        Assert.Equal(angle.Sin, sin, Tolerance);
        Assert.Equal(angle.Cos, cos, Tolerance);
    }

    [Fact]
    public void Angle_ToVector_IsTheInverseOfFromVector()
    {
        var angle = Angle.FromDegrees(30);

        Assert.Equal(angle.Radians, Angle.FromVector(angle.ToVector()).Radians, Tolerance);
        Assert.Equal(MathF.Cos(angle.Radians), angle.Cos, Tolerance);
        Assert.Equal(MathF.Sin(angle.Radians), angle.Sin, Tolerance);
    }

    [Fact]
    public void Angle_Lerp_GoesPartway()
    {
        var angle = Angle.Lerp(Angle.FromDegrees(10), Angle.FromDegrees(30), 0.5f);

        Assert.Equal(20f, angle.Degrees, 0.01f);
    }

    [Theory]
    [InlineData(350f, 10f, 0.5f, 0f)]
    [InlineData(10f, 350f, 0.5f, 0f)]
    [InlineData(170f, -170f, 0.5f, 180f)]
    [InlineData(0f, 270f, 0.5f, -45f)]
    public void Angle_Lerp_GoesTheShortestWay(
        float from,
        float to,
        float amount,
        float expected)
    {
        var angle = Angle.Lerp(Angle.FromDegrees(from), Angle.FromDegrees(to), amount);

        Assert.Equal(Angle.Wrap(Angle.FromDegrees(expected)).Degrees, Angle.Wrap(angle).Degrees, 0.01f);
    }

    [Fact]
    public void Vector2Int_HasItsComponents()
    {
        var v = new Vector2Int(3, 4);

        Assert.Equal(3, v.X);
        Assert.Equal(4, v.Y);
        Assert.Equal(new Vector2Int(5, 5), new Vector2Int(5));
    }

    [Fact]
    public void Vector2Int_HasArithmetic()
    {
        var a = new Vector2Int(3, -4);
        var b = new Vector2Int(1, 2);

        Assert.Equal(new Vector2Int(4, -2), a + b);
        Assert.Equal(new Vector2Int(2, -6), a - b);
        Assert.Equal(new Vector2Int(-3, 4), -a);
        Assert.Equal(new Vector2Int(6, -8), a * 2);
        Assert.Equal(new Vector2Int(6, -8), 2 * a);
        Assert.Equal(new Vector2Int(1, -2), a / 2);

        Assert.Equal("X: 3 Y: -4", a.ToString());
    }

    [Fact]
    public void Vector2Int_NamedVectors_PointTheWayTheScreenDoes()
    {
        Assert.Equal(new Vector2Int(0, 0), Vector2Int.Zero);
        Assert.Equal(new Vector2Int(1, 1), Vector2Int.One);
    }

    [Fact]
    public void Vector2Int_Equates()
    {
        var v = new Vector2Int(3, 4);

        Assert.True(v == new Vector2Int(3, 4));
        Assert.False(v != new Vector2Int(3, 4));
        Assert.True(v != new Vector2Int(0, 4));
        Assert.True(v != new Vector2Int(3, 0));
        Assert.True(v.Equals(new Vector2Int(3, 4)));
        Assert.True(v.Equals((object)new Vector2Int(3, 4)));
        Assert.False(v.Equals((object)new Vector2Int(4, 3)));
        Assert.False(v.Equals("not a vector"));
        Assert.Equal(v.GetHashCode(), new Vector2Int(3, 4).GetHashCode());
        Assert.NotEqual(v.GetHashCode(), new Vector2Int(4, 3).GetHashCode());
    }

    [Fact]
    public void Vector2Int_WidensToAVector2()
    {
        Vector2 widened = new Vector2Int(-3, 4);

        Assert.Equal(new Vector2(-3f, 4f), widened);
    }
}
