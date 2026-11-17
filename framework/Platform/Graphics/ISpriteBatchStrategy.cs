// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Graphics;

namespace DotGame.Framework.Platform.Graphics;

internal interface ISpriteBatchStrategy : IDisposable
{
    /// <summary>
    /// Begins a sprite batch drawing session.
    /// Must be called before any <c>Draw()</c> calls and paired with a corresponding <c>End()</c> call.
    /// </summary>
    /// <param name="transformMatrix">An optional transformation matrix to apply to all drawn sprites. If null, no transformation is applied.</param>
    /// <param name="blend">How sprites combine with what's behind them.</param>
    /// <param name="sampler">How textures are read.</param>
    public void Begin(
        Matrix4x4? transformMatrix = null,
        BlendMode blend = BlendMode.Alpha,
        Sampler sampler = Sampler.LinearClamp);

    /// <summary>
    /// Sets the shader for the sprites drawn after this, until it's next set. <c>Begin</c> starts each
    /// batch with the default one. Called between <c>Begin</c> and <c>End</c>.
    /// </summary>
    /// <param name="shader">The shader, or <c>null</c> for the default one.</param>
    public void SetShader(
        Strategy<IShaderStrategy>? shader);

    /// <summary>
    /// Queues a texture to be drawn during this batch.
    /// <c>Begin</c> must be called before this method.
    /// </summary>
    public void Draw(
        Strategy<ITextureStrategy> textureStrategy,
        Vector2 position,
        Color color,
        Angle rotation,
        Vector2 origin,
        Vector2 scale,
        Rectangle? sourceRectangle,
        SpriteFlip flip,
        in ShaderCustomData customData);

    /// <summary>
    /// Queues a triangle to be drawn during this batch, in order with the sprites. Its three
    /// vertices carry <paramref name="customData"/> to the shader, as a sprite's four do.
    /// </summary>
    public void DrawTriangle(
        Strategy<ITextureStrategy> textureStrategy,
        in Vertex a,
        in Vertex b,
        in Vertex c,
        in ShaderCustomData customData);

    /// <summary>
    /// Ends the sprite batch and flushes all queued draw calls.
    /// <c>Begin</c> must be called before this method.
    /// </summary>
    public void End();
}
