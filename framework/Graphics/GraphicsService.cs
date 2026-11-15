// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Graphics;

/// <summary>Draws: sprite batches, textures, render targets and the scissor. Reach it as <c>Game.Graphics</c>.</summary>
public sealed class GraphicsService
{
    internal GraphicsService(
        Strategy<IGraphicsServiceStrategy> graphicsServiceStrategy)
    {
        ArgumentNullException.ThrowIfNull(graphicsServiceStrategy);

        this.Strategy = graphicsServiceStrategy;
    }

    private readonly Strategy<IGraphicsServiceStrategy> Strategy;

    /// <summary>A sprite batch for everyday drawing, so a game needn't make its own.</summary>
    public SpriteBatch SharedSpriteBatch { get; private set; } = null!;

    /// <summary>
    /// The samples per pixel the screen is drawn with, which the game's options asked for and
    /// the device allowed; 1 means none. Render textures always have one.
    /// </summary>
    public int MultisampleCount => this.Strategy.Interface.MultisampleCount;

    /// <summary>
    /// The graphics device was lost and has come back (a mobile GL context, or a web page's). The
    /// platform has restored textures and shaders loaded through content. Rebuild anything the
    /// game drew itself: a <see cref="RenderTexture"/>'s contents are gone.
    /// </summary>
    public event EventHandler? DeviceReset;

    internal void SetPreferredFrameRate(
        int? frameRate)
    {
        this.Strategy.Interface.PreferredFrameRate = frameRate;
        StatsCollector.SetPreferredFrameRate(frameRate);
    }

    internal void RaiseDeviceReset()
    {
        this.DeviceReset?.Invoke(this, EventArgs.Empty);
    }

    internal async Task Initialize()
    {
        await this.Strategy.Interface.OnInitialize();
        this.SharedSpriteBatch = new SpriteBatch(this.Strategy.Interface.CreateSpriteBatch(), this);
    }

    /// <summary>Makes a sprite batch of your own, which you dispose.</summary>
    public SpriteBatch CreateSpriteBatch()
    {
        return new SpriteBatch(this.Strategy.Interface.CreateSpriteBatch(), this);
    }

    /// <summary>Makes a texture from pixels: RGBA, the top row first, premultiplied. You dispose it.</summary>
    public Texture CreateTexture(
        Vector2Int size,
        ReadOnlySpan<Color32> data)
    {
        return new Texture(this.Strategy.Interface.CreateTexture(size, data));
    }

    /// <summary>Makes a texture to draw into. You dispose it, and its contents are lost if the device resets.</summary>
    public RenderTexture CreateRenderTexture(
        Vector2Int size)
    {
        return new RenderTexture(this.Strategy.Interface.CreateRenderTexture(size));
    }

    /// <summary>
    /// Draws to a render texture until the returned value is disposed, then goes back to what was
    /// being drawn to: <c>using (Graphics.UseRenderTarget(texture)) { ... }</c>. These nest, so code
    /// drawing to a texture needn't know what its caller was drawing to. A value can also be kept
    /// and disposed later, last made first disposed; ending one early ends those made after it.
    /// </summary>
    /// <remarks>
    /// A target starts with no scissor, as a scissor's coordinates are its own target's, and the
    /// scissor comes back with the target before it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A sprite batch is drawing: its <see cref="SpriteBatch.UseRenderTarget"/> changes the target
    /// without ending it.
    /// </exception>
    public RenderTargetScope UseRenderTarget(
        RenderTexture renderTexture)
    {
        ArgumentNullException.ThrowIfNull(renderTexture);

        var scope = new RenderTargetScope(this, this.CurrentRenderTexture, this._renderTargetDepth + 1, this._scissorRectangle, this._scissorDepth);
        if (!ReferenceEquals(this.CurrentRenderTexture, renderTexture))
        {
            StatsCollector.EnterFramework();
            this.ThrowIfDrawing("the render target");
            this.ChangeRenderTarget(renderTexture);
            this.ChangeScissor(null);
            StatsCollector.ExitFramework();
        }

        this._renderTargetDepth++;
        return scope;
    }

    // How many render target scopes haven't ended
    private int _renderTargetDepth;

    /// <summary>
    /// Goes back to <paramref name="previous"/>, or the window, as a <see cref="RenderTargetScope"/>
    /// ends. It doesn't throw, as it may be running because drawing threw: that exception is the
    /// one that matters. A batch left open is still a mistake, so it's reported instead.
    /// </summary>
    internal void RestoreRenderTarget(
        RenderTexture? previous,
        int depth,
        RectangleInt? previousScissor,
        int scissorDepth)
    {
        // Already ended, by disposing it again, or by one made before it ending first
        if (depth > this._renderTargetDepth)
        {
            return;
        }

        if (depth < this._renderTargetDepth)
        {
            Diagnostics.NonFatalError.Report(
                0x4c7e21b4,
                new InvalidOperationException("A render target's scope ended before one made after it: end the last one made first."));
        }

        this._renderTargetDepth = depth - 1;

        if (ReferenceEquals(this.CurrentRenderTexture, previous))
        {
            return;
        }

        if (this.DrawingBatches > 0)
        {
            Diagnostics.NonFatalError.Report(
                0x4c7e21b3,
                new InvalidOperationException("A sprite batch was still drawing when UseRenderTarget's block ended: end it inside the block."));
        }

        StatsCollector.EnterFramework();
        this.ChangeRenderTarget(previous);
        this.ChangeScissor(previousScissor);
        this._scissorDepth = scissorDepth;
        StatsCollector.ExitFramework();
    }

