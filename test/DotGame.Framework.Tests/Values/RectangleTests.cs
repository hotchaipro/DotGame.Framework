// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using Xunit;

namespace DotGame.Framework.Tests.Values;

/// <summary>
/// <see cref="Rectangle"/> and <see cref="RectangleInt"/>, which keep the same rules: a rectangle
/// holds its left and top edges and not its right and bottom ones, so neighbors don't overlap.
/// </summary>
public sealed class RectangleTests
{
    // ── Rectangle ──────────────────────────────────────────────────────

    [Fact]
    public void Rectangle_HasItsEdgesAndCenter()
    {
        var r = new Rectangle(10f, 20f, 30f, 40f);

        Assert.Equal(10f, r.Left);
        Assert.Equal(40f, r.Right);
        Assert.Equal(20f, r.Top);
        Assert.Equal(60f, r.Bottom);
        Assert.Equal(new Vector2(25f, 40f), r.Center);
        Assert.Equal(new Vector2(10f, 20f), r.Position);
        Assert.Equal(new Vector2(30f, 40f), r.Size);
        Assert.Equal(r, new Rectangle(new Vector2(10f, 20f), new Vector2(30f, 40f)));
    }

    [Fact]
    public void Rectangle_PositionAndSize_CanBeSet()
    {
        var r = new Rectangle(10f, 20f, 30f, 40f)
        {
            Position = new Vector2(1f, 2f),
            Size = new Vector2(3f, 4f),
        };

        Assert.Equal(new Rectangle(1f, 2f, 3f, 4f), r);
    }

    [Fact]
    public void Rectangle_IsEmpty_WhenItHasNoArea_WhereverItIs()
    {
        Assert.True(default(Rectangle).IsEmpty);
        Assert.True(new Rectangle(10f, 10f, 0f, 0f).IsEmpty);
        Assert.True(new Rectangle(0f, 0f, 1f, 0f).IsEmpty);
        Assert.True(new Rectangle(0f, 0f, 0f, 1f).IsEmpty);
        Assert.True(new Rectangle(0f, 0f, -1f, 5f).IsEmpty);
        Assert.False(new Rectangle(0f, 0f, 1f, 1f).IsEmpty);
    }

    [Fact]
    public void Rectangle_With_ChangesOnlyWhatItNames()
    {
        var r = new Rectangle(1f, 2f, 3f, 4f);

        Assert.Equal(new Rectangle(9f, 2f, 3f, 4f), r with { X = 9f });
        Assert.Equal(new Rectangle(7f, 8f, 3f, 4f), r with { Position = new Vector2(7f, 8f) });
        Assert.Equal(new Rectangle(1f, 2f, 5f, 6f), r with { Size = new Vector2(5f, 6f) });
    }

    [Fact]
    public void Rectangle_OffsetAndInflate_LeaveTheOriginalAlone()
    {
        var r = new Rectangle(1f, 2f, 3f, 4f);

        _ = Rectangle.Offset(r, new Vector2(5f, 5f));
        _ = Rectangle.Inflate(r, 5f, 5f);

        Assert.Equal(new Rectangle(1f, 2f, 3f, 4f), r);
    }

    [Theory]
    [InlineData(10f, 20f, true)] // Top-left, in
    [InlineData(25f, 40f, true)]
    [InlineData(39.99f, 59.99f, true)]
    [InlineData(40f, 20f, false)] // Right edge, out
    [InlineData(10f, 60f, false)] // Bottom edge, out
    [InlineData(9.99f, 20f, false)]
    [InlineData(10f, 19.99f, false)]
    public void Rectangle_Contains_APoint(
        float x,
        float y,
        bool expected)
    {
        var r = new Rectangle(10f, 20f, 30f, 40f);

        Assert.Equal(expected, r.Contains(new Vector2(x, y)));
    }

