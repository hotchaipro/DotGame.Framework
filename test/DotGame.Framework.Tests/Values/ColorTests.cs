// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using Xunit;

namespace DotGame.Framework.Tests.Values;

/// <summary>
/// <see cref="Color"/> and <see cref="Color32"/>.
/// </summary>
public sealed class ColorTests
{
    private const float Tolerance = 1e-3f;

    private static void AssertColor(
        float r,
        float g,
        float b,
        float a,
        Color actual)
    {
        Assert.Equal(r, actual.R, Tolerance);
        Assert.Equal(g, actual.G, Tolerance);
        Assert.Equal(b, actual.B, Tolerance);
        Assert.Equal(a, actual.A, Tolerance);
    }

    // ── Constructing ───────────────────────────────────────────────────

    [Fact]
    public void Construction_SaysWhatItTakes()
    {
        AssertColor(0.25f, 0.5f, 0.75f, 0.1f, Color.FromPremultiplied(0.25f, 0.5f, 0.75f, 0.1f));
        AssertColor(0.25f, 0.5f, 0.75f, 1f, new Color(0.25f, 0.5f, 0.75f));
        AssertColor(1f, 0f, 51f / 255f, 1f, Color.FromRgb(0xFF0033));
        AssertColor(0.5f, 0f, 0f, 0.5f, new Color(1f, 0f, 0f, 0.5f));
    }

    [Fact]
    public void FromRgb_WithAnAlpha_IsNotPremultiplied()
    {
        float alpha = 128 / 255f;

        AssertColor(alpha, 0f, 0f, alpha, Color.FromRgb(0xFF0000, 128 / 255f));
    }

