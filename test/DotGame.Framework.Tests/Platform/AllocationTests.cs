// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Diagnostics;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Common.Graphics;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;
using Xunit;
using static DotGame.Framework.Tests.Platform.TestPlatform;

namespace DotGame.Framework.Tests.Platform;

/// <summary>
/// "A normal frame allocates nothing" as a claim something keeps. Each guard warms up first, so
/// first-call costs and input buffers reaching their working size don't count, and then measures
/// the framework's whole side of many frames.
/// </summary>
[Collection(nameof(GameThreadTests))]
public sealed class AllocationTests : IDisposable
{
    private const int WarmUpFrames = 200;
    private const int MeasuredFrames = 1000;
    private const int SpritesPerFrame = 64;

    public void Dispose()
    {
        StatsCollector.SetMode(StatsOverlayMode.Off);
    }

    [Fact]
    public void Tick_OfAnIdleFrame_AllocatesNothing()
    {
        Assert.Equal(0, FrameAllocations(input: false, StatsOverlayMode.Off));
    }

    [Fact]
    public void Tick_WithInput_AllocatesNothing()
    {
        Assert.Equal(0, FrameAllocations(input: true, StatsOverlayMode.Off));
    }

    [Theory]
    [InlineData(StatsOverlayMode.FpsOnly)]
    [InlineData(StatsOverlayMode.Detailed)]
    public void Tick_WhileCollectingStatistics_AllocatesNothing(
        StatsOverlayMode mode)
    {
        Assert.Equal(0, FrameAllocations(input: true, mode));
    }

