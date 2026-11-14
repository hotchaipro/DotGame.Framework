// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Testing;

/// <summary>
/// Real framework textures over a fake platform texture, so code built on the framework runs as
/// it does in a game without a graphics device.
/// </summary>
public static class TestTextures
{
    public static Texture Create(
        int width = 1024,
        int height = 1024)
    {
        var fake = new FakeTexture(new Vector2Int(width, height));
        var texture = new Texture(new Strategy<ITextureStrategy>(fake));
        fake.Owner = texture;
        return texture;
    }

    /// <summary>
    /// The texture a platform texture belongs to, if a test made it.
    /// </summary>
    internal static Texture? OwnerOf(
        Strategy<ITextureStrategy> strategy)
    {
        return (strategy.Interface as FakeTexture)?.Owner;
    }
}

/// <summary>
/// Real framework shaders over a fake platform shader, which has no parameters.
/// </summary>
public static class TestShaders
{
    /// <summary>One shader to share, for tests that need one but don't look at it.</summary>
    public static Shader Shared { get; } = Create();

    public static Shader Create()
    {
        var fake = new FakeShader();
        var shader = new Shader(new Strategy<IShaderStrategy>(fake));
        fake.Owner = shader;
        return shader;
    }

    internal static Shader? OwnerOf(
        Strategy<IShaderStrategy>? strategy)
    {
        return (strategy?.Interface as FakeShader)?.Owner;
    }
}

internal sealed class FakeTexture(Vector2Int size) : ITextureStrategy
{
    public Texture? Owner { get; set; }

    public Vector2Int Size { get; } = size;

