// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Graphics;

/// <summary>Draws sprites, and triangles, efficiently: while the scope from <see cref="Begin()"/> lasts, draws that can share state are combined.</summary>
public sealed class SpriteBatch : IDisposable
{
    private bool _isDrawing;
    private readonly GraphicsService? _graphics;

    internal SpriteBatch(
        Strategy<ISpriteBatchStrategy> spriteBatchStrategy,
        GraphicsService? graphics = null)
    {
        ArgumentNullException.ThrowIfNull(spriteBatchStrategy);

        this.Strategy = spriteBatchStrategy;
        this._graphics = graphics;
    }

    [PlatformContract]
    internal readonly Strategy<ISpriteBatchStrategy> Strategy;

    /// <summary>Whether it has been released.</summary>
    public bool IsDisposed => this.Strategy.IsDisposed;

    /// <summary>
    /// The graphics service this batch draws with.
    /// </summary>
    public GraphicsService Graphics => this._graphics
        ?? throw new InvalidOperationException("This sprite batch has no graphics service.");

    // How close an edge is to a whole pixel to be on it, so a rounding error doesn't add a pixel
    private const float ScissorEdgeTolerance = 1f / 1024f;

    // How far from zero a transform's terms can be for a rectangle to stay a rectangle
    private const float ScissorAxisTolerance = 1e-5f;

    private Matrix3x2 _transform = Matrix3x2.Identity;
    private SpriteBatchSettings _settings;

    // How many of this batch's render target scopes are open
    private int _targetDepth;

    // Which batch this is, so that a scope ends only its own
    private int _batchNumber;

    // The shader the next sprite draws with, and how many scopes are open
    private Shader? _shader;
    private int _shaderDepth;

    /// <summary>
    /// Begins a sprite batch with the default settings.
    /// </summary>
    /// <remarks>
    /// Must be called before any Draw() calls. The batch lasts until the returned scope is disposed:
    /// <c>using var batch = spriteBatch.Begin();</c>, or a <c>using</c> block for more than one batch in
    /// a method. Throwing the scope away fails the build.
    /// </remarks>
    public SpriteBatchScope Begin()
    {
        return this.Begin(new SpriteBatchSettings());
    }

    /// <summary>
    /// Begins a sprite batch that applies a transform, such as a camera's, to every sprite.
    /// </summary>
    /// <inheritdoc cref="Begin()" path="/remarks"/>
    public SpriteBatchScope Begin(
        Matrix3x2 transform)
    {
        return this.Begin(new SpriteBatchSettings { Transform = transform });
    }

    /// <summary>
    /// Begins a sprite batch with the specified settings.
    /// </summary>
    /// <inheritdoc cref="Begin()" path="/remarks"/>
    public SpriteBatchScope Begin(
        in SpriteBatchSettings settings)
    {
        StatsCollector.EnterFramework();

        if (this._isDrawing)
        {
            throw new InvalidOperationException("The scope from SpriteBatch.Begin() must end before calling Begin() again.");
        }

        this.BeginOnPlatform(in settings);

        if (this._graphics is not null)
        {
            this._graphics.DrawingBatches++;
        }

        this._batchNumber++;
        this._isDrawing = true;

        StatsCollector.ExitFramework();

        return new SpriteBatchScope(this, this._batchNumber);
    }

    private void BeginOnPlatform(
        in SpriteBatchSettings settings)
    {
        this.Strategy.Interface.Begin(
            (settings.Transform is { } transform) ? new Matrix4x4(transform) : null,
            settings.Blend,
            settings.Sampler);

        this._settings = settings;
        this._shader = null;
        this._transform = settings.Transform ?? Matrix3x2.Identity;
    }

