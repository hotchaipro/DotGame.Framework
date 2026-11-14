// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Graphics;

/// <summary>A texture you can draw into with <see cref="GraphicsService.UseRenderTarget"/>, then draw like any other texture. Its pixels can be read back with <c>ReadPixelsAsync</c>.</summary>
public sealed class RenderTexture : Texture
{
    internal RenderTexture(
        Strategy<ITextureStrategy> textureStrategy)
        : base(textureStrategy)
    {
    }
}