    public void SetData(
        RectangleInt region,
        ReadOnlySpan<Color32> data)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeShader : IShaderStrategy, IDisposable
{
    public Shader? Owner { get; set; }

    public Strategy<IShaderParameterStrategy> GetParameter(
        string name)
    {
        throw new NotSupportedException();
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// What a platform's shader sees of the data drawn with a sprite.
/// </summary>
public static class TestShaderData
{
    extension(ShaderCustomData data)
    {
        /// <summary>The four values, as the platform passes them to the shader.</summary>
        public (Half X, Half Y, Half Z, Half W) Values => (data.X, data.Y, data.Z, data.W);
    }
}

/// <summary>
/// One sprite drawn with a <see cref="RecordingBatch"/>, as the platform's sprite batch receives it.
/// </summary>
/// <param name="Texture">The texture drawn, if a test made it (see <see cref="TestTextures"/>).</param>
/// <param name="Shader">The shader it was drawn with, if any and a test made it.</param>
/// <param name="Flip">How its image was mirrored inside its rectangle.</param>
public readonly record struct DrawCall(
    Texture? Texture,
    Vector2 Position,
    Color Color,
    Angle Rotation,
    Vector2 Origin,
    Vector2 Scale,
    Rectangle? Source,
    ShaderCustomData CustomData,
    Shader? Shader = null,
    SpriteFlip Flip = SpriteFlip.None)
{
    private Rectangle SourceRectangle => this.Source!.Value;

    /// <summary>
    /// Where the source rectangle's left edge lands, without rotation.
    /// </summary>
    public float Left => this.Position.X - (this.Origin.X * this.Scale.X);

    public float Top => this.Position.Y - (this.Origin.Y * this.Scale.Y);

    public float Right => this.Left + (this.SourceRectangle.Width * this.Scale.X);

    public float Bottom => this.Top + (this.SourceRectangle.Height * this.Scale.Y);
}

/// <summary>
/// A begun <see cref="SpriteBatch"/> that keeps what's drawn with it. The list has room for
/// allocation tests to draw without growing it.
/// </summary>
public sealed class RecordingBatch : ISpriteBatchStrategy
{
    public RecordingBatch()
    {
        this.Batch = new SpriteBatch(new Strategy<ISpriteBatchStrategy>(this));
        this.Scope = this.Batch.Begin();
    }

    public SpriteBatch Batch { get; }

    public SpriteBatchScope Scope { get; }

    /// <summary>The shader set on the batch now, if any and a test made it.</summary>
    public Shader? Shader { get; private set; }

    public List<DrawCall> Calls { get; } = new(4096);

    public int TriangleCount { get; private set; }

    void ISpriteBatchStrategy.DrawTriangle(
        Strategy<ITextureStrategy> textureStrategy,
        in Vertex a,
        in Vertex b,
        in Vertex c)
    {
        this.TriangleCount++;
    }

    void ISpriteBatchStrategy.Begin(
        Matrix4x4? transformMatrix,
        BlendMode blend,
        Sampler sampler)
    {
    }

    void ISpriteBatchStrategy.SetShader(
        Strategy<IShaderStrategy>? shader)
    {
        this.Shader = TestShaders.OwnerOf(shader);
    }

    void ISpriteBatchStrategy.Draw(
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
        this.Calls.Add(new DrawCall(TestTextures.OwnerOf(textureStrategy), position, color, rotation, origin, scale, sourceRectangle, customData, this.Shader, flip));
    }

    void ISpriteBatchStrategy.End()
    {
    }

    void IDisposable.Dispose()
    {
    }
}

/// <summary>
/// A <see cref="GraphicsService"/> over a fake device: textures and render textures with no
/// pixels, and a sprite batch that keeps only the last color drawn.
/// </summary>
public sealed class TestGraphics : IGraphicsServiceStrategy
{
    private Strategy<ITextureStrategy>? _renderTarget;
    private GraphicsService? _service;

    /// <summary>
    /// The service, started on first use.
    /// </summary>
    public GraphicsService Service
    {
        get
        {
            if (this._service is null)
            {
                this._service = new GraphicsService(this.ToStrategy());
                this._service.Initialize().GetAwaiter().GetResult();
            }

            return this._service;
        }
    }

    /// <summary>The color of the last sprite drawn with any of the service's sprite batches.</summary>
    public Color LastColor { get; private set; }

    /// <summary>Whether drawing goes to a render texture now, rather than the screen.</summary>
    public bool HasRenderTarget => this._renderTarget is not null;

    /// <summary>
    /// Whether drawing goes to <paramref name="texture"/> now.
    /// </summary>
    public bool IsRenderTarget(
        Texture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        return ReferenceEquals(this._renderTarget, texture.Strategy);
    }

    internal Strategy<IGraphicsServiceStrategy> ToStrategy() => new(this);

    int IGraphicsServiceStrategy.MultisampleCount => 1;

    int? IGraphicsServiceStrategy.PreferredFrameRate { get; set; }

    Task IGraphicsServiceStrategy.OnInitialize() => Task.CompletedTask;

    Strategy<ISpriteBatchStrategy> IGraphicsServiceStrategy.CreateSpriteBatch() => new(new LastColorBatch(this));

    Strategy<ITextureStrategy> IGraphicsServiceStrategy.CreateTexture(Vector2Int size, ReadOnlySpan<Color32> data) => new(new FakeTexture(size));

    Strategy<ITextureStrategy> IGraphicsServiceStrategy.CreateRenderTexture(Vector2Int size) => new(new FakeTexture(size));

    void IGraphicsServiceStrategy.SetRenderTarget(
        Strategy<ITextureStrategy>? textureStrategy)
    {
        this._renderTarget = textureStrategy;
    }

    Task IGraphicsServiceStrategy.ReadPixelsAsync(Strategy<ITextureStrategy> textureStrategy, Memory<Color32> destination) => throw new NotSupportedException();

    void IGraphicsServiceStrategy.Clear(
        Color color)
    {
    }

    void IGraphicsServiceStrategy.SetScissorRectangle(
        RectangleInt? rectangle)
    {
    }

    private sealed class LastColorBatch(TestGraphics graphics) : ISpriteBatchStrategy
    {
        public void DrawTriangle(Strategy<ITextureStrategy> textureStrategy, in Vertex a, in Vertex b, in Vertex c)
        {
        }

        public void Begin(Matrix4x4? transformMatrix = null, BlendMode blend = BlendMode.Alpha, Sampler sampler = Sampler.LinearClamp)
        {
        }

        public void SetShader(Strategy<IShaderStrategy>? shader)
        {
        }

        public void Draw(Strategy<ITextureStrategy> textureStrategy, Vector2 position, Color color, Angle rotation, Vector2 origin, Vector2 scale, Rectangle? sourceRectangle, SpriteFlip flip, in ShaderCustomData customData)
        {
            graphics.LastColor = color;
        }

        public void End()
        {
        }

        public void Dispose()
        {
        }
    }
}
