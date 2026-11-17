// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Graphics;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Graphics;
using Xunit;

namespace DotGame.Framework.Tests.Graphics;

/// <summary>
/// The rules <see cref="SpriteBatch"/> keeps for every platform: begin, draw, end, in that order,
/// and what it hands the platform's batch.
/// </summary>
public sealed class SpriteBatchTests : IDisposable
{
    private readonly RecordingBatch _platform = new();
    private readonly SpriteBatch _batch;
    private readonly Texture _texture = new(new Strategy<ITextureStrategy>(new FakeTexture()));

    public SpriteBatchTests()
    {
        this._batch = new SpriteBatch(new Strategy<ISpriteBatchStrategy>(this._platform));
    }

    public void Dispose()
    {
        this._batch.Dispose();
        this._texture.Dispose();
    }

    [Fact]
    public void BeginDrawEnd_ReachThePlatformInOrder()
    {
        using (this._batch.Begin())
        {
            this._batch.Draw(this._texture, Vector2.Zero, Color.White);
            this._batch.Draw(this._texture, Vector2.One, Color.White);
        }

        Assert.Equal(["Begin", "Draw", "Draw", "End"], this._platform.Calls);
    }

    [Fact]
    public void AfterEnd_ItCanBeginAgain()
    {
        using (this._batch.Begin())
        {
        }

        using (this._batch.Begin())
        {
        }

        Assert.Equal(["Begin", "End", "Begin", "End"], this._platform.Calls);
    }

    [Fact]
    public void Begin_WhileDrawing_Throws_AndTheBatchCarriesOn()
    {
        using var drawing = this._batch.Begin();

        Assert.Throws<InvalidOperationException>(() => this._batch.Begin());

        Assert.Equal(["Begin"], this._platform.Calls);
        this._batch.Draw(this._texture, Vector2.Zero, Color.White);
    }

    [Fact]
    public void DrawTriangles_CarryTheirShaderDataToThePlatform()
    {
        var data = new ShaderCustomData(1f, 2f, 3f, 4f);
        DotGame.Framework.Graphics.Vertex[] corners =
        [
            new(Vector2.Zero, Vector2.Zero, Color.White),
            new(Vector2.UnitX, Vector2.UnitX, Color.White),
            new(Vector2.UnitY, Vector2.UnitY, Color.White),
        ];

        using (this._batch.Begin())
        {
            this._batch.DrawTriangles(this._texture, corners, in data);
            Assert.Equal(data, this._platform.LastTriangleData);

            this._batch.DrawTriangles(this._texture, corners, [0, 1, 2], in data);
            Assert.Equal(2, this._platform.TriangleCount);

            // Without any, a triangle carries none, as a sprite does
            this._batch.DrawTriangles(this._texture, corners);
            Assert.Equal(default, this._platform.LastTriangleData);
        }
    }

