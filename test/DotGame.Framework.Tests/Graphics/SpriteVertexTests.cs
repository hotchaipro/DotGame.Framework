// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Runtime.CompilerServices;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Common.Graphics;
using Xunit;

namespace DotGame.Framework.Tests.Graphics;

/// <summary>
/// The sprite-to-quad maths every platform's sprite batch shares, against the transform it
/// stands for: scale about the origin, rotate, translate to the position, then the batch's
/// transform.
/// </summary>
public sealed class SpriteVertexTests
{
    private const float Tolerance = 1e-3f;

    public static TheoryData<float, float, float, float, float, float> Placements => new()
    {
        // rotation (degrees), origin x, origin y, scale x, scale y, batch scale
        { 0f, 0f, 0f, 1f, 1f, 1f },
        { 0f, 16f, 8f, 2f, 0.5f, 1f },
        { 30f, 16f, 8f, 1f, 1f, 1f },
        { -135f, 4f, 20f, 1.5f, -1f, 1f },
        { 90f, 0f, 0f, 1f, 1f, 2.5f },
        { 200f, 32f, 16f, 0.75f, 2f, 0.5f },
    };

    [Theory]
    [MemberData(nameof(Placements))]
    public void WriteSprite_PlacesTheCornersAsTheTransformWould(
        float degrees,
        float originX,
        float originY,
        float scaleX,
        float scaleY,
        float batchScale)
    {
        var textureSize = new Vector2Int(64, 32);
        var position = new Vector2(100f, -40f);
        var origin = new Vector2(originX, originY);
        var scale = new Vector2(scaleX, scaleY);
        var rotation = Angle.FromDegrees(degrees);
        var batch = Matrix4x4.CreateScale(batchScale) * Matrix4x4.CreateTranslation(7f, 11f, 0f);

        var quad = new SpriteVertex[4];
        SpriteVertex.WriteSprite(ref quad[0], textureSize, position, Color.White, rotation, origin, scale, null, SpriteFlip.None, in batch, default);

        var toWorld = Matrix4x4.CreateRotationZ(rotation.Radians) * Matrix4x4.CreateTranslation(position.X, position.Y, 0f) * batch;
        Vector2[] corners = [new(0f, 0f), new(64f, 0f), new(64f, 32f), new(0f, 32f)];
        for (int i = 0; i < 4; i++)
        {
            var local = (corners[i] - origin) * scale;
            var expected = Vector2.Transform(local, toWorld);

            Assert.Equal(expected.X, quad[i].X, Tolerance);
            Assert.Equal(expected.Y, quad[i].Y, Tolerance);
        }
    }

    [Fact]
    public void WriteSprite_MapsTheSourceRectangle()
    {
        var quad = new SpriteVertex[4];
        var identity = Matrix4x4.Identity;
        SpriteVertex.WriteSprite(
            ref quad[0],
            new Vector2Int(200, 100),
            Vector2.Zero,
            Color.White,
            default,
            Vector2.Zero,
            Vector2.One,
            new Rectangle(50, 25, 100, 50),
            SpriteFlip.None,
            in identity,
            default);

        // The quad is the source rectangle's size, and samples just that part of the texture
        Assert.Equal((0f, 0f), (quad[0].X, quad[0].Y));
        Assert.Equal((100f, 50f), (quad[2].X, quad[2].Y));
        Assert.Equal((0.25f, 0.25f), (quad[0].U, quad[0].V));
        Assert.Equal((0.75f, 0.75f), (quad[2].U, quad[2].V));
        Assert.Equal((0.75f, 0.25f), (quad[1].U, quad[1].V));
        Assert.Equal((0.25f, 0.75f), (quad[3].U, quad[3].V));
    }

