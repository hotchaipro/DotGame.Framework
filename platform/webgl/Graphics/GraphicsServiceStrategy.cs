// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.WebGL.Graphics;

[SupportedOSPlatform("browser")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The graphics service lives for the lifetime of the app.")]
internal sealed class GraphicsServiceStrategy : IGraphicsServiceStrategy
{
    private readonly WebGLGameEngine _engine;
    private readonly string _canvasElement;
    private JSObject? _webGlContext;
    private bool _isDeviceLost;
    private ShaderStrategy? _defaultShader;
    private readonly List<WeakReference<Strategy<ITextureStrategy>>> _trackedRenderTextures = new();
    private readonly List<(Vector2Int Size, byte[] RgbaData, WeakReference<Strategy<ITextureStrategy>> WeakRef)> _trackedDataTextures = new();
    private readonly FrameRecorder _recorder = new();

    // Completes when the context is ready to use, replaced when it's lost, so a load that starts
    // or finishes meanwhile waits for it
    private TaskCompletionSource _contextReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Counts context losses, so a recovery that a new loss overtakes stops
    private int _lossCount;

    // The scissor last asked for, applied again to a restored context, which starts without one
    private RectangleInt? _scissorRectangle;

    internal FrameRecorder Recorder => this._recorder;

    /// <summary>
    /// Runs the frame's recorded drawing, at the end of the frame.
    /// </summary>
    internal void EndFrame()
    {
        this._recorder.Execute(canDraw: !this._isDeviceLost);

        // The frame has run, so its readbacks' pixels are in
        this.CompleteReadbacks();
    }

    /// <summary>
    /// Completes once the context can be used: at once, unless it's lost.
    /// </summary>
    internal Task WhenContextReady() => this._contextReady.Task;

    /// <summary>
    /// A render texture's pixels to read back, read in JS as the frame runs and taken once it
    /// has.
    /// </summary>
    private sealed class Readback
    {
        public required int Id;
        public required Memory<Color32> Destination;
        public required TaskCompletionSource Completion;
    }

    private readonly List<Readback> _readbacks = new List<Readback>();
    private int _nextReadbackId;

    public Task ReadPixelsAsync(
        Strategy<ITextureStrategy> textureStrategy,
        Memory<Color32> destination)
    {
        ArgumentNullException.ThrowIfNull(textureStrategy);

        var nativeHandle = textureStrategy.ToConcrete<TextureStrategy>().NativeHandle;

        // A float in the command buffer holds whole numbers exactly up to 2^24
        int readbackId = this._nextReadbackId;
        this._nextReadbackId = (this._nextReadbackId + 1) & 0xffffff;

        var readback = new Readback()
        {
            Id = readbackId,
            Destination = destination,
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        this._readbacks.Add(readback);

        // Read in order with the frame's drawing, as it runs
        this._recorder.RecordReadback(nativeHandle, readbackId);

        return readback.Completion.Task;
    }

    private void CompleteReadbacks()
    {
        if (this._readbacks.Count == 0)
        {
            return;
        }

        // NOTE: Those the GPU hasn't finished stay for a later frame
        for (int i = this._readbacks.Count - 1; i >= 0; i--)
        {
            var readback = this._readbacks[i];

            // Color32 is RGBA bytes, as WebGL reads them
            int result = WebGLBindings.TakeReadback(readback.Id, MemoryMarshal.AsBytes(readback.Destination.Span));
            if (result == 0)
            {
                continue;
            }

            if (result > 0)
            {
                readback.Completion.TrySetResult();
            }
            else
            {
                readback.Completion.TrySetException(new InvalidOperationException("The frame the pixels were drawn in wasn't drawn."));
            }

            this._readbacks.RemoveAt(i);
        }
    }

    internal GraphicsServiceStrategy(
        WebGLGameEngine engine,
        string canvasElement,
        int multisampleCount = 1)
    {
        ArgumentNullException.ThrowIfNull(canvasElement);

        this._engine = engine;
        this._canvasElement = canvasElement;
        this._multisample = multisampleCount > 1;
    }

    private readonly bool _multisample;

    public int? PreferredFrameRate { get; set; }

    public int MultisampleCount => (this._webGlContext is { } context) ? WebGLBindings.GetMultisampleCount(context) : 1;

    internal bool IsDeviceLost => this._isDeviceLost;

    internal ShaderStrategy DefaultShader => this._defaultShader
        ?? throw new InvalidOperationException("Default shader is not initialized.");

    public Task OnInitialize()
    {
        this._webGlContext = WebGLBindings.GetOrCreateWebGLContext(this._canvasElement, this._multisample);
        this.CompileDefaultShader();
        WebGLBindings.CreateFrameRenderer(this._webGlContext, this.DefaultShader.NativeHandle);
        this._contextReady.TrySetResult();
        return Task.CompletedTask;
    }

    internal void HandleContextLost()
    {
        this._isDeviceLost = true;
        this._lossCount++;

        if (this._contextReady.Task.IsCompleted)
        {
            this._contextReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        WebGLBindings.HandleContextLost();

        // NOTE: The context object stays: the canvas gives the same one back, and calls on a lost
        // context do nothing, so a game's calls meanwhile need no guards
        this._defaultShader?.Dispose();
        this._defaultShader = null;
    }

    internal async Task HandleContextRestored()
    {
        int lossCount = this._lossCount;

        WebGLBindings.HandleContextRestored();

        this._recorder.ForgetResources();

        // The restored context, the same canvas's: game.js has synced its size
        this._webGlContext = WebGLBindings.GetOrCreateWebGLContext(this._canvasElement, this._multisample);

        // Lost again before or during the compile: the lost event follows, and the next restore
        // starts over. A lost context creates no shaders, and some browsers (WebKit) then throw.
        try
        {
            if (WebGLBindings.IsContextLost(this._webGlContext))
            {
                return;
            }

            this.CompileDefaultShader();
        }
        catch (System.Runtime.InteropServices.JavaScript.JSException) when (WebGLBindings.IsContextLost(this._webGlContext))
        {
            return;
        }
        WebGLBindings.CreateFrameRenderer(this._webGlContext, this.DefaultShader.NativeHandle);

        // A load in progress finishes on this context
        this._contextReady.TrySetResult();

        // Reload all GL resources (textures, shaders)
        await this._engine.ContentService.HandleContextRestored();

        // Lost again: the next restore starts over
        if (lossCount != this._lossCount)
        {
            return;
        }

        // Recreate tracked render textures
        this.RecreateTrackedRenderTextures();

        // Recreate tracked procedurally-created textures
        this.RecreateTrackedDataTextures();

        // The new context has no scissor
        this.SetScissorRectangle(this._scissorRectangle);

        // All resources recreated; allow rendering to resume
        this._isDeviceLost = false;

        // The recovery isn't game time
        this._engine.Host.ResetFrameClock();
    }

    public JSObject WebGLContext
    {
        get
        {
            var webGlContext = this._webGlContext;
            if (webGlContext is null)
            {
                throw new InvalidOperationException("WebGL context is not initialized.");
            }

            return webGlContext;
        }
    }

    public Strategy<ISpriteBatchStrategy> CreateSpriteBatch()
    {
        return new Strategy<ISpriteBatchStrategy>(new SpriteBatchStrategy(this._recorder));
    }

    public Strategy<ITextureStrategy> CreateTexture(
        Vector2Int size,
        ReadOnlySpan<Color32> data)
    {
        var pixelCount = size.X * size.Y;

        if (data.Length != pixelCount)
        {
            throw new ArgumentException("Data array length must match width * height.", nameof(data));
        }

        // Color32 is RGBA bytes, as WebGL takes them
        var rgbaData = MemoryMarshal.AsBytes(data).ToArray();

        var jsTexture = WebGLBindings.CreateTextureFromData(this.WebGLContext, size.X, size.Y, rgbaData);

        var strategy = new Strategy<ITextureStrategy>(new TextureStrategy(jsTexture, size) { Owner = this });

        // NOTE: Textures the game has finished with go now, or a game that keeps generating them
        // would keep all their pixels
        this._trackedDataTextures.RemoveAll(static entry => !entry.WeakRef.TryGetTarget(out var target) || target.IsDisposed);

        // Retain the pixel data so the texture can be recreated after a context loss.
        this._trackedDataTextures.Add((size, rgbaData, new WeakReference<Strategy<ITextureStrategy>>(strategy)));

        return strategy;
    }

    // Replaces pixels, and the copy kept for after a lost context
    internal void UpdateTexture(
        TextureStrategy texture,
        RectangleInt region,
        ReadOnlySpan<Color32> data)
    {
        var bytes = MemoryMarshal.AsBytes(data);
        WebGLBindings.SetTextureData(texture.NativeHandle, region.X, region.Y, region.Width, region.Height, MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(bytes), bytes.Length));

        foreach (var (size, rgbaData, weakRef) in this._trackedDataTextures)
        {
            if (weakRef.TryGetTarget(out var target) && (target.IsDisposed == false) && ReferenceEquals(target.Interface, texture))
            {
                int stride = region.Width * 4;
                for (int row = 0; row < region.Height; row++)
                {
                    bytes.Slice(row * stride, stride).CopyTo(rgbaData.AsSpan((((region.Y + row) * size.X) + region.X) * 4, stride));
                }

                break;
            }
        }
    }

    public Strategy<ITextureStrategy> CreateRenderTexture(
        Vector2Int size)
    {
        var jsRenderTexture = WebGLBindings.CreateRenderTexture(this.WebGLContext, size.X, size.Y);

        var strategy = new Strategy<ITextureStrategy>(new TextureStrategy(jsRenderTexture, size));

        this._trackedRenderTextures.Add(new WeakReference<Strategy<ITextureStrategy>>(strategy));

        return strategy;
    }

    public void SetRenderTarget(
        Strategy<ITextureStrategy>? renderTextureStrategy)
    {
        JSObject? nativeHandle = null;
        if (renderTextureStrategy is not null)
        {
            nativeHandle = renderTextureStrategy.ToConcrete<TextureStrategy>().NativeHandle;
        }

        this._recorder.RecordSetRenderTarget(nativeHandle);
    }

    public void Clear(Color color)
    {
        this._recorder.RecordClear(color.R, color.G, color.B, color.A);
    }

    public void SetScissorRectangle(
        RectangleInt? rectangle)
    {
        this._scissorRectangle = rectangle;

        if (rectangle is { } scissor)
        {
            this._recorder.RecordSetScissor(scissor.X, scissor.Y, scissor.Width, scissor.Height);
        }
        else
        {
            this._recorder.RecordDisableScissor();
        }
    }

    private void CompileDefaultShader()
    {
        var jsShaderProgram = WebGLBindings.CompileShaderProgram(
            this.WebGLContext,
            DefaultShaderSource.VertexShader,
            DefaultShaderSource.FragmentShader);

        this._defaultShader = new ShaderStrategy(jsShaderProgram, this);
    }

    private void RecreateTrackedDataTextures()
    {
        for (int i = this._trackedDataTextures.Count - 1; i >= 0; i--)
        {
            var (size, rgbaData, weakRef) = this._trackedDataTextures[i];

            if (weakRef.TryGetTarget(out var strategy) && !strategy.IsDisposed)
            {
                // Invalidate old GL texture (already freed by context loss)
                strategy.ToConcrete<TextureStrategy>().Invalidate();

                var jsTexture = WebGLBindings.CreateTextureFromData(this.WebGLContext, size.X, size.Y, rgbaData);
                strategy.Reset(new TextureStrategy(jsTexture, size));
            }
            else
            {
                this._trackedDataTextures.RemoveAt(i);
            }
        }
    }

    private void RecreateTrackedRenderTextures()
    {
        for (int i = this._trackedRenderTextures.Count - 1; i >= 0; i--)
        {
            if (this._trackedRenderTextures[i].TryGetTarget(out var strategy) && !strategy.IsDisposed)
            {
                // Invalidate old GL render texture (already freed by context loss)
                strategy.ToConcrete<TextureStrategy>().Invalidate();

                var size = strategy.Interface.Size;
                var jsRenderTexture = WebGLBindings.CreateRenderTexture(this.WebGLContext, size.X, size.Y);
                strategy.Reset(new TextureStrategy(jsRenderTexture, size));
            }
            else
            {
                this._trackedRenderTextures.RemoveAt(i);
            }
        }
    }
}