    [Fact]
    public void Rectangle_Contains_ARectangle_IncludingItself()
    {
        var r = new Rectangle(10f, 20f, 30f, 40f);

        Assert.True(r.Contains(r));
        Assert.True(r.Contains(new Rectangle(11f, 21f, 1f, 1f)));
        Assert.False(r.Contains(new Rectangle(9f, 21f, 2f, 1f)));
        Assert.False(r.Contains(new Rectangle(39f, 21f, 2f, 1f)));
        Assert.False(r.Contains(new Rectangle(11f, 19f, 1f, 2f)));
        Assert.False(r.Contains(new Rectangle(11f, 59f, 1f, 2f)));
    }

    public static TheoryData<float, float, float, float, bool> Overlaps => new()
    {
        { 5f, 5f, 10f, 10f, true }, // Partly over a corner
        { 2f, 2f, 1f, 1f, true }, // Inside
        { -5f, -5f, 100f, 100f, true }, // Around
        { 10f, 0f, 5f, 10f, false }, // Beside, sharing an edge
        { 0f, 10f, 10f, 5f, false }, // Below, sharing an edge
        { -5f, 0f, 5f, 10f, false }, // To the left, sharing an edge
        { 0f, -5f, 10f, 5f, false }, // Above, sharing an edge
        { 20f, 20f, 5f, 5f, false }, // Apart
    };

    [Theory]
    [MemberData(nameof(Overlaps))]
    public void Rectangle_Intersects_BothWays(
        float x,
        float y,
        float width,
        float height,
        bool expected)
    {
        var a = new Rectangle(0f, 0f, 10f, 10f);
        var b = new Rectangle(x, y, width, height);

        Assert.Equal(expected, a.Intersects(b));
        Assert.Equal(expected, b.Intersects(a));
    }

    [Fact]
    public void Rectangle_Intersect_IsTheOverlap_OrEmpty()
    {
        var a = new Rectangle(0f, 0f, 10f, 10f);

        Assert.Equal(new Rectangle(5f, 6f, 5f, 4f), Rectangle.Intersection(a, new Rectangle(5f, 6f, 20f, 20f)));
        Assert.Equal(new Rectangle(5f, 6f, 5f, 4f), Rectangle.Intersection(new Rectangle(5f, 6f, 20f, 20f), a));
        Assert.Equal(a, Rectangle.Intersection(a, a));
        Assert.Equal(default, Rectangle.Intersection(a, new Rectangle(10f, 0f, 5f, 5f)));
        Assert.Equal(default, Rectangle.Intersection(a, new Rectangle(50f, 50f, 5f, 5f)));
    }

    [Fact]
    public void Rectangle_Union_HoldsBoth()
    {
        var union = Rectangle.Union(new Rectangle(0f, 0f, 2f, 2f), new Rectangle(5f, 6f, 2f, 2f));

        Assert.Equal(new Rectangle(0f, 0f, 7f, 8f), union);
        Assert.Equal(union, Rectangle.Union(new Rectangle(5f, 6f, 2f, 2f), new Rectangle(0f, 0f, 2f, 2f)));
    }

    [Fact]
    public void Rectangle_Inflate_MovesEachEdgeOut()
    {
        var r = new Rectangle(10f, 20f, 30f, 40f);

        r = Rectangle.Inflate(r, 1f, 2f);
        Assert.Equal(new Rectangle(9f, 18f, 32f, 44f), r);

        r = Rectangle.Inflate(r, -1f, -2f);
        Assert.Equal(new Rectangle(10f, 20f, 30f, 40f), r);
    }

    [Fact]
    public void Rectangle_Offset_Moves()
    {
        var r = new Rectangle(10f, 20f, 30f, 40f);

        r = Rectangle.Offset(r, new Vector2(1f, 2f));
        Assert.Equal(new Rectangle(11f, 22f, 30f, 40f), r);

        r = Rectangle.Offset(r, new Vector2(-1f, -2f));
        Assert.Equal(new Rectangle(10f, 20f, 30f, 40f), r);
    }