    [Fact]
    public void WriteSprite_PacksTheColor()
    {
        var quad = new SpriteVertex[4];
        var identity = Matrix4x4.Identity;
        SpriteVertex.WriteSprite(ref quad[0], new Vector2Int(4, 4), Vector2.Zero, Color.FromPremultiplied(1f, 0f, 0f, 1f), default, Vector2.Zero, Vector2.One, null, SpriteFlip.None, in identity, default);

        Assert.All(quad, vertex => Assert.Equal(0xff0000ffu, vertex.Color));
    }

    [Fact]
    public void TheLayout_IsWhatTheShadersRead()
    {
        var vertex = default(SpriteVertex);
        ref byte start = ref Unsafe.As<float, byte>(ref vertex.X);

        Assert.Equal(28, Unsafe.SizeOf<SpriteVertex>());
        Assert.Equal(SpriteVertex.Size, Unsafe.SizeOf<SpriteVertex>());
        Assert.Equal(4, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<float, byte>(ref vertex.Y)));
        Assert.Equal(SpriteVertex.ColorOffset, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<uint, byte>(ref vertex.Color)));
        Assert.Equal(SpriteVertex.TextureCoordinateOffset, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<float, byte>(ref vertex.U)));
        Assert.Equal(SpriteVertex.TextureCoordinateOffset + 4, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<float, byte>(ref vertex.V)));
        Assert.Equal(SpriteVertex.CustomDataOffset, (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<ShaderCustomData, byte>(ref vertex.CustomData)));
    }

    [Theory]
    [InlineData(0f, 0u)]
    [InlineData(1f, 255u)]
    [InlineData(0.5f, 128u)]
    [InlineData(0.25f, 64u)]
    [InlineData(1f / 255f, 1u)]
    [InlineData(-0.5f, 0u)]
    [InlineData(1.5f, 255u)]
    [InlineData(float.NaN, 0u)]
    [InlineData(float.PositiveInfinity, 255u)]
    [InlineData(float.NegativeInfinity, 0u)]
    public void PackColor_RoundsAndClampsEachComponent(
        float component,
        uint expected)
    {
        Assert.Equal(expected, SpriteVertex.PackColor(Color.FromPremultiplied(component, 0f, 0f, 0f)));
        Assert.Equal(expected << 8, SpriteVertex.PackColor(Color.FromPremultiplied(0f, component, 0f, 0f)));
        Assert.Equal(expected << 16, SpriteVertex.PackColor(Color.FromPremultiplied(0f, 0f, component, 0f)));
        Assert.Equal(expected << 24, SpriteVertex.PackColor(Color.FromPremultiplied(0f, 0f, 0f, component)));
    }

    [Fact]
    public void PackColor_PutsRedFirstInMemory()
    {
        uint packed = SpriteVertex.PackColor(Color.FromPremultiplied(1 / 255f, 2 / 255f, 3 / 255f, 4 / 255f));

        Assert.Equal<byte>([1, 2, 3, 4], BitConverter.GetBytes(packed));
    }

    [Fact]
    public void WriteQuad_WritesTheCornersInOrder_WithTheirCoordinates()
    {
        var quad = new SpriteVertex[4];
        var customData = new ShaderCustomData(1f, 2f, 3f, 4f);

        SpriteVertex.WriteQuad(ref quad[0], 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 0.1f, 0.2f, 0.3f, 0.4f, 0x11223344u, in customData);

        Assert.Equal((1f, 2f, 0.1f, 0.2f), (quad[0].X, quad[0].Y, quad[0].U, quad[0].V));
        Assert.Equal((3f, 4f, 0.3f, 0.2f), (quad[1].X, quad[1].Y, quad[1].U, quad[1].V));
        Assert.Equal((5f, 6f, 0.3f, 0.4f), (quad[2].X, quad[2].Y, quad[2].U, quad[2].V));
        Assert.Equal((7f, 8f, 0.1f, 0.4f), (quad[3].X, quad[3].Y, quad[3].U, quad[3].V));
        Assert.All(quad, vertex =>
        {
            Assert.Equal(0x11223344u, vertex.Color);
            Assert.Equal(customData, vertex.CustomData);
        });
    }

    [Fact]
    public void WriteSprite_WithANegativeScale_MirrorsAboutTheOrigin_InPlace()
    {
        var quad = new SpriteVertex[4];
        var identity = Matrix4x4.Identity;

        // The origin at the texture's center keeps a flipped sprite where it was
        SpriteVertex.WriteSprite(ref quad[0], new Vector2Int(64, 32), new Vector2(100f, 100f), Color.White, default, new Vector2(32f, 16f), new Vector2(-1f, 1f), null, SpriteFlip.None, in identity, default);

        Assert.Equal((132f, 84f), (quad[0].X, quad[0].Y));
        Assert.Equal((68f, 84f), (quad[1].X, quad[1].Y));
        Assert.Equal((68f, 116f), (quad[2].X, quad[2].Y));
        Assert.Equal((132f, 116f), (quad[3].X, quad[3].Y));

        // The picture is flipped, and the coordinates still run left to right from its corner
        Assert.Equal((0f, 0f), (quad[0].U, quad[0].V));
        Assert.Equal((1f, 1f), (quad[2].U, quad[2].V));
    }

    [Fact]
    public void WriteSprite_DoesNotWritePastItsFourVertices()
    {
        var vertices = new SpriteVertex[6];
        vertices[0].X = -1f;
        vertices[5].X = -5f;
        var identity = Matrix4x4.Identity;

        SpriteVertex.WriteSprite(ref vertices[1], new Vector2Int(4, 4), Vector2.Zero, Color.White, default, Vector2.Zero, Vector2.One, null, SpriteFlip.None, in identity, default);

        Assert.Equal(-1f, vertices[0].X);
        Assert.Equal(-5f, vertices[5].X);
    }

    [Fact]
    public void WriteSprite_CarriesTheCustomData()
    {
        var quad = new SpriteVertex[4];
        var identity = Matrix4x4.Identity;
        var customData = new ShaderCustomData(0.5f, 1.5f, 2.5f, 3.5f);

        SpriteVertex.WriteSprite(ref quad[0], new Vector2Int(4, 4), Vector2.Zero, Color.White, default, Vector2.Zero, Vector2.One, null, SpriteFlip.None, in identity, in customData);

        Assert.All(quad, vertex => Assert.Equal(customData, vertex.CustomData));
    }

    [Theory]
    [InlineData(SpriteFlip.None, 0f, 0f, 1f, 1f)]
    [InlineData(SpriteFlip.Horizontal, 1f, 0f, 0f, 1f)]
    [InlineData(SpriteFlip.Vertical, 0f, 1f, 1f, 0f)]
    [InlineData(SpriteFlip.Both, 1f, 1f, 0f, 0f)]
    public void WriteSprite_Flip_SwapsTheTextureCoordinates_AndLeavesTheCornersInPlace(
        SpriteFlip flip,
        float leftU,
        float topV,
        float rightU,
        float bottomV)
    {
        var textureSize = new Vector2Int(64, 32);
        var position = new Vector2(100f, -40f);
        var origin = new Vector2(10f, 6f);
        var scale = new Vector2(2f, 3f);
        var rotation = Angle.FromDegrees(30);
        var identity = Matrix4x4.Identity;

        var plain = new SpriteVertex[4];
        var flipped = new SpriteVertex[4];
        SpriteVertex.WriteSprite(ref plain[0], textureSize, position, Color.White, rotation, origin, scale, null, SpriteFlip.None, in identity, default);
        SpriteVertex.WriteSprite(ref flipped[0], textureSize, position, Color.White, rotation, origin, scale, null, flip, in identity, default);

        // The corners, in order: top-left, top-right, bottom-right, bottom-left
        float[] us = [leftU, rightU, rightU, leftU];
        float[] vs = [topV, topV, bottomV, bottomV];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(plain[i].X, flipped[i].X);
            Assert.Equal(plain[i].Y, flipped[i].Y);
            Assert.Equal(us[i], flipped[i].U);
            Assert.Equal(vs[i], flipped[i].V);
        }
    }
}