    /// <summary>
    /// Draws to a render texture until the scope ends, then comes back to what this batch was
    /// drawing, as it was: <c>using (spriteBatch.UseRenderTarget(texture)) { ... }</c>. So code handed
    /// a batch can draw to a texture without knowing how the batch was begun. These nest.
    /// </summary>
    /// <remarks>
    /// A target has its own coordinates, so the scope starts clean: with
    /// <paramref name="settings"/>, the default shader and no scissor. All of them come back
    /// when it ends. Sprites drawn before it keep the target they were drawn for. For a target that
    /// spans batches, see <see cref="GraphicsService.UseRenderTarget"/>.
    /// <para>
    /// It allocates nothing, but changing the target is one of the costlier things a GPU does, and this
    /// changes it twice, the more so on a phone. A few a frame are fine; one a sprite isn't. Redrawing
    /// a texture only when it changes, or before the frame's main batch, costs less.
    /// </para>
    /// </remarks>
    /// <param name="renderTexture">The texture to draw to.</param>
    /// <param name="settings">How to draw to it. The default is no transform, alpha blending and linear sampling.</param>
    /// <exception cref="InvalidOperationException">This batch hasn't begun, or another is drawing.</exception>
    public RenderTargetScope UseRenderTarget(
        RenderTexture renderTexture,
        in SpriteBatchSettings settings = default)
    {
        ArgumentNullException.ThrowIfNull(renderTexture);

        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before UseRenderTarget().");
        }

        var graphics = this.Graphics;
        if (graphics.DrawingBatches > 1)
        {
            throw new InvalidOperationException("Another sprite batch is drawing: end it before changing the render target.");
        }

        StatsCollector.EnterFramework();

        var outerSettings = this._settings;
        var outerShader = this._shader;
        int outerShaderDepth = this._shaderDepth;

        this.Strategy.Interface.End();
        graphics.DrawingBatches--;
        var target = graphics.UseRenderTarget(renderTexture);
        graphics.DrawingBatches++;
        this.BeginOnPlatform(in settings);

        this._shaderDepth = 0;
        this._targetDepth++;

        StatsCollector.ExitFramework();

