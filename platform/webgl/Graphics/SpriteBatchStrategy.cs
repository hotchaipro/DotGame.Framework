// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Common.Graphics;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.WebGL.Graphics;

/// <summary>
/// A sprite batch, which adds its sprites to the service's <see cref="FrameRecorder"/>. Every
/// batch shares it, so their sprites draw in order, and beginning and ending one costs nothing.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class SpriteBatchStrategy : ISpriteBatchStrategy
{
    private readonly FrameRecorder _recorder;
    private bool _isDrawing;
    private Matrix4x4 _batchTransform;
    private JSObject? _shader;

    internal SpriteBatchStrategy(
        FrameRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        this._recorder = recorder;
    }

    public void Begin(
        Matrix4x4? transformMatrix = null,
        BlendMode blend = BlendMode.Alpha,
        Sampler sampler = Sampler.LinearClamp)
    {
        if (this._isDrawing)
        {
            throw new InvalidOperationException("The scope from SpriteBatch.Begin() must end before calling Begin() again.");
        }

        this._batchTransform = transformMatrix ?? Matrix4x4.Identity;

        // The shader switches when the first sprite is drawn with it, not here or in SetShader, so a
        // batch or a scope that draws nothing (the engine begins a batch after every effect) doesn't
        // split the draws on either side of it
        this._shader = null;

        this._recorder.SetRenderState((int)blend | ((int)sampler << 4));

        this._isDrawing = true;
    }

    public void SetShader(
        Strategy<IShaderStrategy>? shaderStrategy)
    {
        this._shader = shaderStrategy?.ToConcrete<ShaderStrategy>().NativeHandle;
    }

    public void Draw(
        Strategy<ITextureStrategy> textureStrategy,
        Vector2 position,
        Color color,
        Angle rotation,
        Vector2 origin,
        Vector2 scale,
        Rectangle? sourceRectangle,
        SpriteFlip flip,
        in ShaderCustomData customData)
    {
        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before Draw().");
        }

        ArgumentNullException.ThrowIfNull(textureStrategy);

        var webGLTexture = textureStrategy.ToConcrete<TextureStrategy>();

        SpriteVertex.WriteSprite(
            ref this._recorder.AddSprite(webGLTexture.NativeHandle, this._shader),
            webGLTexture.Size,
            position,
            color,
            rotation,
            origin,
            scale,
            sourceRectangle,
            flip,
            in this._batchTransform,
            in customData);
    }

    public void DrawTriangle(
        Strategy<ITextureStrategy> textureStrategy,
        in Vertex a,
        in Vertex b,
        in Vertex c,
        in ShaderCustomData customData)
    {
        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before DrawTriangle().");
        }

        var webGLTexture = textureStrategy.ToConcrete<TextureStrategy>();
        SpriteVertex.WriteTriangle(ref this._recorder.AddSprite(webGLTexture.NativeHandle, this._shader), in a, in b, in c, in this._batchTransform, in customData);
    }

    public void End()
    {
        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before End().");
        }

        this._isDrawing = false;
    }

    public void Dispose()
    {
    }
}