    [Theory]
    [InlineData(StatsOverlayMode.Off)]
    [InlineData(StatsOverlayMode.Detailed)]
    public void UseRenderTarget_AllocatesNothing(
        StatsOverlayMode mode)
    {
        StatsCollector.SetMode(mode);
        var graphics = new GraphicsService(CreateServices().Graphics);
        var outer = new RenderTexture(new Strategy<ITextureStrategy>(new SizedTexture()));
        var inner = new RenderTexture(new Strategy<ITextureStrategy>(new SizedTexture()));

        for (int i = 0; i < WarmUpFrames; i++)
        {
            UseNestedRenderTargets(graphics, outer, inner);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredFrames; i++)
        {
            UseNestedRenderTargets(graphics, outer, inner);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // Both ways to hold a target: a block, and a value kept and disposed later
    private static void UseNestedRenderTargets(
        GraphicsService graphics,
        RenderTexture outer,
        RenderTexture inner)
    {
        using (graphics.UseRenderTarget(outer))
        {
            var kept = graphics.UseRenderTarget(inner);
            kept.Dispose();
        }
    }

    [Theory]
    [InlineData(StatsOverlayMode.Off)]
    [InlineData(StatsOverlayMode.Detailed)]
    public void ABatchsScopes_AllocateNothing(
        StatsOverlayMode mode)
    {
        StatsCollector.SetMode(mode);
        var graphics = new GraphicsService(CreateServices().Graphics);
        using var batch = graphics.CreateSpriteBatch();
        var target = new RenderTexture(new Strategy<ITextureStrategy>(new SizedTexture()));
        var shader = new Shader(new Strategy<IShaderStrategy>(new ScopeShader()));

        for (int i = 0; i < WarmUpFrames; i++)
        {
            UseBatchScopes(batch, graphics, target, shader);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredFrames; i++)
        {
            UseBatchScopes(batch, graphics, target, shader);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // Each scope a batch has, and nested, as what ends one puts back what the next one saved
    private static void UseBatchScopes(
        SpriteBatch batch,
        GraphicsService graphics,
        RenderTexture target,
        Shader shader)
    {
        using var drawing = batch.Begin(Matrix3x2.CreateScale(2f));

        using (batch.UseShader(shader))
        using (batch.UseScissor(new Rectangle(1f, 2f, 3f, 4f)))
        using (graphics.UseScissor(new RectangleInt(0, 0, 8, 8)))
        using (batch.UseRenderTarget(target, new SpriteBatchSettings { Blend = BlendMode.Additive }))
        using (batch.UseShader(null))
        using (batch.UseRenderTarget(target))
        {
        }
    }

    private sealed class ScopeShader : IShaderStrategy
    {
        public Strategy<IShaderParameterStrategy> GetParameter(
            string name)
        {
            throw new NotSupportedException();
        }
    }

    [Theory]
    [InlineData(StatsOverlayMode.Off)]
    [InlineData(StatsOverlayMode.Detailed)]
    public void Tick_DrawingToRenderTargets_AllocatesNothing(
        StatsOverlayMode mode)
    {
        long allocated = -1;

        OnGameThread(() =>
        {
            StatsCollector.SetMode(mode);

            using var batch = new SpriteBatch(new Strategy<ISpriteBatchStrategy>(new VertexWritingBatch()));
            using var texture = new Texture(new Strategy<ITextureStrategy>(new SizedTexture()));
            var game = new RenderTargetGame(batch, texture);
            var host = new GameHost(game);

            host.BeginStartup(CreateServices());
            PumpUntil(() => host.IsStarted);

            double now = 0;
            for (int i = 0; i < WarmUpFrames; i++)
            {
                Frame(host, input: false, now += 16);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredFrames; i++)
            {
                Frame(host, input: false, now += 16);
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(WarmUpFrames + MeasuredFrames, game.Draws);
        });

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SpriteBatchDraw_AllocatesNothing()
    {
        using var batch = new SpriteBatch(new Strategy<ISpriteBatchStrategy>(new VertexWritingBatch()));
        using var texture = new Texture(new Strategy<ITextureStrategy>(new SizedTexture()));

        DrawSprites(batch, texture);
        DrawSprites(batch, texture);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < MeasuredFrames; i++)
        {
            DrawSprites(batch, texture);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static long FrameAllocations(
        bool input,
        StatsOverlayMode mode)
    {
        long allocated = -1;

        OnGameThread(() =>
        {
            StatsCollector.SetMode(mode);

            using var batch = new SpriteBatch(new Strategy<ISpriteBatchStrategy>(new VertexWritingBatch()));
            using var texture = new Texture(new Strategy<ITextureStrategy>(new SizedTexture()));
            var game = new SpritesGame(batch, texture);
            var host = new GameHost(game);

            host.BeginStartup(CreateServices());
            PumpUntil(() => host.IsStarted);

            double now = 0;
            for (int i = 0; i < WarmUpFrames; i++)
            {
                Frame(host, input, now += 16);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredFrames; i++)
            {
                Frame(host, input, now += 16);
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(WarmUpFrames + MeasuredFrames, game.Draws);
        });

        return allocated;
    }

    private static void Frame(
        GameHost host,
        bool input,
        double now)
    {
        if (input)
        {
            // NOTE: A Debug build traces every press (TRACE_INPUT), which is the only allocation
            // it adds, so it's measured without them
#if !DEBUG
            host.QueuePointerEvent(new PointerEventData { State = PointerEventKind.Began, PointerId = 1, PointerType = PointerDevice.Touch, Position = Vector2.One });
            host.QueuePointerEvent(new PointerEventData { State = PointerEventKind.Ended, PointerId = 1, PointerType = PointerDevice.Touch, Position = Vector2.One });
#endif
            host.QueuePointerEvent(new PointerEventData { State = PointerEventKind.Moved, PointerId = 1, PointerType = PointerDevice.Touch, Position = Vector2.One });
            host.QueuePointerEvent(new PointerEventData { State = PointerEventKind.Moved, PointerId = 0, PointerType = PointerDevice.Mouse, Position = Vector2.One });
            host.QueueKeyEvent(new KeyEventData { State = KeyEventKind.Pressed, Key = Key.Space });
            host.QueueKeyEvent(new KeyEventData { State = KeyEventKind.Released, Key = Key.Space });
        }

        host.Tick(At(now), canDraw: true);
    }

    private static void DrawSprites(
        SpriteBatch batch,
        Texture texture)
    {
        using var drawing = batch.Begin();
        for (int i = 0; i < SpritesPerFrame; i++)
        {
            batch.Draw(texture, new Vector2(i, i), Color.White);
            batch.Draw(texture, new Vector2(i, i), Color.Red, Angle.QuarterCircle, new Vector2(4f, 4f), new Vector2(2f, 2f), new Rectangle(0f, 0f, 8f, 8f));
        }
    }

    // Every way a frame holds a target: a block, nested, a value kept across methods, and the
    // engine's stack of them
    private sealed class RenderTargetGame(SpriteBatch batch, Texture texture) : Game
    {
        private readonly RenderTexture _outer = new(new Strategy<ITextureStrategy>(new SizedTexture()));
        private readonly RenderTexture _inner = new(new Strategy<ITextureStrategy>(new SizedTexture()));
        private readonly System.Collections.Generic.Stack<(RenderTexture Target, RenderTargetScope Scope)> _stack = new();
        private RenderTargetScope _kept;

        public int Draws { get; private set; }

        protected override void OnDraw()
        {
            this.Draws++;

            this._kept = this.Graphics.UseRenderTarget(this._outer);
            this.DrawNested();
            this._kept.Dispose();

            this._stack.Push((this._outer, this.Graphics.UseRenderTarget(this._outer)));
            this._stack.Push((this._inner, this.Graphics.UseRenderTarget(this._inner)));
            DrawSprites(batch, texture);
            this._stack.Pop().Scope.Dispose();
            this._stack.Pop().Scope.Dispose();

            DrawSprites(batch, texture);
        }

        private void DrawNested()
        {
            using (this.Graphics.UseRenderTarget(this._inner))
            {
                this.Graphics.Clear(Color.Black);
                DrawSprites(batch, texture);
            }

            DrawSprites(batch, texture);
        }
    }

    private sealed class SpritesGame(SpriteBatch batch, Texture texture) : Game
    {
        public int Draws { get; private set; }

        protected override void OnDraw()
        {
            this.Draws++;
            DrawSprites(batch, texture);
        }
    }

    private sealed class SizedTexture : ITextureStrategy
    {
        public void SetData(DotGame.Framework.RectangleInt region, System.ReadOnlySpan<DotGame.Framework.Color32> data)
        {
            this.SetDataCalls++;
        }

        public int SetDataCalls { get; private set; }

        public Vector2Int Size => new(64, 64);

        public void Dispose()
        {
        }
    }

    // A platform's sprite batch, as far as the managed side of one goes: every platform writes
    // each sprite with SpriteVertex
    private sealed class VertexWritingBatch : ISpriteBatchStrategy
    {
        public int TriangleCount { get; private set; }

        public void DrawTriangle(Strategy<ITextureStrategy> textureStrategy, in DotGame.Framework.Graphics.Vertex a, in DotGame.Framework.Graphics.Vertex b, in DotGame.Framework.Graphics.Vertex c) => this.TriangleCount++;

        private readonly SpriteVertex[] _vertices = new SpriteVertex[4 * 2 * SpritesPerFrame];
        private Matrix4x4 _transform;
        private int _count;

        public void Begin(Matrix4x4? transformMatrix = null, BlendMode blend = BlendMode.Alpha, Sampler sampler = Sampler.LinearClamp)
        {
            this._transform = transformMatrix ?? Matrix4x4.Identity;
            this._count = 0;
        }

        public void SetShader(Strategy<IShaderStrategy>? shader)
        {
        }

        public void Draw(Strategy<ITextureStrategy> textureStrategy, Vector2 position, Color color, Angle rotation, Vector2 origin, Vector2 scale, Rectangle? sourceRectangle, SpriteFlip flip, in ShaderCustomData customData)
        {
            SpriteVertex.WriteSprite(
                ref this._vertices[this._count],
                textureStrategy.Interface.Size,
                position,
                color,
                rotation,
                origin,
                scale,
                sourceRectangle,
                SpriteFlip.None,
                in this._transform,
                in customData);
            this._count += 4;
        }

        public void End()
        {
            StatsCollector.CountDraw(this._count / 4);
        }

        public void Dispose()
        {
        }
    }
}
