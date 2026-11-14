// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Diagnostics;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Graphics;
using Xunit;

namespace DotGame.Framework.Tests.Graphics;

/// <summary>
/// The render target changes between batches, and the scissor at any time, the same on every platform.
/// </summary>
[Collection(nameof(DotGame.Framework.Tests.Platform.GameThreadTests))]
public sealed class DrawingStateRulesTests
{
    private readonly FakeGraphics _platform = new();
    private readonly GraphicsService _graphics;
    private readonly SpriteBatch _batch;
    private readonly RenderTexture _texture;

    public DrawingStateRulesTests()
    {
        this._graphics = new GraphicsService(new Strategy<IGraphicsServiceStrategy>(this._platform));
        this._batch = this._graphics.CreateSpriteBatch();
        this._texture = this._graphics.CreateRenderTexture(new Vector2Int(4, 4));
    }

    [Fact]
    public void DuringABatch_ChangingTheRenderTarget_Throws()
    {
        using var batch = this._batch.Begin();

        Assert.Throws<InvalidOperationException>(() => this._graphics.UseRenderTarget(this._texture));
        Assert.Empty(this._platform.Targets);
    }

    [Fact]
    public void DuringABatch_TheScissorChanges_AndGoesBack()
    {
        using var batch = this._batch.Begin();

        using (this._graphics.UseScissor(new RectangleInt(1, 2, 3, 4)))
        {
            Assert.Equal(new RectangleInt(1, 2, 3, 4), this._graphics.ScissorRectangle);
        }

        Assert.Null(this._graphics.ScissorRectangle);
    }

    [Fact]
    public void AScissorMadeInABatch_CanOutlastIt()
    {
        var batch = this._batch.Begin();
        using var scissor = this._graphics.UseScissor(new RectangleInt(1, 2, 3, 4));
        batch.Dispose();

        Assert.Equal(new RectangleInt(1, 2, 3, 4), this._graphics.ScissorRectangle);
    }

    [Fact]
    public void UseScissor_WithNoTransform_RoundsOutward()
    {
        using var batch = this._batch.Begin();

        using (this._batch.UseScissor(new Rectangle(1.5f, 2.5f, 10f, 10f)))
        {
            Assert.Equal(new RectangleInt(1, 2, 11, 11), this._graphics.ScissorRectangle);
        }

        Assert.Null(this._graphics.ScissorRectangle);
    }

    [Fact]
    public void UseScissor_FollowsTheBatchsTransform()
    {
        using var batch = this._batch.Begin(Matrix3x2.CreateScale(2f) * Matrix3x2.CreateTranslation(10f, 20f));

        using (this._batch.UseScissor(new Rectangle(1f, 1f, 5f, 5f)))
        {
            Assert.Equal(new RectangleInt(12, 22, 10, 10), this._graphics.ScissorRectangle);
        }
    }

    [Fact]
    public void UseScissor_InAMirroredBatch_IsStillARectangle()
    {
        using var batch = this._batch.Begin(Matrix3x2.CreateScale(-1f, 1f));

        using (this._batch.UseScissor(new Rectangle(1f, 0f, 5f, 5f)))
        {
            Assert.Equal(new RectangleInt(-6, 0, 5, 5), this._graphics.ScissorRectangle);
        }
    }

    [Fact]
    public void UseScissor_InAQuarterTurn_IsARectangle_WithoutARoundingErrorsPixel()
    {
        using var batch = this._batch.Begin(Matrix3x2.CreateRotation(MathF.PI / 2f));

        using (this._batch.UseScissor(new Rectangle(1f, 2f, 3f, 4f)))
        {
            Assert.Equal(new RectangleInt(-6, 1, 4, 3), this._graphics.ScissorRectangle);
        }
    }

    [Fact]
    public void UseScissor_InARotatedBatch_Throws_AndTheBatchCarriesOn()
    {
        using var batch = this._batch.Begin(Matrix3x2.CreateRotation(0.1f));

        Assert.Throws<InvalidOperationException>(() => this._batch.UseScissor(new Rectangle(0f, 0f, 5f, 5f)));

        Assert.Null(this._graphics.ScissorRectangle);
    }

