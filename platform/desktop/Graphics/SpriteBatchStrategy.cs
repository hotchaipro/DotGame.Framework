// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Common.Graphics;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Desktop.Graphics;

/// <summary>
/// Builds sprites' vertices and hands them to the graphics service's
/// <see cref="DrawRecorder"/>, which draws the frame when it's submitted. A batch's
/// <c>End</c> doesn't draw anything.
/// </summary>
internal sealed class SpriteBatchStrategy : ISpriteBatchStrategy
{
    private readonly GraphicsServiceStrategy _graphicsService;
    private bool _isDrawing;
    private Matrix4x4 _batchTransform;
    private ShaderStrategy? _currentShader;
    private nint _pipeline;
    private BlendMode _blend;

    internal SpriteBatchStrategy(
        GraphicsServiceStrategy graphicsService)
    {
        this._graphicsService = graphicsService ?? throw new ArgumentNullException(nameof(graphicsService));
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
        this._blend = blend;
        this.SetShader(null);
        this._graphicsService.Recorder.SetSampler(this._graphicsService.GetSampler(sampler));
        this._isDrawing = true;
    }

    public void SetShader(
        Strategy<IShaderStrategy>? shaderStrategy)
    {
        this._currentShader = shaderStrategy?.ToConcrete<ShaderStrategy>();
        this._pipeline = this._currentShader?.GetPipeline(this._blend) ?? this._graphicsService.GetDefaultPipeline(this._blend);
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

        var sdlTexture = textureStrategy.ToConcrete<TextureStrategy>();
        var textureSize = sdlTexture.Size;

        ref var first = ref this._graphicsService.Recorder.AddSprite(this._currentShader, this._pipeline, sdlTexture.NativeHandle);

        SpriteVertex.WriteSprite(
            ref first,
            textureSize,
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

        var sdlTexture = textureStrategy.ToConcrete<TextureStrategy>();
        ref var first = ref this._graphicsService.Recorder.AddSprite(this._currentShader, this._pipeline, sdlTexture.NativeHandle);

        SpriteVertex.WriteTriangle(ref first, in a, in b, in c, in this._batchTransform, in customData);
    }

    public void End()
    {
        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before End().");
        }

        this._isDrawing = false;
    }

    // Nothing to release: the recorder owns the GPU buffers
    public void Dispose()
    {
    }
}
