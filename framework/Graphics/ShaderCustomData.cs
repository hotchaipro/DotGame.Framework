// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Graphics;

/// <summary>
/// Four custom values that travel with a sprite to its shader, for settings that differ from sprite
/// to sprite in one batch. Settings shared by every sprite in a batch belong in the shader's
/// parameters instead (<see cref="ShaderParameter"/>).
/// </summary>
/// <remarks>
/// <para>
/// A shader reads them as a <c>float4</c> with the semantic <c>TEXCOORD1</c>, after the color and
/// the texture coordinate. The default sprite shader ignores them, and sprites drawn without them
/// pass zeros.
/// </para>
/// <para>
/// They're kept to about three significant digits, from about 6×10⁻⁵ to 65504, as they travel as
/// half floats.
/// </para>
/// </remarks>
public readonly record struct ShaderCustomData
{
    /// <summary>
    /// Makes the data. The shader sees these as its value's x, y, z and w.
    /// </summary>
    public ShaderCustomData(
        float x,
        float y,
        float z,
        float w)
    {
        this.X = (Half)x;
        this.Y = (Half)y;
        this.Z = (Half)z;
        this.W = (Half)w;
    }

    internal Half X { get; }

    internal Half Y { get; }

    internal Half Z { get; }

    internal Half W { get; }
}