    [Fact]
    public void UseScissor_BeforeBegin_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => this._batch.UseScissor(new Rectangle(0f, 0f, 5f, 5f)));
    }

    [Fact]
    public void UseScissor_Empty_DrawsNothing()
    {
        using var batch = this._batch.Begin();

        using (this._batch.UseScissor(new Rectangle(5f, 5f, -3f, 10f)))
        {
            Assert.Equal(default(RectangleInt), this._graphics.ScissorRectangle);
        }
    }

    [Fact]
    public void UseScissor_Nests_LimitedByTheScissorAroundIt()
    {
        using var batch = this._batch.Begin();

        using (this._graphics.UseScissor(new RectangleInt(0, 0, 10, 10)))
        using (this._batch.UseScissor(new Rectangle(5f, 5f, 10f, 10f)))
        {
            Assert.Equal(new RectangleInt(5, 5, 5, 5), this._graphics.ScissorRectangle);
        }
    }

    [Fact]
    public void UseScissor_UsesThisBatchsTransform_NotTheLastOnes()
    {
        using (this._batch.Begin(Matrix3x2.CreateScale(2f)))
        {
        }

        using var batch = this._batch.Begin();

        using (this._batch.UseScissor(new Rectangle(1f, 1f, 1f, 1f)))
        {
            Assert.Equal(new RectangleInt(1, 1, 1, 1), this._graphics.ScissorRectangle);
        }
    }

    [Fact]
    public void DuringABatch_SettingWhatIsAlreadySet_IsFine()
    {
        using var scissor = this._graphics.UseScissor(new RectangleInt(0, 0, 1, 1));
        using var batch = this._batch.Begin();

        this._graphics.UseScissor(new RectangleInt(0, 0, 1, 1)).Dispose();
    }

    [Fact]
    public void AnEmptyScissor_DrawsNothing_WithOrWithoutOneAroundIt()
    {
        using (this._graphics.UseScissor(new RectangleInt(5, 5, -3, 10)))
        {
            Assert.Equal(default(RectangleInt), this._graphics.ScissorRectangle);
        }

        using (this._graphics.UseScissor(new RectangleInt(0, 0, 10, 10)))
        using (this._graphics.UseScissor(new RectangleInt(20, 20, 5, 5)))
        {
            Assert.Equal(default(RectangleInt), this._graphics.ScissorRectangle);
        }
    }

    [Fact]
    public void Scissors_Nest_LimitedByTheOneAroundThem_AndGoBack()
    {
        using (this._graphics.UseScissor(new RectangleInt(0, 0, 10, 10)))
        {
            using (this._graphics.UseScissor(new RectangleInt(5, 5, 10, 10)))
            {
                Assert.Equal(new RectangleInt(5, 5, 5, 5), this._graphics.ScissorRectangle);
            }

            Assert.Equal(new RectangleInt(0, 0, 10, 10), this._graphics.ScissorRectangle);
        }

        Assert.Null(this._graphics.ScissorRectangle);
    }

    [Fact]
    public void BetweenBatches_TheyChange_AndADisposedBatchLeavesNoTrace()
    {
        using (this._batch.Begin())
        {
        }

        this._graphics.UseRenderTarget(this._texture).Dispose();
        using var scissor = this._graphics.UseScissor(new RectangleInt(0, 0, 1, 1));

        var other = this._graphics.CreateSpriteBatch();
        other.Begin();
        other.Dispose();
    }

    // The render target scope reports through the framework's non-fatal path, once per run
    private static List<Exception> ReportsOf(
        Action test)
    {
        NonFatalError.ResetForTests();
        var reports = new List<Exception>();
        void OnReport(object? sender, NonFatalExceptionEventArgs e) => reports.Add(e.Exception);
        Trace.NonFatalException += OnReport;
        try
        {
            test();
        }
        finally
        {
            Trace.NonFatalException -= OnReport;
            NonFatalError.ResetForTests();
        }

        return reports;
    }

    [Fact]
    public void ScopesEndedInOrder_ReportNothing()
    {
        var other = this._graphics.CreateRenderTexture(new Vector2Int(4, 4));

        var reports = ReportsOf(() =>
        {
            var outer = this._graphics.UseRenderTarget(this._texture);
            var inner = this._graphics.UseRenderTarget(other);
            inner.Dispose();
            Assert.Same(this._texture, this._graphics.CurrentRenderTexture);
            outer.Dispose();
        });

        Assert.Empty(reports);
        Assert.Null(this._graphics.CurrentRenderTexture);
    }

    [Fact]
    public void ScopesEndedOutOfOrder_AreReported_AndTheLaterOneDoesNothing()
    {
        var other = this._graphics.CreateRenderTexture(new Vector2Int(4, 4));

        var reports = ReportsOf(() =>
        {
            var outer = this._graphics.UseRenderTarget(this._texture);
            var inner = this._graphics.UseRenderTarget(other);

            outer.Dispose();
            Assert.Null(this._graphics.CurrentRenderTexture);

            inner.Dispose();
            Assert.Null(this._graphics.CurrentRenderTexture);
        });

        Assert.Single(reports);
    }

    [Fact]
    public void DisposingAScopeTwice_DoesNothingTheSecondTime()
    {
        var other = this._graphics.CreateRenderTexture(new Vector2Int(4, 4));

        var reports = ReportsOf(() =>
        {
            using (this._graphics.UseRenderTarget(this._texture))
            {
                var inner = this._graphics.UseRenderTarget(other);
                inner.Dispose();
                inner.Dispose();
                Assert.Same(this._texture, this._graphics.CurrentRenderTexture);
            }
        });

        Assert.Empty(reports);
        Assert.Null(this._graphics.CurrentRenderTexture);
    }

    [Fact]
    public void AnEarlierScopeEnded_LeavesTheDepthRightForTheNextOnes()
    {
        var reports = ReportsOf(() =>
        {
            var first = this._graphics.UseRenderTarget(this._texture);
            first.Dispose();

            using (this._graphics.UseRenderTarget(this._texture))
            {
                Assert.Same(this._texture, this._graphics.CurrentRenderTexture);
            }
        });

        Assert.Empty(reports);
        Assert.Null(this._graphics.CurrentRenderTexture);
    }

    [Fact]
    public void WhenDrawingThrowsInsideTheBlock_ThatExceptionIsTheOneSeen_AndTheTargetGoesBack()
    {
        void DrawAndFail()
        {
            using (this._graphics.UseRenderTarget(this._texture))
            {
                using var drawing = this._batch.Begin();
                throw new TimeoutException("drawing failed");
            }
        }

        Assert.Throws<TimeoutException>(DrawAndFail);

        Assert.Null(this._graphics.CurrentRenderTexture);
        Assert.Equal([this._texture.Strategy, null], this._platform.Targets);
    }

    [Fact]
    public void ABatchsScope_EndedBeforeAShadersMadeInIt_Reports_AndTheNextBatchIsFine()
    {
        var reports = ReportsOf(() =>
        {
            var batch = this._batch.Begin();
            var shader = this._batch.UseShader(null);

            batch.Dispose();
            shader.Dispose();
        });

        Assert.Single(reports);
        using var next = this._batch.Begin();
    }

    [Fact]
    public void ABatchUsingATarget_DrawsToItClean_AndComesBackAsItWas()
    {
        var transform = Matrix3x2.CreateScale(2f);
        var shader = new Shader(new Strategy<IShaderStrategy>(new FakeShader()));
        using var batch = this._batch.Begin(new SpriteBatchSettings { Transform = transform, Blend = BlendMode.Additive });
        using var outerShader = this._batch.UseShader(shader);

        using (this._batch.UseRenderTarget(this._texture, new SpriteBatchSettings { Sampler = Sampler.PointClamp }))
        {
            Assert.Same(this._texture, this._graphics.CurrentRenderTexture);
            Assert.Null(this._platform.Batch.Transform);
            Assert.Equal(BlendMode.Alpha, this._platform.Batch.Blend);
            Assert.Equal(Sampler.PointClamp, this._platform.Batch.Sampler);
            Assert.Null(this._platform.Batch.Shader);
        }

        Assert.Null(this._graphics.CurrentRenderTexture);
        Assert.Equal(new Matrix4x4(transform), this._platform.Batch.Transform);
        Assert.Equal(BlendMode.Additive, this._platform.Batch.Blend);
        Assert.Same(shader.Strategy, this._platform.Batch.Shader);
    }

    [Fact]
    public void ABatchsScissor_FollowsItsTransform_InsideATargetToo()
    {
        using var batch = this._batch.Begin(Matrix3x2.CreateScale(2f));

        using (this._batch.UseRenderTarget(this._texture))
        using (this._batch.UseScissor(new Rectangle(1f, 1f, 1f, 1f)))
        {
            Assert.Equal(new RectangleInt(1, 1, 1, 1), this._graphics.ScissorRectangle);
        }

        using (this._batch.UseScissor(new Rectangle(1f, 1f, 1f, 1f)))
        {
            Assert.Equal(new RectangleInt(2, 2, 2, 2), this._graphics.ScissorRectangle);
        }
    }

    [Fact]
    public void ABatchUsingATarget_StartsWithNoScissor_AndGetsItBack()
    {
        using var outer = this._graphics.UseScissor(new RectangleInt(1, 2, 3, 4));
        using var batch = this._batch.Begin();

        using (this._batch.UseRenderTarget(this._texture))
        {
            Assert.Null(this._graphics.ScissorRectangle);
        }

        Assert.Equal(new RectangleInt(1, 2, 3, 4), this._graphics.ScissorRectangle);
    }

    [Fact]
    public void ATarget_StartsWithNoScissor_AndGetsItBack()
    {
        using var outer = this._graphics.UseScissor(new RectangleInt(1, 2, 3, 4));

        using (this._graphics.UseRenderTarget(this._texture))
        {
            Assert.Null(this._graphics.ScissorRectangle);

            using (this._graphics.UseScissor(new RectangleInt(5, 5, 5, 5)))
            {
                Assert.Equal(new RectangleInt(5, 5, 5, 5), this._graphics.ScissorRectangle);
            }
        }

        Assert.Equal(new RectangleInt(1, 2, 3, 4), this._graphics.ScissorRectangle);
    }

    [Fact]
    public void ABatchsTargets_Nest()
    {
        var other = this._graphics.CreateRenderTexture(new Vector2Int(4, 4));
        using var batch = this._batch.Begin();

        using (this._batch.UseRenderTarget(this._texture))
        {
            using (this._batch.UseRenderTarget(other))
            {
                Assert.Same(other, this._graphics.CurrentRenderTexture);
            }

            Assert.Same(this._texture, this._graphics.CurrentRenderTexture);
        }

        Assert.Null(this._graphics.CurrentRenderTexture);
    }

    [Fact]
    public void ABatchsTargetScope_DisposedAgain_DoesNothing()
    {
        using var batch = this._batch.Begin();
        var scope = this._batch.UseRenderTarget(this._texture);

        scope.Dispose();
        scope.Dispose();

        Assert.Equal([this._texture.Strategy, null], this._platform.Targets);
    }

    [Fact]
    public void UsingATarget_OutsideABatch_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => this._batch.UseRenderTarget(this._texture));
        Assert.Empty(this._platform.Targets);
    }

    [Fact]
    public void UsingATarget_WithAnotherBatchDrawing_Throws_AndTheBatchCarriesOn()
    {
        var other = this._graphics.CreateSpriteBatch();
        using var batch = this._batch.Begin();
        using var otherBatch = other.Begin();

        Assert.Throws<InvalidOperationException>(() => this._batch.UseRenderTarget(this._texture));

        Assert.Empty(this._platform.Targets);
    }

    [Fact]
    public void ABatchEndingBeforeItsTargetScope_Reports_AndTheTargetGoesBack()
    {
        var reports = ReportsOf(() =>
        {
            var batch = this._batch.Begin();
            var scope = this._batch.UseRenderTarget(this._texture);

            batch.Dispose();
            scope.Dispose();
        });

        Assert.Single(reports);
        Assert.Null(this._graphics.CurrentRenderTexture);
        using var next = this._batch.Begin();
    }

    [Fact]
    public void UseRenderTarget_GoesBackToWhatWasInUse()
    {
        var other = this._graphics.CreateRenderTexture(new Vector2Int(4, 4));

        using (this._graphics.UseRenderTarget(this._texture))
        {
            using (this._graphics.UseRenderTarget(other))
            {
                Assert.Same(other, this._graphics.CurrentRenderTexture);
            }

            Assert.Same(this._texture, this._graphics.CurrentRenderTexture);
        }

        Assert.Null(this._graphics.CurrentRenderTexture);
    }

    private sealed class FakeGraphics : IGraphicsServiceStrategy
    {
        public int MultisampleCount => 1;

        public int? PreferredFrameRate { get; set; }

        public List<Strategy<ITextureStrategy>?> Targets { get; } = [];

        public Task OnInitialize() => Task.CompletedTask;

        public FakeBatch Batch { get; private set; } = null!;

        public Strategy<ISpriteBatchStrategy> CreateSpriteBatch()
        {
            this.Batch = new FakeBatch();
            return new(this.Batch);
        }

        public Strategy<ITextureStrategy> CreateTexture(Vector2Int size, ReadOnlySpan<Color32> data) => throw new NotSupportedException();

        public Strategy<ITextureStrategy> CreateRenderTexture(Vector2Int size) => new(new FakeTexture());

        public void SetRenderTarget(Strategy<ITextureStrategy>? textureStrategy) => this.Targets.Add(textureStrategy);

        public Task ReadPixelsAsync(Strategy<ITextureStrategy> textureStrategy, Memory<Color32> destination) => Task.CompletedTask;

        public void Clear(Color color)
        {
        }

        public void SetScissorRectangle(RectangleInt? rectangle)
        {
        }
    }

    private sealed class FakeShader : IShaderStrategy
    {
        public Strategy<IShaderParameterStrategy> GetParameter(
            string name)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeBatch : ISpriteBatchStrategy
    {
        public Matrix4x4? Transform { get; private set; }

        public BlendMode Blend { get; private set; }

        public Sampler Sampler { get; private set; }

        public Strategy<IShaderStrategy>? Shader { get; private set; }

        public int TriangleCount { get; private set; }

        public void DrawTriangle(Strategy<ITextureStrategy> textureStrategy, in DotGame.Framework.Graphics.Vertex a, in DotGame.Framework.Graphics.Vertex b, in DotGame.Framework.Graphics.Vertex c) => this.TriangleCount++;

        public void Begin(Matrix4x4? transformMatrix = null, BlendMode blend = BlendMode.Alpha, Sampler sampler = Sampler.LinearClamp)
        {
            this.Transform = transformMatrix;
            this.Blend = blend;
            this.Sampler = sampler;
            this.Shader = null;
        }

        public void SetShader(Strategy<IShaderStrategy>? shader)
        {
            this.Shader = shader;
        }

        public void Draw(Strategy<ITextureStrategy> textureStrategy, Vector2 position, Color color, Angle rotation, Vector2 origin, Vector2 scale, Rectangle? sourceRectangle, SpriteFlip flip, in ShaderCustomData customData)
        {
        }

        public void End()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeTexture : ITextureStrategy
    {
        public void SetData(DotGame.Framework.RectangleInt region, System.ReadOnlySpan<DotGame.Framework.Color32> data)
        {
            this.SetDataCalls++;
        }

        public int SetDataCalls { get; private set; }

        public Vector2Int Size => new(4, 4);

        public void Dispose()
        {
        }
    }
}