        return target.ForBatch(this, this._batchNumber, this._targetDepth, in outerSettings, outerShader, outerShaderDepth);
    }

    internal void RestoreRenderTarget(
        in RenderTargetScope scope)
    {
        // Already ended, by disposing it again, by one made before it ending first, or with the
        // batch, which has reported it. The target it holds goes back on its own terms
        if ((!this._isDrawing) || (scope.BatchNumber != this._batchNumber) || this.IsDisposed || (scope.BatchDepth > this._targetDepth))
        {
            scope.RestoreTarget();
            return;
        }

        StatsCollector.EnterFramework();

        if (scope.BatchDepth < this._targetDepth)
        {
            Diagnostics.NonFatalError.Report(
                0x2f90d6a3,
                new InvalidOperationException("A sprite batch's render target scope ended before one made after it."));
        }

        var graphics = this.Graphics;
        this.Strategy.Interface.End();
        graphics.DrawingBatches--;
        scope.RestoreTarget();
        graphics.DrawingBatches++;
        var settings = scope.BatchSettings;
        this.BeginOnPlatform(in settings);

        this.ChangeShader(scope.BatchShader);
        this._shaderDepth = scope.BatchShaderDepth;
        this._targetDepth = scope.BatchDepth - 1;

        StatsCollector.ExitFramework();
    }

    /// <summary>
    /// Draws with a shader until the scope ends: <c>using (spriteBatch.UseShader(shader)) { ... }</c>.
    /// Each batch starts with the default sprite shader. These nest, and each goes back to the shader
    /// before it, so code that draws with one needn't know what its caller draws with.
    /// </summary>
    /// <remarks>
    /// Nothing draws until a sprite needs the change, so a scope that draws nothing, or sprites that
    /// use the shader already in use, cost nothing. Sprites either side of a change can't share a draw.
    /// </remarks>
    /// <param name="shader">The shader, or <c>null</c> for the default one.</param>
    /// <exception cref="InvalidOperationException">The batch hasn't begun.</exception>
    public ShaderScope UseShader(
        Shader? shader)
    {
        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before UseShader().");
        }

        var scope = new ShaderScope(this, this._shader, this._shaderDepth + 1);
        this.ChangeShader(shader);
        this._shaderDepth++;
        return scope;
    }

    internal void RestoreShader(
        Shader? previous,
        int depth)
    {
        // Already ended, by disposing it again, or by one made before it ending first
        if (depth > this._shaderDepth)
        {
            return;
        }

        if (depth < this._shaderDepth)
        {
            Diagnostics.NonFatalError.Report(
                0x3c71e0b6,
                new InvalidOperationException("A shader's scope ended before one made after it."));
        }

        this.ChangeShader(previous);
        this._shaderDepth = depth - 1;
    }

    private void ChangeShader(
        Shader? shader)
    {
        if (!ReferenceEquals(this._shader, shader))
        {
            this._shader = shader;
            this.Strategy.Interface.SetShader(shader?.Strategy);
        }
    }

    /// <summary>
    /// Limits drawing to a rectangle in this batch's own coordinates until the scope ends:
    /// <c>using (spriteBatch.UseScissor(rectangle)) { ... }</c>. The batch's transform applies to it as it
    /// does to sprites, so code that draws needn't know its caller's camera. It's
    /// <see cref="GraphicsService.UseScissor"/> with the rectangle converted, its edges rounded outward
    /// so that nothing inside it is lost, and so it nests the same way and can outlast the batch. For a
    /// limit fixed on the screen, or one that spans batches, use that instead.
    /// </summary>
    /// <remarks>
    /// The limit is fixed when it's made, so it doesn't follow a later change of transform. Each one
    /// splits the draw.
    /// </remarks>
    /// <param name="rectangle">The area. An empty one, of no positive size, draws nothing.</param>
    /// <exception cref="InvalidOperationException">
    /// The batch hasn't begun, or its transform rotates by other than quarter turns, or skews, so that
    /// the area isn't a rectangle on the screen, which a scissor can't be.
    /// </exception>
    public ScissorScope UseScissor(
        Rectangle rectangle)
    {
        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before UseScissor().");
        }

        var t = this._transform;
        bool isAligned = ((MathF.Abs(t.M12) <= ScissorAxisTolerance) && (MathF.Abs(t.M21) <= ScissorAxisTolerance))
            || ((MathF.Abs(t.M11) <= ScissorAxisTolerance) && (MathF.Abs(t.M22) <= ScissorAxisTolerance));
        if (!isAligned)
        {
            throw new InvalidOperationException("A scissor must stay a rectangle on the screen, and this batch's transform rotates or skews it: use GraphicsService.UseScissor for one fixed on the screen.");
        }

        if (rectangle.IsEmpty)
        {
            return this.Graphics.UseScissor(default);
        }

        var first = Vector2.Transform(new Vector2(rectangle.Left, rectangle.Top), t);
        var second = Vector2.Transform(new Vector2(rectangle.Right, rectangle.Bottom), t);
        int left = (int)MathF.Floor(MathF.Min(first.X, second.X) + ScissorEdgeTolerance);
        int top = (int)MathF.Floor(MathF.Min(first.Y, second.Y) + ScissorEdgeTolerance);
        int right = (int)MathF.Ceiling(MathF.Max(first.X, second.X) - ScissorEdgeTolerance);
        int bottom = (int)MathF.Ceiling(MathF.Max(first.Y, second.Y) - ScissorEdgeTolerance);

        return this.Graphics.UseScissor(new RectangleInt(left, top, right - left, bottom - top));
    }

    /// <summary>
    /// Queues a texture, or a part of it, to be drawn during this batch.
    /// </summary>
    /// <param name="texture">The texture to draw.</param>
    /// <param name="position">Where the texture's <paramref name="origin"/> is drawn.</param>
    /// <param name="color">The tint the texture's colors are multiplied by.</param>
    /// <param name="rotation">The rotation about <paramref name="origin"/>.</param>
    /// <param name="origin">
    /// The point the texture is positioned, rotated and scaled about, in the texture's (or the
    /// <paramref name="sourceRectangle"/>'s) pixels from its top-left corner.
    /// </param>
    /// <param name="scale">
    /// The scale along each axis, or <c>null</c> for none. A negative component mirrors the
    /// texture about <paramref name="origin"/>: <c>new Vector2(-1, 1)</c> flips it horizontally,
    /// and with the origin at the texture's center it stays in place.
    /// </param>
    /// <param name="sourceRectangle">
    /// A part of <paramref name="texture"/> to draw, in the texture's own pixel coordinates, or
    /// <c>null</c> to draw all of it.
    /// </param>
    /// <param name="flip">
    /// Mirrors the image inside the rectangle it's drawn in, so it flips in place whatever the
    /// origin: a character facing the other way.
    /// </param>
    /// <param name="customData">
    /// Values passed to the shader with this sprite, read as <c>TEXCOORD1</c>. The
    /// default sprite shader ignores them.
    /// </param>
    /// <remarks>
    /// <see cref="Begin()"/> must be called before this method. This is the
    /// framework's low-level draw primitive; higher-level "source"
    /// abstractions (atlas regions, trimmed sprites, nine-patches, etc.)
    /// belong in libraries on top of it, as extension methods that call
    /// this one.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Draw(
        Texture texture,
        Vector2 position,
        Color color,
        Angle rotation = default,
        Vector2 origin = default,
        Vector2? scale = null,
        Rectangle? sourceRectangle = null,
        SpriteFlip flip = SpriteFlip.None,
        in ShaderCustomData customData = default)
    {
        ArgumentNullException.ThrowIfNull(texture);

        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before Draw().");
        }

        this.Strategy.Interface.Draw(texture.Strategy, position, color, rotation, origin, scale ?? Vector2.One, sourceRectangle, flip, in customData);
    }

    /// <summary>
    /// Draws triangles, three vertices each, in order with the sprites around them: for trails,
    /// terrain, deformed sprites and shapes. They go through the batch's transform, shader, blend
    /// and sampler as sprites do. Draw either way round: nothing is culled.
    /// </summary>
    /// <remarks>
    /// Each costs what a sprite costs. For triangles of plain color, draw them with a 1x1 white
    /// texture.
    /// </remarks>
    /// <exception cref="ArgumentException">The number of vertices isn't a multiple of three.</exception>
    public void DrawTriangles(
        Texture texture,
        ReadOnlySpan<Vertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(texture);

        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before DrawTriangles().");
        }

        if ((vertices.Length % 3) != 0)
        {
            throw new ArgumentException("Triangles take three vertices each.", nameof(vertices));
        }

        for (int i = 0; i < vertices.Length; i += 3)
        {
            this.Strategy.Interface.DrawTriangle(texture.Strategy, in vertices[i], in vertices[i + 1], in vertices[i + 2]);
        }
    }

    /// <summary>
    /// Draws triangles from shared vertices, three indices each.
    /// </summary>
    /// <exception cref="ArgumentException">The number of indices isn't a multiple of three.</exception>
    public void DrawTriangles(
        Texture texture,
        ReadOnlySpan<Vertex> vertices,
        ReadOnlySpan<ushort> indices)
    {
        ArgumentNullException.ThrowIfNull(texture);

        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before DrawTriangles().");
        }

        if ((indices.Length % 3) != 0)
        {
            throw new ArgumentException("Triangles take three indices each.", nameof(indices));
        }

        for (int i = 0; i < indices.Length; i += 3)
        {
            this.Strategy.Interface.DrawTriangle(texture.Strategy, in vertices[indices[i]], in vertices[indices[i + 1]], in vertices[indices[i + 2]]);
        }
    }

    // Ends the batch, unless the scope has ended it already
    internal void EndBatch(
        int batchNumber)
    {
        // NOTE: Releasing the batch has ended it, so a scope that outlasts it has nothing to do
        if ((!this._isDrawing) || (batchNumber != this._batchNumber) || this.IsDisposed)
        {
            return;
        }

        StatsCollector.EnterFramework();

        if (this._shaderDepth > 0)
        {
            Diagnostics.NonFatalError.Report(
                0x6a14c9f2,
                new InvalidOperationException("A sprite batch's scope ended before a shader's scope made after it."));
            this._shaderDepth = 0;
        }

        if (this._targetDepth > 0)
        {
            Diagnostics.NonFatalError.Report(
                0x51c8ae07,
                new InvalidOperationException("A sprite batch's scope ended before a render target's scope made in it."));
            this._targetDepth = 0;
        }

        this.Strategy.Interface.End();

        if (this._graphics is not null)
        {
            this._graphics.DrawingBatches--;
        }

        this._isDrawing = false;

        StatsCollector.ExitFramework();
    }

    /// <summary>Releases the batch. The shared batch belongs to the graphics service.</summary>
    public void Dispose()
    {
        if (this._isDrawing && (this._graphics is not null))
        {
            this._isDrawing = false;
            this._graphics.DrawingBatches--;
        }

        GC.SuppressFinalize(this);
        this.Strategy.Dispose();
    }
}