    private void ChangeRenderTarget(
        RenderTexture? renderTexture)
    {
        this.CurrentRenderTexture = renderTexture;
        this.Strategy.Interface.SetRenderTarget(renderTexture?.Strategy);
    }

    /// <summary>
    /// Reads a render texture's pixels into <paramref name="destination"/>, one per texel:
    /// RGBA, premultiplied as drawn, the top row first. The pixels are those drawn to the
    /// texture so far this frame, once they've been drawn, so the task completes later (on the
    /// game thread), when the frame's drawing has run.
    /// </summary>
    /// <param name="renderTexture">The texture to read.</param>
    /// <param name="destination">
    /// Where the pixels go: at least as many as the texture has. It must stay untouched until
    /// the task completes.
    /// </param>
    public Task ReadPixelsAsync(
        RenderTexture renderTexture,
        Memory<Color32> destination)
    {
        ArgumentNullException.ThrowIfNull(renderTexture);

        var size = renderTexture.Size;
        int pixelCount = size.X * size.Y;
        if (destination.Length < pixelCount)
        {
            throw new ArgumentException($"The destination holds {destination.Length} pixels, fewer than the render texture's {pixelCount}.", nameof(destination));
        }

        return this.Strategy.Interface.ReadPixelsAsync(renderTexture.Strategy, destination.Slice(0, pixelCount));
    }

    /// <summary>Fills the current target with a color.</summary>
    public void Clear(Color color)
    {
        StatsCollector.EnterFramework();

        if ((this.CurrentRenderTexture is null) && !this._isWindowCleared)
        {
            this._isWindowCleared = true;
            this.WindowBackground = color;
        }

        this.Strategy.Interface.Clear(color);
        StatsCollector.ExitFramework();
    }

    /// <summary>
    /// The color the last frame first cleared the window to: what shows around and behind the
    /// game's drawing (its letterbox color). A platform whose window doesn't reach the
    /// whole screen shows it there too, as the web page's background, so the screen around the
    /// game looks the same as on platforms whose window fills it. Null until a frame clears the
    /// window.
    /// </summary>
    internal Color? WindowBackground { get; private set; }

    private bool _isWindowCleared;

    /// <summary>
    /// Called by the host before each frame is drawn, so the frame's first clear of the window
    /// is its <see cref="WindowBackground"/>.
    /// </summary>
    internal void BeginFrame()
    {
        this._isWindowCleared = false;
    }

    internal RenderTexture? CurrentRenderTexture { get; private set; }

    // The sprite batches between Begin and End
    internal int DrawingBatches { get; set; }

    private void ThrowIfDrawing(
        string what)
    {
        if (this.DrawingBatches > 0)
        {
            StatsCollector.ExitFramework();
            throw new InvalidOperationException(
                $"A sprite batch is drawing: use its UseRenderTarget, which changes {what} without ending it.");
        }
    }

    private RectangleInt? _scissorRectangle;
    private int _scissorDepth;
    /// <summary>The only area drawing can change on the current target, in the coordinates of sprites with no transform, or null for all of it.</summary>
    public RectangleInt? ScissorRectangle => this._scissorRectangle;

    /// <summary>
    /// Limits drawing to a rectangle until the returned value is disposed, then goes back to the
    /// limit before it: <c>using (Graphics.UseScissor(rectangle)) { ... }</c>. These nest, and an
    /// inner one is limited by the one around it, so code that clips needn't know what its caller
    /// clipped to. A value can also be kept and disposed later, last made first disposed; ending
    /// one early ends those made after it.
    /// </summary>
    /// <remarks>
    /// It can change during a sprite batch: sprites drawn before the change keep the limit they were
    /// drawn with. Each change splits the draw. This one suits a limit fixed on the screen, or one that
    /// spans batches. To limit drawing in the coordinates of a batch with a transform, see
    /// <see cref="SpriteBatch.UseScissor"/>.
    /// </remarks>
    /// <param name="rectangle">The area, in the coordinates of sprites with no transform. It ignores any transform, and an empty one, of no positive size, draws nothing.</param>
    public ScissorScope UseScissor(
        RectangleInt rectangle)
    {
        var scope = new ScissorScope(this, this._scissorRectangle, this._scissorDepth + 1);

        // NOTE: An intersection with an empty one is empty, and a platform shouldn't see a negative size
        var limit = (this._scissorRectangle is RectangleInt outer) ? RectangleInt.Intersection(outer, rectangle) : rectangle;
        this.ChangeScissor(limit.IsEmpty ? default : limit);
        this._scissorDepth++;
        return scope;
    }

    private void ChangeScissor(
        RectangleInt? rectangle)
    {
        StatsCollector.EnterFramework();

        if (this._scissorRectangle == rectangle)
        {
            StatsCollector.ExitFramework();
            return;
        }

        this._scissorRectangle = rectangle;
        this.Strategy.Interface.SetScissorRectangle(rectangle);

        StatsCollector.ExitFramework();
    }

    internal void RestoreScissor(
        RectangleInt? previous,
        int depth)
    {
        // Already ended, by disposing it again, or by one made before it ending first
        if (depth > this._scissorDepth)
        {
            return;
        }

        if (depth < this._scissorDepth)
        {
            Diagnostics.NonFatalError.Report(
                0x5d2e83a1,
                new InvalidOperationException("A scissor's scope ended before one made after it."));
        }

        this._scissorDepth = depth - 1;
        this.ChangeScissor(previous);
    }
}