    [Fact]
    public void FromRgb_TakesAnAlpha_AndRefusesOneOutOfRange()
    {
        Assert.Equal(new Color(0x12 / 255f, 0x34 / 255f, 0x56 / 255f, 0.5f), Color.FromRgb(0x123456, 0.5f));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Color.FromRgb(0x123456, 128));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Color.FromRgb(0x123456, -0.1f));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Color.FromRgb(0x123456, float.NaN));
    }

    [Fact]
    public void WithAlpha_SetsIt_AndMultiplyingFades()
    {
        var translucent = new Color(1f, 0f, 0f, 0.4f);

        AssertColor(0.5f, 0f, 0f, 0.5f, translucent.WithAlpha(0.5f));
        AssertColor(0.2f, 0f, 0f, 0.2f, translucent * 0.5f);
        AssertColor(0f, 0f, 0f, 0.6f, Color.Black.WithAlpha(0.6f));
        AssertColor(0.25f, 0.25f, 0.25f, 0.25f, Color.White.WithAlpha(0.25f));
    }

    [Fact]
    public void WithAlpha_OfTransparent_IsBlack_AndOfAnAdditiveColor_Throws()
    {
        AssertColor(0f, 0f, 0f, 0.5f, Color.Transparent.WithAlpha(0.5f));
        Assert.Throws<System.InvalidOperationException>(() => Color.FromPremultiplied(1f, 0.5f, 0f, 0f).WithAlpha(0.5f));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Color.White.WithAlpha(1.5f));
    }

    [Fact]
    public void WholeNumbers_InTheConstructor_DoNotCompile()
    {
        var constructor = typeof(Color).GetConstructor([typeof(int), typeof(int), typeof(int), typeof(int)]);
        var attribute = (System.ObsoleteAttribute)System.Attribute.GetCustomAttribute(constructor!, typeof(System.ObsoleteAttribute))!;

        Assert.True(attribute.IsError);
    }

    [Fact]
    public void FromRgb_ReadsANumber_AndRefusesOneWithAlpha()
    {
        Assert.Equal(Color.FromRgb(0x123456), Color.FromRgb(0x123456));
        Assert.Equal(Color.White, Color.FromRgb(0xFFFFFF));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Color.FromRgb(0x80123456));
    }

    [Theory]
    [InlineData(0xFF, 0xFF, 0xFF, nameof(Color.White))]
    [InlineData(0x00, 0x00, 0x00, nameof(Color.Black))]
    [InlineData(0x80, 0x80, 0x80, nameof(Color.Gray))]
    [InlineData(0xFF, 0x00, 0x00, nameof(Color.Red))]
    [InlineData(0x00, 0xFF, 0x00, nameof(Color.Green))]
    [InlineData(0x00, 0x00, 0xFF, nameof(Color.Blue))]
    [InlineData(0xFF, 0xFF, 0x00, nameof(Color.Yellow))]
    [InlineData(0x00, 0xFF, 0xFF, nameof(Color.Cyan))]
    [InlineData(0xFF, 0x00, 0xFF, nameof(Color.Magenta))]
    public void NamedColors_AreWhatTheySay(
        int r,
        int g,
        int b,
        string name)
    {
        var actual = name switch
        {
            nameof(Color.White) => Color.White,
            nameof(Color.Black) => Color.Black,
            nameof(Color.Gray) => Color.Gray,
            nameof(Color.Red) => Color.Red,
            nameof(Color.Green) => Color.Green,
            nameof(Color.Blue) => Color.Blue,
            nameof(Color.Yellow) => Color.Yellow,
            nameof(Color.Cyan) => Color.Cyan,
            _ => Color.Magenta,
        };

        Assert.Equal(new Color(((byte)r) / 255f, ((byte)g) / 255f, ((byte)b) / 255f), actual);
    }

    [Fact]
    public void Transparent_HasNoAlpha_AndOtherNamedColorsAreOpaque()
    {
        Assert.Equal(0f, Color.Transparent.A);
        Assert.Equal(1f, Color.Red.A);
    }

    // ── Arithmetic ─────────────────────────────────────────────────────

    [Fact]
    public void Lerp_ClampsItsAmount()
    {
        var from = Color.FromPremultiplied(0f, 0.5f, 1f, 0f);
        var to = Color.FromPremultiplied(1f, 0.5f, 0f, 1f);

        Assert.Equal(from, Color.Lerp(from, to, 0f));
        Assert.Equal(to, Color.Lerp(from, to, 1f));
        AssertColor(0.25f, 0.5f, 0.75f, 0.25f, Color.Lerp(from, to, 0.25f));
        Assert.Equal(from, Color.Lerp(from, to, -1f));
        Assert.Equal(to, Color.Lerp(from, to, 2f));
    }

    [Fact]
    public void Multiplying_ScalesEveryComponent()
    {
        var color = Color.FromPremultiplied(0.2f, 0.4f, 0.6f, 0.8f);

        AssertColor(0.1f, 0.2f, 0.3f, 0.4f, color * 0.5f);
        AssertColor(0.1f, 0.2f, 0.3f, 0.4f, 0.5f * color);
        AssertColor(0.04f, 0.16f, 0.36f, 0.64f, color * color);
    }

    [Fact]
    public void FromRgba_MultipliesByAlpha()
    {
        AssertColor(0.5f, 0.25f, 0f, 0.5f, new Color(1f, 0.5f, 0f, 0.5f));
    }

    // ── Conversions ────────────────────────────────────────────────────

    [Fact]
    public void ToString_DoesNotDependOnTheCulture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal("R: 0.5 G: 0.25 B: 0 A: 1", new Color(0.5f, 0.25f, 0f).ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    // ── Equality ───────────────────────────────────────────────────────

    [Fact]
    public void Equality_ComparesEveryComponent()
    {
        var color = Color.FromPremultiplied(0.1f, 0.2f, 0.3f, 0.4f);

        Assert.True(color == Color.FromPremultiplied(0.1f, 0.2f, 0.3f, 0.4f));
        Assert.False(color != Color.FromPremultiplied(0.1f, 0.2f, 0.3f, 0.4f));
        Assert.True(color.Equals(Color.FromPremultiplied(0.1f, 0.2f, 0.3f, 0.4f)));
        Assert.True(color.Equals((object)Color.FromPremultiplied(0.1f, 0.2f, 0.3f, 0.4f)));
        Assert.False(color.Equals("not a color"));

        Assert.True(color != Color.FromPremultiplied(0f, 0.2f, 0.3f, 0.4f));
        Assert.True(color != Color.FromPremultiplied(0.1f, 0f, 0.3f, 0.4f));
        Assert.True(color != Color.FromPremultiplied(0.1f, 0.2f, 0f, 0.4f));
        Assert.True(color != Color.FromPremultiplied(0.1f, 0.2f, 0.3f, 0f));
    }

    [Fact]
    public void EqualColors_HaveEqualHashes_EvenWithNegativeZero()
    {
        Assert.Equal(Color.FromPremultiplied(0.1f, 0.2f, 0.3f, 0.4f).GetHashCode(), Color.FromPremultiplied(0.1f, 0.2f, 0.3f, 0.4f).GetHashCode());

        Assert.True(Color.FromPremultiplied(0f, 0f, 0f, 0f) == Color.FromPremultiplied(-0f, -0f, -0f, -0f));
        Assert.Equal(Color.FromPremultiplied(0f, 0f, 0f, 0f).GetHashCode(), Color.FromPremultiplied(-0f, -0f, -0f, -0f).GetHashCode());
    }

    // ── Color32 ────────────────────────────────────────────────────────

    [Fact]
    public void EveryByte_RoundTripsThroughColor()
    {
        for (int i = 0; i <= 255; i++)
        {
            byte value = (byte)i;
            var original = Color32.FromPremultiplied(value, value, value, value);

            Color32 back = original.ToColor().ToColor32();

            Assert.Equal(original, back);
        }
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 255)]
    [InlineData(0.5f, 128)]
    [InlineData(0.25f, 64)]
    [InlineData(0.749f, 191)]
    public void ToColor32_RoundsToTheNearestByte(
        float component,
        byte expected)
    {
        Color32 converted = Color.FromPremultiplied(component, component, component, component).ToColor32();

        Assert.Equal(Color32.FromPremultiplied(expected, expected, expected, expected), converted);
    }

    [Fact]
    public void ToColor32_ClampsComponentsOutOfRange()
    {
        Color32 converted = Color.FromPremultiplied(1.5f, -0.5f, 2f, 300f).ToColor32();

        Assert.Equal(Color32.FromPremultiplied(255, 0, 255, 255), converted);
    }

    [Fact]
    public void FromColor32_Divides()
    {
        AssertColor(1f, 0f, 0.2f, 0.4f, Color32.FromPremultiplied(255, 0, 51, 102).ToColor());
    }

    [Fact]
    public void Color32_FromRgba_MultipliesByAlpha_AndFromPremultipliedKeepsItsBytes()
    {
        Assert.Equal(Color32.FromPremultiplied(255, 128, 0, 255), Color32.FromRgba(255, 128, 0, 255));
        Assert.Equal(Color32.FromPremultiplied(128, 0, 0, 128), Color32.FromRgba(255, 0, 0, 128));
        Assert.Equal(Color32.FromPremultiplied(0, 0, 0, 0), Color32.FromRgba(255, 255, 255, 0));

        var kept = Color32.FromPremultiplied(255, 0, 51, 102);
        Assert.Equal((byte)255, kept.R);
        Assert.Equal((byte)102, kept.A);
    }

    [Fact]
    public void Color_AndColor32_FromTheSameStraightBytes_Agree()
    {
        Assert.Equal(Color32.FromRgba(255, 128, 0, 128), Color.FromRgb(0xFF8000, 128 / 255f).ToColor32());
    }

    [Fact]
    public void Color32_Equality_ComparesEveryComponent()
    {
        var color = Color32.FromPremultiplied(1, 2, 3, 4);

        Assert.True(color == Color32.FromPremultiplied(1, 2, 3, 4));
        Assert.False(color != Color32.FromPremultiplied(1, 2, 3, 4));
        Assert.True(color.Equals((object)Color32.FromPremultiplied(1, 2, 3, 4)));
        Assert.False(color.Equals("not a color"));
        Assert.True(color != Color32.FromPremultiplied(0, 2, 3, 4));
        Assert.True(color != Color32.FromPremultiplied(1, 0, 3, 4));
        Assert.True(color != Color32.FromPremultiplied(1, 2, 0, 4));
        Assert.True(color != Color32.FromPremultiplied(1, 2, 3, 0));
    }

    [Fact]
    public void Color32_Hash_TellsEveryComponentApart()
    {
        var seen = new HashSet<int>
        {
            Color32.FromPremultiplied(1, 0, 0, 0).GetHashCode(),
            Color32.FromPremultiplied(0, 1, 0, 0).GetHashCode(),
            Color32.FromPremultiplied(0, 0, 1, 0).GetHashCode(),
            Color32.FromPremultiplied(0, 0, 0, 1).GetHashCode(),
        };

        Assert.Equal(4, seen.Count);
        Assert.Equal(Color32.FromPremultiplied(1, 2, 3, 255).GetHashCode(), Color32.FromPremultiplied(1, 2, 3, 255).GetHashCode());
    }

    [Fact]
    public void Color32_IsFourBytes_InRgbaOrder_AsTexturesTakeIt()
    {
        System.Span<Color32> pixel = [Color32.FromPremultiplied(1, 2, 3, 4)];

        Assert.Equal(4, System.Runtime.CompilerServices.Unsafe.SizeOf<Color32>());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixel).ToArray());
    }

    [Fact]
    public void Equals_TreatsNotANumberAsItself_AndOperatorsDoNot()
    {
        var a = new Color(float.NaN, 0f, 0f);
        var b = new Color(float.NaN, 0f, 0f);

        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
        Assert.False(a == b);
    }

    [Fact]
    public void Color32_ToString_NamesItsComponents()
    {
        Assert.Equal("R: 1 G: 2 B: 3 A: 4", Color32.FromPremultiplied(1, 2, 3, 4).ToString());
    }
}