    [Fact]
    public void Rectangle_Equality_ComparesEveryField()
    {
        var r = new Rectangle(1f, 2f, 3f, 4f);

        Assert.True(r == new Rectangle(1f, 2f, 3f, 4f));
        Assert.False(r != new Rectangle(1f, 2f, 3f, 4f));
        Assert.True(r.Equals(new Rectangle(1f, 2f, 3f, 4f)));
        Assert.True(r.Equals((object)new Rectangle(1f, 2f, 3f, 4f)));
        Assert.False(r.Equals("not a rectangle"));
        Assert.Equal(r.GetHashCode(), new Rectangle(1f, 2f, 3f, 4f).GetHashCode());

        Assert.True(r != new Rectangle(0f, 2f, 3f, 4f));
        Assert.True(r != new Rectangle(1f, 0f, 3f, 4f));
        Assert.True(r != new Rectangle(1f, 2f, 0f, 4f));
        Assert.True(r != new Rectangle(1f, 2f, 3f, 0f));
    }

    [Fact]
    public void Rectangle_PrintsInAnyCulture()
    {
        var r = new Rectangle(1.5f, 2f, 3f, 4f);

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal("X: 1.5 Y: 2 Width: 3 Height: 4", r.ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    // ── RectangleInt ───────────────────────────────────────────────────

    [Fact]
    public void RectangleInt_HasItsEdgesAndCenter()
    {
        var r = new RectangleInt(10, 20, 31, 41);

        Assert.Equal(10, r.Left);
        Assert.Equal(41, r.Right);
        Assert.Equal(20, r.Top);
        Assert.Equal(61, r.Bottom);
        Assert.Equal(new Vector2(25.5f, 40.5f), r.Center);
        Assert.Equal(new Vector2Int(10, 20), r.Position);
        Assert.Equal(new Vector2Int(31, 41), r.Size);
        Assert.Equal(r, new RectangleInt(new Vector2Int(10, 20), new Vector2Int(31, 41)));
    }

    [Fact]
    public void RectangleInt_PositionAndSize_CanBeSet()
    {
        var r = new RectangleInt(10, 20, 30, 40)
        {
            Position = new Vector2Int(1, 2),
            Size = new Vector2Int(3, 4),
        };

        Assert.Equal(new RectangleInt(1, 2, 3, 4), r);
    }

    [Fact]
    public void RectangleInt_IsEmpty_WhenItHasNoArea_WhereverItIs()
    {
        Assert.True(default(RectangleInt).IsEmpty);
        Assert.True(new RectangleInt(10, 10, 0, 0).IsEmpty);
        Assert.True(new RectangleInt(0, 0, 1, 0).IsEmpty);
        Assert.True(new RectangleInt(0, 0, 0, 1).IsEmpty);
        Assert.True(new RectangleInt(0, 0, -1, 5).IsEmpty);
        Assert.False(new RectangleInt(0, 0, 1, 1).IsEmpty);
    }

    [Theory]
    [InlineData(10, 20, true)]
    [InlineData(39, 59, true)]
    [InlineData(40, 20, false)]
    [InlineData(10, 60, false)]
    [InlineData(9, 20, false)]
    [InlineData(10, 19, false)]
    public void RectangleInt_Contains_APoint(
        int x,
        int y,
        bool expected)
    {
        var r = new RectangleInt(10, 20, 30, 40);

        Assert.Equal(expected, r.Contains(new Vector2(x, y)));
    }

    [Fact]
    public void RectangleInt_Contains_AFractionalPoint()
    {
        var r = new RectangleInt(10, 20, 30, 40);

        Assert.True(r.Contains(new Vector2(39.5f, 59.5f)));
        Assert.False(r.Contains(new Vector2(9.5f, 20f)));
    }

    [Fact]
    public void RectangleInt_Contains_ARectangle()
    {
        var r = new RectangleInt(10, 20, 30, 40);

        Assert.True(r.Contains(r));
        Assert.True(r.Contains(new RectangleInt(11, 21, 1, 1)));
        Assert.False(r.Contains(new RectangleInt(9, 21, 2, 1)));
        Assert.False(r.Contains(new RectangleInt(39, 21, 2, 1)));
    }

    public static TheoryData<int, int, int, int, bool> OverlapsInt => new()
    {
        { 5, 5, 10, 10, true },
        { 2, 2, 1, 1, true },
        { -5, -5, 100, 100, true },
        { 10, 0, 5, 10, false },
        { 0, 10, 10, 5, false },
        { -5, 0, 5, 10, false },
        { 0, -5, 10, 5, false },
        { 20, 20, 5, 5, false },
    };

    [Theory]
    [MemberData(nameof(OverlapsInt))]
    public void RectangleInt_Intersects_BothWays(
        int x,
        int y,
        int width,
        int height,
        bool expected)
    {
        var a = new RectangleInt(0, 0, 10, 10);
        var b = new RectangleInt(x, y, width, height);

        Assert.Equal(expected, a.Intersects(b));
        Assert.Equal(expected, b.Intersects(a));
    }

    [Fact]
    public void RectangleInt_Intersect_IsTheOverlap_OrEmpty()
    {
        var a = new RectangleInt(0, 0, 10, 10);

        Assert.Equal(new RectangleInt(5, 6, 5, 4), RectangleInt.Intersection(a, new RectangleInt(5, 6, 20, 20)));
        Assert.Equal(a, RectangleInt.Intersection(a, a));
        Assert.Equal(default, RectangleInt.Intersection(a, new RectangleInt(10, 0, 5, 5)));
    }

    [Fact]
    public void RectangleInt_Union_HoldsBoth()
    {
        Assert.Equal(new RectangleInt(0, 0, 7, 8), RectangleInt.Union(new RectangleInt(0, 0, 2, 2), new RectangleInt(5, 6, 2, 2)));
    }

    [Fact]
    public void RectangleInt_InflateAndOffset_MoveItsEdges()
    {
        var r = new RectangleInt(10, 20, 30, 40);

        r = RectangleInt.Inflate(r, 1, 2);
        Assert.Equal(new RectangleInt(9, 18, 32, 44), r);

        r = RectangleInt.Inflate(r, -1, -2);
        r = RectangleInt.Offset(r, new Vector2Int(1, 2));
        Assert.Equal(new RectangleInt(11, 22, 30, 40), r);

        r = RectangleInt.Offset(r, new Vector2Int(-1, -2));
        Assert.Equal(new RectangleInt(10, 20, 30, 40), r);
    }

    [Fact]
    public void RectangleInt_Equality_ComparesEveryField()
    {
        var r = new RectangleInt(1, 2, 3, 4);

        Assert.True(r == new RectangleInt(1, 2, 3, 4));
        Assert.False(r != new RectangleInt(1, 2, 3, 4));
        Assert.True(r.Equals(new RectangleInt(1, 2, 3, 4)));
        Assert.True(r.Equals((object)new RectangleInt(1, 2, 3, 4)));
        Assert.False(r.Equals(new Rectangle(1f, 2f, 3f, 4f)));
        Assert.Equal(r.GetHashCode(), new RectangleInt(1, 2, 3, 4).GetHashCode());

        Assert.True(r != new RectangleInt(0, 2, 3, 4));
        Assert.True(r != new RectangleInt(1, 0, 3, 4));
        Assert.True(r != new RectangleInt(1, 2, 0, 4));
        Assert.True(r != new RectangleInt(1, 2, 3, 0));
    }

    [Fact]
    public void RectangleInt_Prints_AndWidensToARectangle()
    {
        var r = new RectangleInt(1, 2, 3, 4);

        Assert.Equal("X: 1 Y: 2 Width: 3 Height: 4", r.ToString());

        Rectangle widened = r;
        Assert.Equal(new Rectangle(1f, 2f, 3f, 4f), widened);
    }

    // ── An empty rectangle is nothing ──────────────────────────────────

    public static TheoryData<float, float, float, float> EmptyRectangles => new()
    {
        { 5f, 5f, 0f, 0f },
        { 5f, 5f, 2f, 0f },
        { 5f, 5f, 0f, 2f },
        { 5f, 5f, -2f, 2f },
        { 5f, 5f, 2f, -2f },
        { 5f, 5f, -2f, -2f },
        { 5f, 5f, float.NaN, 2f },
        { 5f, 5f, 2f, float.NaN },
    };

    [Theory]
    [MemberData(nameof(EmptyRectangles))]
    public void Rectangle_WhenEmpty_OverlapsAndContainsNothing(
        float x,
        float y,
        float width,
        float height)
    {
        var empty = new Rectangle(x, y, width, height);
        var around = new Rectangle(0f, 0f, 10f, 10f);

        Assert.True(empty.IsEmpty);
        Assert.False(around.Intersects(empty));
        Assert.False(empty.Intersects(around));
        Assert.False(around.Contains(empty));
        Assert.False(empty.Contains(around));
        Assert.False(empty.Contains(new Vector2(4f, 4f)));
        Assert.True(Rectangle.Intersection(around, empty) == default);
    }

    [Theory]
    [MemberData(nameof(EmptyRectangles))]
    public void Rectangle_Union_IgnoresAnEmptyOne_EitherWay(
        float x,
        float y,
        float width,
        float height)
    {
        var empty = new Rectangle(x, y, width, height);
        var real = new Rectangle(10f, 10f, 5f, 5f);

        Assert.Equal(real, Rectangle.Union(empty, real));
        Assert.Equal(real, Rectangle.Union(real, empty));
    }

    [Fact]
    public void Rectangle_Inflate_PastNothing_IsEmpty()
    {
        Assert.True(Rectangle.Inflate(new Rectangle(0f, 0f, 10f, 10f), -6f, -6f).IsEmpty);
    }

    [Fact]
    public void Rectangle_FromCorners_IsTheSame_InAnyDirection()
    {
        var expected = new Rectangle(1f, 2f, 3f, 4f);

        Assert.Equal(expected, Rectangle.FromCorners(new Vector2(1f, 2f), new Vector2(4f, 6f)));
        Assert.Equal(expected, Rectangle.FromCorners(new Vector2(4f, 6f), new Vector2(1f, 2f)));
        Assert.Equal(expected, Rectangle.FromCorners(new Vector2(4f, 2f), new Vector2(1f, 6f)));
        Assert.Equal(expected, Rectangle.FromCorners(new Vector2(1f, 6f), new Vector2(4f, 2f)));
        Assert.True(Rectangle.FromCorners(new Vector2(3f, 3f), new Vector2(3f, 3f)).IsEmpty);
    }

    [Theory]
    [InlineData(5, 5, 0, 0)]
    [InlineData(5, 5, 2, 0)]
    [InlineData(5, 5, -2, 2)]
    [InlineData(5, 5, -2, -2)]
    public void RectangleInt_WhenEmpty_IsNothing(
        int x,
        int y,
        int width,
        int height)
    {
        var empty = new RectangleInt(x, y, width, height);
        var around = new RectangleInt(0, 0, 10, 10);
        var real = new RectangleInt(10, 10, 5, 5);

        Assert.False(around.Intersects(empty));
        Assert.False(empty.Intersects(around));
        Assert.False(around.Contains(empty));
        Assert.True(RectangleInt.Intersection(around, empty) == default);
        Assert.Equal(real, RectangleInt.Union(empty, real));
        Assert.Equal(real, RectangleInt.Union(real, empty));
    }

    [Fact]
    public void RectangleInt_FromCorners_IsTheSame_InAnyDirection()
    {
        var expected = new RectangleInt(1, 2, 3, 4);

        Assert.Equal(expected, RectangleInt.FromCorners(new Vector2Int(1, 2), new Vector2Int(4, 6)));
        Assert.Equal(expected, RectangleInt.FromCorners(new Vector2Int(4, 6), new Vector2Int(1, 2)));
    }
}
