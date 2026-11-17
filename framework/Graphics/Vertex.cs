// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Graphics;

/// <summary>
/// A corner of a triangle for <see cref="SpriteBatch.DrawTriangles(Texture, System.ReadOnlySpan{Vertex}, in ShaderCustomData)"/>.
/// </summary>
/// <param name="Position">Where it is, as a sprite's position is, before the batch's transform.</param>
/// <param name="TextureCoordinate">The point of the texture here, from 0 to 1 across it.</param>
/// <param name="Color">Multiplied with the texture, and blended across the triangle.</param>
public readonly record struct Vertex(
    Vector2 Position,
    Vector2 TextureCoordinate,
    Color Color);
