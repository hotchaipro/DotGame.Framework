// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DotGame.Framework.Graphics;

namespace DotGame.Framework.Platform.Common.Graphics;

/// <summary>
/// The vertex every platform's sprite batch draws with: 28 bytes.
/// </summary>
/// <remarks>
/// <code>
///   offset  0  position           2 × float
///   offset  8  color              4 × unsigned byte, normalized (R, G, B, A in memory order)
///   offset 12  texture coordinate 2 × float
///   offset 20  shader data        4 × half float (TEXCOORD1)
/// </code>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SpriteVertex
{
    internal const int Size = 28;
    internal const int ColorOffset = 8;
    internal const int TextureCoordinateOffset = 12;
    internal const int CustomDataOffset = 20;

    public float X;
    public float Y;
    public uint Color;
    public float U;
    public float V;
    public ShaderCustomData CustomData;

    /// <summary>
    /// Packs a color into four normalized bytes, clamping each component to 0–1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint PackColor(
        Color color)
    {
        uint r = ToByte(color.R);
        uint g = ToByte(color.G);
        uint b = ToByte(color.B);
        uint a = ToByte(color.A);

        // Little-endian, so R is the first byte in memory
        return r | (g << 8) | (b << 16) | (a << 24);
    }

    /// <summary>
    /// Writes a sprite's four corners, top-left, top-right, bottom-right and bottom-left.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteQuad(
        ref SpriteVertex first,
        float tlX, float tlY,
        float trX, float trY,
        float brX, float brY,
        float blX, float blY,
        float u0, float v0,
        float u1, float v1,
        uint color,
        in ShaderCustomData customData)
    {
        ref var v = ref first;
        v.X = tlX; v.Y = tlY; v.Color = color; v.U = u0; v.V = v0; v.CustomData = customData;

        v = ref Unsafe.Add(ref first, 1);
        v.X = trX; v.Y = trY; v.Color = color; v.U = u1; v.V = v0; v.CustomData = customData;

        v = ref Unsafe.Add(ref first, 2);
        v.X = brX; v.Y = brY; v.Color = color; v.U = u1; v.V = v1; v.CustomData = customData;

        v = ref Unsafe.Add(ref first, 3);
        v.X = blX; v.Y = blY; v.Color = color; v.U = u0; v.V = v1; v.CustomData = customData;
    }

    /// <summary>
    /// Writes a sprite's four corners: its texture (or the source rectangle of it), placed at
    /// <paramref name="position"/>, rotated about <paramref name="origin"/> and scaled, then
    /// transformed by the batch's <paramref name="transform"/> (only its 2D affine part is used).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteSprite(
        ref SpriteVertex first,
        Vector2Int textureSize,
        Vector2 position,
        Color color,
        Angle rotation,
        Vector2 origin,
        Vector2 scale,
        Rectangle? sourceRectangle,
        SpriteFlip flip,
        in Matrix4x4 transform,
        in ShaderCustomData customData)
    {
        // The sprite's rotation and translation folded into the batch transform, as one 2D
        // affine: avoids three Matrix4x4 constructions and a 4x4 multiply per sprite
        float sa, sb, sc, sd;
        float radians = rotation.Radians;
        if (radians == 0f)
        {
            sa = 1f; sb = 0f; sc = 0f; sd = 1f;
        }
        else
        {
            (float sin, float cos) = rotation.SinCos;
            sa = cos; sb = sin; sc = -sin; sd = cos;
        }

        float stx = position.X, sty = position.Y;
        float b11 = transform.M11, b12 = transform.M12, b21 = transform.M21, b22 = transform.M22;
        float ma = (sa * b11) + (sb * b21);
        float mb = (sa * b12) + (sb * b22);
        float mc = (sc * b11) + (sd * b21);
        float md = (sc * b12) + (sd * b22);
        float mtx = (stx * b11) + (sty * b21) + transform.M41;
        float mty = (stx * b12) + (sty * b22) + transform.M42;

        float width, height;
        float u0, v0, u1, v1;
        if (sourceRectangle is { } r)
        {
            width = r.Width;
            height = r.Height;
            float invTexW = 1f / textureSize.X;
            float invTexH = 1f / textureSize.Y;
            u0 = r.X * invTexW;
            v0 = r.Y * invTexH;
            u1 = (r.X + r.Width) * invTexW;
            v1 = (r.Y + r.Height) * invTexH;
        }
        else
        {
            width = textureSize.X;
            height = textureSize.Y;
            u0 = 0f; v0 = 0f; u1 = 1f; v1 = 1f;
        }

        // A flip mirrors the image inside the sprite's rectangle: its texture coordinates swap ends
        if ((flip & SpriteFlip.Horizontal) != 0)
        {
            (u0, u1) = (u1, u0);
        }

        if ((flip & SpriteFlip.Vertical) != 0)
        {
            (v0, v1) = (v1, v0);
        }

        float x1 = -origin.X * scale.X;
        float y1 = -origin.Y * scale.Y;
        float x2 = (width - origin.X) * scale.X;
        float y2 = (height - origin.Y) * scale.Y;

        // The corners through the affine, sharing the partial products
        float x1ma = x1 * ma, x1mb = x1 * mb;
        float x2ma = x2 * ma, x2mb = x2 * mb;
        float y1mc = y1 * mc, y1md = y1 * md;
        float y2mc = y2 * mc, y2md = y2 * md;

        WriteQuad(
            ref first,
            x1ma + y1mc + mtx, x1mb + y1md + mty,
            x2ma + y1mc + mtx, x2mb + y1md + mty,
            x2ma + y2mc + mtx, x2mb + y2md + mty,
            x1ma + y2mc + mtx, x1mb + y2md + mty,
            u0, v0, u1, v1,
            PackColor(color),
            in customData);
    }

    /// <summary>
    /// Writes a triangle into a sprite's four vertices: the last repeats the third, so the quad's
    /// second triangle has no area and the batches' one index pattern serves both.
    /// </summary>
    internal static void WriteTriangle(
        ref SpriteVertex first,
        in Vertex a,
        in Vertex b,
        in Vertex c,
        in Matrix4x4 transform)
    {
        WriteCorner(ref first, in a, in transform);
        WriteCorner(ref Unsafe.Add(ref first, 1), in b, in transform);
        WriteCorner(ref Unsafe.Add(ref first, 2), in c, in transform);
        Unsafe.Add(ref first, 3) = Unsafe.Add(ref first, 2);
    }

    private static void WriteCorner(
        ref SpriteVertex vertex,
        in Vertex corner,
        in Matrix4x4 transform)
    {
        var position = corner.Position;
        vertex.X = (position.X * transform.M11) + (position.Y * transform.M21) + transform.M41;
        vertex.Y = (position.X * transform.M12) + (position.Y * transform.M22) + transform.M42;
        vertex.Color = PackColor(corner.Color);
        vertex.U = corner.TextureCoordinate.X;
        vertex.V = corner.TextureCoordinate.Y;
        vertex.CustomData = default;
    }

    // NaN packs as 0
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ToByte(
        float value) =>
        (value >= 1f) ? 255u : (value > 0f) ? (uint)((value * 255f) + 0.5f) : 0u;
}