    [Fact]
    public void Draw_BeforeBegin_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => this._batch.Draw(this._texture, Vector2.Zero, Color.White));

        Assert.Empty(this._platform.Calls);
    }

    [Fact]
    public void Draw_AfterEnd_Throws()
    {
        using (this._batch.Begin())
        {
        }

        Assert.Throws<InvalidOperationException>(() => this._batch.Draw(this._texture, Vector2.Zero, Color.White));
    }

    [Fact]
    public void AScope_DisposedAgain_EndsTheBatchOnce()
    {
        var scope = this._batch.Begin();

        scope.Dispose();
        scope.Dispose();

        Assert.Equal(["Begin", "End"], this._platform.Calls);
    }

    [Fact]
    public void AnOldScope_DoesNotEndTheNextBatch()
    {
        var old = this._batch.Begin();
        old.Dispose();

        using var current = this._batch.Begin();
        old.Dispose();

        Assert.Equal(["Begin", "End", "Begin"], this._platform.Calls);
    }

    [Fact]
    public void ADefaultScope_DoesNothing()
    {
        default(SpriteBatchScope).Dispose();

        Assert.Empty(this._platform.Calls);
    }

    [Fact]
    public void AnException_EndsTheBatch_SoTheNextBegins()
    {
        void DrawAndFail()
        {
            using var failing = this._batch.Begin();
            throw new TimeoutException("drawing failed");
        }

        Assert.Throws<TimeoutException>(DrawAndFail);

        using var next = this._batch.Begin();
        Assert.Equal(["Begin", "End", "Begin"], this._platform.Calls);
    }

    [Fact]
    public void Draw_RefusesANullTexture()
    {
        using var drawing = this._batch.Begin();

        Assert.Throws<ArgumentNullException>(() => this._batch.Draw(null!, Vector2.Zero, Color.White));
    }

    [Fact]
    public void Begin_PassesTheTransform()
    {
        var transform = Matrix3x2.CreateScale(2f);

        using (this._batch.Begin(transform))
        {
            Assert.Equal(new Matrix4x4(transform), this._platform.Transform);
        }

        using var drawing = this._batch.Begin();
        Assert.Null(this._platform.Transform);
    }

    [Fact]
    public void Draw_PassesEverything()
    {
        var customData = new ShaderCustomData(1f, 2f, 3f, 4f);
        var source = new Rectangle(1f, 2f, 3f, 4f);

        using var drawing = this._batch.Begin();
        this._batch.Draw(this._texture, new Vector2(5f, 6f), Color.Red, Angle.QuarterCircle, new Vector2(7f, 8f), new Vector2(9f, 10f), source, customData: in customData);

        var draw = this._platform.LastDraw;
        Assert.Same(this._texture.Strategy, draw.Texture);
        Assert.Equal(new Vector2(5f, 6f), draw.Position);
        Assert.Equal(Color.Red, draw.Color);
        Assert.Equal(Angle.QuarterCircle, draw.Rotation);
        Assert.Equal(new Vector2(7f, 8f), draw.Origin);
        Assert.Equal(new Vector2(9f, 10f), draw.Scale);
        Assert.Equal(source, draw.Source);
        Assert.Equal(customData, draw.CustomData);
    }

    [Fact]
    public void Draw_Defaults_AreNoRotationNoOriginNoScaleAndAllOfTheTexture()
    {
        using var drawing = this._batch.Begin();
        this._batch.Draw(this._texture, Vector2.Zero, Color.White);

        var draw = this._platform.LastDraw;
        Assert.Equal(Angle.Zero, draw.Rotation);
        Assert.Equal(Vector2.Zero, draw.Origin);
        Assert.Equal(Vector2.One, draw.Scale);
        Assert.Null(draw.Source);
        Assert.Equal(default, draw.CustomData);
    }

    [Fact]
    public void Dispose_DisposesThePlatformsBatch_Once()
    {
        Assert.False(this._batch.IsDisposed);

        this._batch.Dispose();
        this._batch.Dispose();

        Assert.True(this._batch.IsDisposed);
        Assert.Equal(1, this._platform.Disposals);
    }

    [Fact]
    public void Begin_PassesItsSettings()
    {
        using var drawing = this._batch.Begin(new SpriteBatchSettings { Blend = BlendMode.Additive, Sampler = Sampler.PointRepeat });

        Assert.Equal(BlendMode.Additive, this._platform.Blend);
        Assert.Equal(Sampler.PointRepeat, this._platform.Sampler);
    }

    [Fact]
    public void UseShader_LastsForItsScope()
    {
        var shader = NewShader();
        using var drawing = this._batch.Begin();

        using (this._batch.UseShader(shader))
        {
            Assert.Same(shader.Strategy, this._platform.Shader);
        }

        Assert.Null(this._platform.Shader);
    }

    [Fact]
    public void UseShader_Nests_AndEachGoesBackToTheOneBefore()
    {
        var outer = NewShader();
        var inner = NewShader();
        using var drawing = this._batch.Begin();

        using (this._batch.UseShader(outer))
        {
            using (this._batch.UseShader(inner))
            {
                Assert.Same(inner.Strategy, this._platform.Shader);

                using (this._batch.UseShader(null))
                {
                    Assert.Null(this._platform.Shader);
                }
            }

            Assert.Same(outer.Strategy, this._platform.Shader);
        }

        Assert.Null(this._platform.Shader);
    }

    [Fact]
    public void UseShader_WithTheOneInUse_TellsThePlatformNothing()
    {
        var shader = NewShader();
        using var drawing = this._batch.Begin();

        using (this._batch.UseShader(shader))
        {
            this._platform.Calls.Clear();

            using (this._batch.UseShader(shader))
            {
            }

            Assert.Empty(this._platform.Calls);
        }
    }

    [Fact]
    public void UseShader_BeforeBegin_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => this._batch.UseShader(NewShader()));
    }

    [Fact]
    public void AShadersScope_DisposedAgain_DoesNothing()
    {
        var shader = NewShader();
        using var drawing = this._batch.Begin();
        using var outer = this._batch.UseShader(shader);
        var inner = this._batch.UseShader(null);

        inner.Dispose();
        inner.Dispose();

        Assert.Same(shader.Strategy, this._platform.Shader);
        outer.Dispose();
    }

    [Fact]
    public void ADefaultShaderScope_DoesNothing()
    {
        default(ShaderScope).Dispose();
    }

    [Fact]
    public void Begin_ByDefault_IsThePlainCase()
    {
        using var drawing = this._batch.Begin();

        Assert.Null(this._platform.Shader);
        Assert.Null(this._platform.Transform);
        Assert.Equal(BlendMode.Alpha, this._platform.Blend);
        Assert.Equal(Sampler.LinearClamp, this._platform.Sampler);
    }

    private static Shader NewShader()
    {
        return new Shader(new Strategy<IShaderStrategy>(new FakeShader()));
    }

    private sealed class FakeShader : IShaderStrategy
    {
        public Strategy<IShaderParameterStrategy> GetParameter(
            string name)
        {
            throw new NotSupportedException();
        }
    }

    [Fact]
    public void Begin_AfterDispose_Throws()
    {
        this._batch.Dispose();

        Assert.Throws<ObjectDisposedException>(() => this._batch.Begin());
    }

    [Fact]
    public void Draw_AfterDisposeWhileDrawing_Throws()
    {
        using var drawing = this._batch.Begin();
        this._batch.Dispose();

        Assert.Throws<ObjectDisposedException>(() => this._batch.Draw(this._texture, Vector2.Zero, Color.White));
    }

    [Fact]
    public void ATexture_ReportsItsSize_AndItsDisposal()
    {
        var texture = new Texture(new Strategy<ITextureStrategy>(new FakeTexture()));

        Assert.Equal(new Vector2Int(32, 16), texture.Size);
        Assert.False(texture.IsDisposed);

        texture.Dispose();

        Assert.True(texture.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => texture.Size);
    }

    private sealed class FakeTexture : ITextureStrategy
    {
        public void SetData(DotGame.Framework.RectangleInt region, System.ReadOnlySpan<DotGame.Framework.Color32> data)
        {
            this.SetDataCalls++;
        }

        public int SetDataCalls { get; private set; }

        public Vector2Int Size => new(32, 16);

        public void Dispose()
        {
        }
    }

    private readonly record struct DrawCall(
        Strategy<ITextureStrategy> Texture,
        Vector2 Position,
        Color Color,
        Angle Rotation,
        Vector2 Origin,
        Vector2 Scale,
        Rectangle? Source,
        ShaderCustomData CustomData);

    private sealed class RecordingBatch : ISpriteBatchStrategy
    {
        public int TriangleCount { get; private set; }

        public ShaderCustomData LastTriangleData { get; private set; }

        public void DrawTriangle(Strategy<ITextureStrategy> textureStrategy, in DotGame.Framework.Graphics.Vertex a, in DotGame.Framework.Graphics.Vertex b, in DotGame.Framework.Graphics.Vertex c, in ShaderCustomData customData)
        {
            this.TriangleCount++;
            this.LastTriangleData = customData;
        }

        public List<string> Calls { get; } = [];

        public Matrix4x4? Transform { get; private set; }

        public BlendMode Blend { get; private set; }

        public Sampler Sampler { get; private set; }

        public DrawCall LastDraw { get; private set; }

        public int Disposals { get; private set; }

        public Strategy<IShaderStrategy>? Shader { get; private set; }

        public void Begin(
            Matrix4x4? transformMatrix = null,
            BlendMode blend = BlendMode.Alpha,
            Sampler sampler = Sampler.LinearClamp)
        {
            this.Calls.Add("Begin");
            this.Transform = transformMatrix;
            this.Blend = blend;
            this.Sampler = sampler;
            this.Shader = null;
        }

        public void SetShader(
            Strategy<IShaderStrategy>? shader)
        {
            this.Calls.Add("SetShader");
            this.Shader = shader;
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
            this.Calls.Add("Draw");
            this.LastDraw = new DrawCall(textureStrategy, position, color, rotation, origin, scale, sourceRectangle, customData);
        }

        public void End()
        {
            this.Calls.Add("End");
        }

        public void Dispose()
        {
            this.Disposals++;
        }
    }
}
