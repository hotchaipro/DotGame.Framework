// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAnimation;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Common.Graphics;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Threading;
using Metal;
using MetalKit;

namespace DotGame.Framework.iOS.Graphics;

internal sealed class GraphicsServiceStrategy : IGraphicsServiceStrategy
{
    private readonly IMTLDevice _device;
    private readonly IMTLCommandQueue _commandQueue;
    private IMTLSamplerState? _defaultSampler;
    private ShaderStrategy? _defaultShader;

    private IMTLCommandBuffer? _currentCommandBuffer;
    private IMTLRenderCommandEncoder? _currentRenderEncoder;

    // A clear of the bound target that hasn't started its pass. A pass starts at its first draw,
    // since one with nothing in it still costs a tile GPU a load and a store of the whole target.
    private MTLClearColor? _pendingClear;
    private RectangleInt? _scissorRectangle;
    private IMTLFunction? _defaultVertexFunction;
    private IMTLBuffer? _spriteIndexBuffer;
    private MTKView? _frameView;
    private ICAMetalDrawable? _currentDrawable;
    private IMTLTexture? _currentDrawableTexture;

    // The render texture bound by SetRenderTarget, or null when rendering to
    // the drawable. While one is bound it owns the viewport, scissor, clear
    // and projection, because a render texture is addressed in its own texel
    // space rather than in the drawable's.
    private IMTLTexture? _currentRenderTarget;
    private Vector2Int? _currentRenderTargetSize;
    private Matrix4x4 _renderTargetProjectionMatrix;

    private int _viewportWidth;
    private int _viewportHeight;

    // Projection matrix (orthographic, top-left origin)
    private Matrix4x4 _projectionMatrix;

    // Frames begun, and frames the GPU has finished, so sprite batches know when vertex data
    // they wrote is no longer read. A queue's command buffers complete in order, so a count
    // is enough. The completed count is written on Metal's completion thread.
    private long _frameNumber;
    private long _completedFrameCount;
    private readonly Action<IMTLCommandBuffer> _frameCompletedHandler;

    // The state bound on the current render encoder, so consecutive sprite batch flushes set
    // only what changed. Reset with every render pass, since a new encoder binds nothing.
    private IMTLRenderPipelineState? _boundPipeline;
    private IMTLBuffer? _boundVertexBuffer;

    // The sprite batch whose sprites may still be waiting to be drawn. A batch's End doesn't
    // draw, so a later batch with the same state continues it; anything that changes the
    // encoder's state, or another batch beginning, draws them first.
    private SpriteBatchStrategy? _activeBatch;
    private bool _isProjectionBound;
    private IMTLTexture? _boundTexture;
    private byte[] _boundFragmentUniforms = new byte[256];
    private int _boundFragmentUniformLength;

    // What the screen is drawn into when multisampling, resolved to the drawable
    private readonly int _multisampleCount = 1;
    private IMTLTexture? _multisampleTexture;

    public int? PreferredFrameRate { get; set; }

    public int MultisampleCount => this._multisampleCount;

    // Sprites drawn now go to the screen, which is multisampled, and not to a render texture: a
    // pipeline's sample count must match its target's
    internal bool DrawsMultisampled => (this._multisampleCount > 1) && (this._currentRenderTarget is null);

    internal GraphicsServiceStrategy(
        IMTLDevice device,
        int multisampleCount = 1)
    {
        this._device = device ?? throw new ArgumentNullException(nameof(device));

        for (int samples = multisampleCount; samples >= 2; samples /= 2)
        {
            if (device.SupportsTextureSampleCount((nuint)samples))
            {
                this._multisampleCount = samples;
                break;
            }
        }

        this._commandQueue = device.CreateCommandQueue()
            ?? throw new InvalidOperationException("Failed to create Metal command queue.");

        this._frameCompletedHandler = commandBuffer =>
        {
            Interlocked.Increment(ref this._completedFrameCount);

            // NOTE: Otherwise a GPU error or timeout is only a missing frame, with no report
            if (commandBuffer.Status == MTLCommandBufferStatus.Error)
            {
                GameHost.ReportNonFatal(0x47ad2e19, new InvalidOperationException(
                    $"A frame's command buffer failed: {commandBuffer.Error?.LocalizedDescription}"));
            }
        };
    }

    /// <summary>The number of the frame being drawn, counting from 1.</summary>
    internal long FrameNumber => this._frameNumber;

    /// <summary>The number of frames the GPU has finished; frame n is done when this is n or more.</summary>
    internal long CompletedFrameCount => Interlocked.Read(ref this._completedFrameCount);

    internal IMTLDevice Device => this._device;

    /// <summary>
    /// The quads' index buffer, the same for every batch. Never changes, so the service keeps it.
    /// </summary>
    internal IMTLBuffer SpriteIndexBuffer => this._spriteIndexBuffer ??= this.CreateSpriteIndexBuffer();

    private unsafe IMTLBuffer CreateSpriteIndexBuffer()
    {
        var indices = new ushort[SpriteBatchStrategy.MaxSpritesPerBatch * SpriteBatchStrategy.IndicesPerSprite];
        for (int i = 0; i < SpriteBatchStrategy.MaxSpritesPerBatch; i++)
        {
            int vi = i * 4;
            int ii = i * 6;
            indices[ii + 0] = (ushort)(vi + 0);
            indices[ii + 1] = (ushort)(vi + 1);
            indices[ii + 2] = (ushort)(vi + 2);
            indices[ii + 3] = (ushort)(vi + 0);
            indices[ii + 4] = (ushort)(vi + 2);
            indices[ii + 5] = (ushort)(vi + 3);
        }

        fixed (ushort* ptr = indices)
        {
            return this._device.CreateBuffer(
                (nint)ptr,
                (nuint)(indices.Length * sizeof(ushort)),
                MTLResourceOptions.CpuCacheModeDefault)
                ?? throw new InvalidOperationException("Failed to create Metal index buffer.");
        }
    }

    internal ShaderStrategy DefaultShader => this._defaultShader
        ?? throw new InvalidOperationException("Default shader is not initialized.");

    internal IMTLSamplerState DefaultSampler => this._defaultSampler
        ?? throw new InvalidOperationException("Default sampler is not initialized.");

    internal IMTLRenderCommandEncoder? CurrentRenderEncoder => this._currentRenderEncoder;

    internal Matrix4x4 ProjectionMatrix => (this._currentRenderTargetSize is null)
        ? this._projectionMatrix
        : this._renderTargetProjectionMatrix;

    /// <summary>
    /// Width, in pixels, of the currently bound render target - the active
    /// render texture if one is bound, otherwise the drawable.
    /// </summary>
    private int TargetWidth => this._currentRenderTargetSize?.X ?? this._viewportWidth;

    /// <summary>
    /// Height, in pixels, of the currently bound render target - the active
    /// render texture if one is bound, otherwise the drawable.
    /// </summary>
    private int TargetHeight => this._currentRenderTargetSize?.Y ?? this._viewportHeight;

    public Task OnInitialize()
    {
        this.CreateDefaultSampler();
        this.CompileDefaultShader();
        return Task.CompletedTask;
    }

    public Strategy<ISpriteBatchStrategy> CreateSpriteBatch()
    {
        return new Strategy<ISpriteBatchStrategy>(new SpriteBatchStrategy(this));
    }

    public unsafe Strategy<ITextureStrategy> CreateTexture(
        Vector2Int size,
        ReadOnlySpan<Color32> data)
    {
        var pixelCount = size.X * size.Y;

        if (data.Length != pixelCount)
        {
            throw new ArgumentException("Data array length must match width * height.", nameof(data));
        }

        var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(
            MTLPixelFormat.RGBA8Unorm, (nuint)size.X, (nuint)size.Y, false);
        desc.Usage = MTLTextureUsage.ShaderRead;

        var texture = this._device.CreateTexture(desc)
            ?? throw new InvalidOperationException("Failed to create Metal texture.");

        var rgbaData = GC.AllocateUninitializedArray<byte>(pixelCount * 4);
        for (int i = 0; i < pixelCount; i++)
        {
            var offset = i * 4;
            rgbaData[offset] = data[i].R;
            rgbaData[offset + 1] = data[i].G;
            rgbaData[offset + 2] = data[i].B;
            rgbaData[offset + 3] = data[i].A;
        }

        fixed (byte* ptr = rgbaData)
        {
            texture.ReplaceRegion(
                new MTLRegion(new MTLOrigin(0, 0, 0), new MTLSize((nint)size.X, (nint)size.Y, 1)),
                0,
                (nint)ptr,
                (nuint)(size.X * 4));
        }

        desc.Dispose();

        return new Strategy<ITextureStrategy>(new TextureStrategy(texture, size, this));
    }

    public Strategy<ITextureStrategy> CreateRenderTexture(
        Vector2Int size)
    {
        var desc = MTLTextureDescriptor.CreateTexture2DDescriptor(
            MTLPixelFormat.BGRA8Unorm, (nuint)size.X, (nuint)size.Y, false);
        desc.Usage = MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderRead;

        var texture = this._device.CreateTexture(desc)
            ?? throw new InvalidOperationException("Failed to create Metal render texture.");

        desc.Dispose();

        return new Strategy<ITextureStrategy>(new TextureStrategy(texture, size, this));
    }

    public void SetRenderTarget(
        Strategy<ITextureStrategy>? renderTextureStrategy)
    {
        this.FlushPendingSprites();
        this.FlushPendingClear();

        // End current render encoder if active
        this.EndCurrentRenderEncoder();

        if (renderTextureStrategy is not null)
        {
            var metalTexture = renderTextureStrategy.ToConcrete<TextureStrategy>();

            // Track the render target before beginning the pass so that the
            // viewport, scissor and projection are sized to it rather than to
            // the drawable.
            this._currentRenderTarget = metalTexture.NativeTexture;
            this._currentRenderTargetSize = metalTexture.Size;
            this._renderTargetProjectionMatrix = CreateOrthographicProjection(
                metalTexture.Size.X,
                metalTexture.Size.Y);
        }
        else
        {
            this._currentRenderTarget = null;
            this._currentRenderTargetSize = null;
        }
    }

    public unsafe Task ReadPixelsAsync(
        Strategy<ITextureStrategy> textureStrategy,
        Memory<Color32> destination)
    {
        ArgumentNullException.ThrowIfNull(textureStrategy);

        var metalTexture = textureStrategy.ToConcrete<TextureStrategy>();
        var size = metalTexture.Size;
        nuint bytesPerRow = (nuint)(size.X * 4);
        nuint byteCount = bytesPerRow * (nuint)size.Y;

        var buffer = this._device.CreateBuffer(byteCount, MTLResourceOptions.StorageModeShared)
            ?? throw new InvalidOperationException("Failed to create a Metal readback buffer.");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The copy follows what's drawn so far: in the frame's command buffer, between render
        // passes, or in one of its own outside a frame
        this.FlushPendingSprites();
        this.FlushPendingClear();
        this.EndCurrentRenderEncoder();

        bool isOwnCommandBuffer = (this._currentCommandBuffer is null);
        var commandBuffer = this._currentCommandBuffer ?? this._commandQueue.CommandBuffer()
            ?? throw new InvalidOperationException("Failed to create a Metal command buffer.");

        var blit = commandBuffer.BlitCommandEncoder
            ?? throw new InvalidOperationException("Failed to create a Metal blit encoder.");
        blit.CopyFromTexture(
            metalTexture.NativeTexture,
            0,
            0,
            new MTLOrigin(0, 0, 0),
            new MTLSize(size.X, size.Y, 1),
            buffer,
            0,
            bytesPerRow,
            byteCount);
        blit.EndEncoding();
        blit.Dispose();

        // The pixels are in once the command buffer has run, on Metal's completion thread:
        // they're handed over on the game thread
        commandBuffer.AddCompletedHandler((_) =>
        {
            GameThread.Post(() =>
            {
                try
                {
                    // The render texture is BGRA: Color32 is RGBA. Metal's rows are top first.
                    var source = new ReadOnlySpan<byte>((void*)buffer.Contents, (int)byteCount);
                    var pixels = destination.Span;
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        int offset = i * 4;
                        pixels[i] = Color32.FromPremultiplied(source[offset + 2], source[offset + 1], source[offset], source[offset + 3]);
                    }

                    completion.TrySetResult();
                }
                finally
                {
                    buffer.Dispose();
                }
            });
        });

        if (isOwnCommandBuffer)
        {
            commandBuffer.Commit();
        }

        return completion.Task;
    }

    public void Clear(Color color)
    {
        this.FlushPendingSprites();

        // End any active render pass so the next one can start with this clear
        this.EndCurrentRenderEncoder();

        if (this._currentCommandBuffer is not null)
        {
            this._pendingClear = new MTLClearColor(color.R, color.G, color.B, color.A);
        }
    }

    // A clear with no draws after it still has to happen, before anything that follows it
    private void FlushPendingClear()
    {
        if (this._pendingClear is { } clearColor)
        {
            this._pendingClear = null;

            // The bound render target, if any, rather than always the drawable
            var target = this._currentRenderTarget ?? this.DrawableTexture;
            if (target is not null)
            {
                this.BeginRenderPass(target, clearColor);
            }
        }
    }

    /// <summary>
    /// Runs what's waiting to draw, so the frame's drawable is known before it's presented.
    /// </summary>
    internal void FinishPendingDrawing()
    {
        this.FlushPendingSprites();
        this.FlushPendingClear();
    }

    public void SetScissorRectangle(
        RectangleInt? rectangle)
    {
        this.FlushPendingSprites();

        this._scissorRectangle = rectangle;
        this.ApplyScissorRectangle();
    }

    // NOTE: A pass starts with a full scissor, and the service's state is what other platforms
    // keep, so each new pass gets it again
    private void ApplyScissorRectangle()
    {
        if (this._currentRenderEncoder is null)
        {
            return;
        }

        int targetWidth = this.TargetWidth;
        int targetHeight = this.TargetHeight;
        int left = 0;
        int top = 0;
        int right = targetWidth;
        int bottom = targetHeight;

        if (this._scissorRectangle is { } r)
        {
            left = Math.Clamp(r.X, 0, targetWidth);
            top = Math.Clamp(r.Y, 0, targetHeight);
            right = Math.Clamp(r.X + r.Width, left, targetWidth);
            bottom = Math.Clamp(r.Y + r.Height, top, targetHeight);
        }

        this._currentRenderEncoder.SetScissorRect(new MTLScissorRect(
            (nuint)left, (nuint)top, (nuint)(right - left), (nuint)(bottom - top)));
    }

    internal void SetViewportSize(
        int width,
        int height)
    {
        this._viewportWidth = width;
        this._viewportHeight = height;

        this._projectionMatrix = CreateOrthographicProjection(width, height);
    }

    /// <summary>
    /// Builds an orthographic projection with (0,0) at the top-left and
    /// (width, height) at the bottom-right.
    /// </summary>
    private static Matrix4x4 CreateOrthographicProjection(
        int width,
        int height)
    {
        // Data layout: Slang mul(v,M) compiles to MSL v*M. Both MSL column-major
        // and SPIR-V RowMajor produce result[i] = Σ_j mem[i*4+j] * v[j],
        // so translation goes in column 3 (memory positions 3, 7, 11, 15).
        return new Matrix4x4(
            2f / width, 0f, 0f, -1f,
            0f, -2f / height, 0f, 1f,
            0f, 0f, -1f, 0f,
            0f, 0f, 0f, 1f);
    }

    // NOTE: Taken when first drawn to, as late as can be: taking a drawable blocks when all are in
    // use, and holding one through input and update makes that likelier. Frames that draw nothing
    // to the screen never touch one.
    private IMTLTexture? DrawableTexture
    {
        get
        {
            if ((this._currentDrawableTexture is null) && (this._frameView is not null))
            {
                this._currentDrawable = this._frameView.CurrentDrawable;
                this._currentDrawableTexture = this._currentDrawable?.Texture;
            }

            return this._currentDrawableTexture;
        }
    }

    /// <summary>
    /// The frame's drawable, if it has drawn to one; the frame owns it until it ends.
    /// </summary>
    internal ICAMetalDrawable? CurrentDrawable => this._currentDrawable;

    internal void BeginFrame(
        MTKView view)
    {
        this._frameView = view;
        this._pendingClear = null;
        this._currentCommandBuffer = this._commandQueue.CommandBuffer();
        this._frameNumber++;
        this._currentCommandBuffer?.AddCompletedHandler(this._frameCompletedHandler);
    }

    internal void EndFrame()
    {
        this.FinishPendingDrawing();
        this.EndCurrentRenderEncoder();

        this._currentCommandBuffer = null;
        this._frameView = null;
        this._currentDrawableTexture?.Dispose();
        this._currentDrawableTexture = null;
        this._currentDrawable = null;

        // Defensively drop any render target left bound by an unbalanced frame
        // so it cannot leak into the next one.
        this._currentRenderTarget = null;
        this._currentRenderTargetSize = null;
    }

    internal IMTLCommandBuffer? CurrentCommandBuffer => this._currentCommandBuffer;

    private void BeginRenderPass(
        IMTLTexture target,
        MTLClearColor? clearColor)
    {
        if (this._currentCommandBuffer is null)
        {
            return;
        }

        using var passDesc = new MTLRenderPassDescriptor();
        using var colorAttachments = passDesc.ColorAttachments;
        using var colorAttachment = colorAttachments[0];
        if (this.DrawsMultisampled)
        {
            // NOTE: Stored as well as resolved, as a later pass in the frame goes on from it: one
            // that follows a render texture's, say
            colorAttachment.Texture = this.GetMultisampleTexture(target);
            colorAttachment.ResolveTexture = target;
            colorAttachment.StoreAction = MTLStoreAction.StoreAndMultisampleResolve;
        }
        else
        {
            colorAttachment.Texture = target;
            colorAttachment.StoreAction = MTLStoreAction.Store;
        }

        if (clearColor.HasValue)
        {
            colorAttachment.LoadAction = MTLLoadAction.Clear;
            colorAttachment.ClearColor = clearColor.Value;
        }
        else
        {
            colorAttachment.LoadAction = MTLLoadAction.Load;
        }

        this._currentRenderEncoder = this._currentCommandBuffer.CreateRenderCommandEncoder(passDesc);
        this.ResetBoundState();

        if (this._currentRenderEncoder is not null)
        {
            int targetWidth = this.TargetWidth;
            int targetHeight = this.TargetHeight;

            this._currentRenderEncoder.SetViewport(new MTLViewport(
                0, 0, targetWidth, targetHeight, 0, 1));
            this.ApplyScissorRectangle();
        }
    }

    private IMTLTexture GetMultisampleTexture(
        IMTLTexture drawable)
    {
        if ((this._multisampleTexture is null)
            || (this._multisampleTexture.Width != drawable.Width)
            || (this._multisampleTexture.Height != drawable.Height))
        {
            this._multisampleTexture?.Dispose();

            using var desc = new MTLTextureDescriptor
            {
                TextureType = MTLTextureType.k2DMultisample,
                PixelFormat = MTLPixelFormat.BGRA8Unorm,
                Width = drawable.Width,
                Height = drawable.Height,
                SampleCount = (nuint)this._multisampleCount,
                Usage = MTLTextureUsage.RenderTarget,
                StorageMode = MTLStorageMode.Private,
            };

            this._multisampleTexture = this._device.CreateTexture(desc)
                ?? throw new InvalidOperationException("Failed to create the multisampled target.");
        }

        return this._multisampleTexture;
    }

    internal void EndCurrentRenderEncoder()
    {
        if (this._currentRenderEncoder is not null)
        {
            this._currentRenderEncoder.EndEncoding();
            this._currentRenderEncoder.Dispose();
            this._currentRenderEncoder = null;
        }

        this.ResetBoundState();
    }

    /// <summary>
    /// Makes a batch the one whose sprites are pending, drawing the previous one's first.
    /// </summary>
    internal void SetActiveBatch(
        SpriteBatchStrategy? batch)
    {
        if (!ReferenceEquals(batch, this._activeBatch))
        {
            this._activeBatch?.FlushPending();
            this._activeBatch = batch;
        }
    }

    /// <summary>
    /// A batch that's being disposed: its waiting sprites are drawn first, as End promised.
    /// </summary>
    internal void ReleaseActiveBatch(
        SpriteBatchStrategy batch)
    {
        if (ReferenceEquals(batch, this._activeBatch))
        {
            batch.FlushPending();
            this._activeBatch = null;
        }
    }

    /// <summary>
    /// A texture that's being disposed: sprites still waiting to draw with it are drawn first.
    /// </summary>
    internal void FlushSpritesUsing(
        IMTLTexture texture)
    {
        this._activeBatch?.FlushPendingIfUsing(texture);

        if (ReferenceEquals(texture, this._currentRenderTarget))
        {
            this.FlushPendingClear();
        }
    }

    /// <summary>
    /// The encoder to draw with, starting a pass with the waiting clear, or that keeps the target's
    /// contents if there's none: sprites drawn before any clear aren't lost. <c>null</c> outside a frame.
    /// </summary>
    internal IMTLRenderCommandEncoder? GetOrBeginRenderEncoder()
    {
        if ((this._currentRenderEncoder is null) && (this._currentCommandBuffer is not null))
        {
            var target = this._currentRenderTarget ?? this.DrawableTexture;
            if (target is not null)
            {
                var clearColor = this._pendingClear;
                this._pendingClear = null;
                this.BeginRenderPass(target, clearColor);
            }
        }

        return this._currentRenderEncoder;
    }

    /// <summary>
    /// Draws the sprites still waiting in the active batch.
    /// </summary>
    internal void FlushPendingSprites()
    {
        this._activeBatch?.FlushPending();
    }

    private void ResetBoundState()
    {
        this._boundPipeline = null;
        this._boundSampler = null;
        this._boundVertexBuffer = null;
        this._isProjectionBound = false;
        this._boundTexture = null;
        this._boundFragmentUniformLength = -1;
    }

    internal void BindPipeline(
        IMTLRenderCommandEncoder encoder,
        IMTLRenderPipelineState pipeline)
    {
        if (!ReferenceEquals(pipeline, this._boundPipeline))
        {
            encoder.SetRenderPipelineState(pipeline);
            this._boundPipeline = pipeline;
        }
    }

    internal void BindVertexBuffer(
        IMTLRenderCommandEncoder encoder,
        IMTLBuffer buffer,
        nuint offset)
    {
        if (ReferenceEquals(buffer, this._boundVertexBuffer))
        {
            encoder.SetVertexBufferOffset(offset, 1);
        }
        else
        {
            encoder.SetVertexBuffer(buffer, offset, 1);
            this._boundVertexBuffer = buffer;
        }
    }

    // The projection depends only on the render target, which an encoder never changes
    internal unsafe void BindProjection(
        IMTLRenderCommandEncoder encoder)
    {
        if (!this._isProjectionBound)
        {
            var projection = this.ProjectionMatrix;
            encoder.SetVertexBytes((nint)(&projection), (nuint)sizeof(Matrix4x4), 0);
            this._isProjectionBound = true;
        }
    }

    // The samplers other than the default, made when first used
    private readonly IMTLSamplerState?[] _samplers = new IMTLSamplerState?[4];
    private IMTLSamplerState? _boundSampler;

    internal void BindSampler(
        IMTLRenderCommandEncoder encoder,
        Sampler sampler)
    {
        var state = (sampler == Sampler.LinearClamp) ? this.DefaultSampler : (this._samplers[(int)sampler] ??= this.CreateSampler(sampler));
        if (!ReferenceEquals(state, this._boundSampler))
        {
            encoder.SetFragmentSamplerState(state, 0);
            this._boundSampler = state;
        }
    }

    private IMTLSamplerState CreateSampler(
        Sampler sampler)
    {
        using var descriptor = new MTLSamplerDescriptor();
        var filter = ((sampler == Sampler.PointClamp) || (sampler == Sampler.PointRepeat)) ? MTLSamplerMinMagFilter.Nearest : MTLSamplerMinMagFilter.Linear;
        var address = ((sampler == Sampler.LinearRepeat) || (sampler == Sampler.PointRepeat)) ? MTLSamplerAddressMode.Repeat : MTLSamplerAddressMode.ClampToEdge;
        descriptor.MinFilter = filter;
        descriptor.MagFilter = filter;
        descriptor.SAddressMode = address;
        descriptor.TAddressMode = address;

        return this._device.CreateSamplerState(descriptor)
            ?? throw new InvalidOperationException("Failed to create Metal sampler state.");
    }

    internal IMTLFunction DefaultVertexFunction => this._defaultVertexFunction
        ?? throw new InvalidOperationException("The default vertex function isn't compiled yet.");

    internal void BindTexture(
        IMTLRenderCommandEncoder encoder,
        IMTLTexture texture)
    {
        if (!ReferenceEquals(texture, this._boundTexture))
        {
            encoder.SetFragmentTexture(texture, 0);
            this._boundTexture = texture;
        }
    }

    // Uniform values are encoder state, not pipeline state, so the same bytes stay bound
    // across a pipeline change
    internal unsafe void BindFragmentUniforms(
        IMTLRenderCommandEncoder encoder,
        ReadOnlySpan<byte> uniforms)
    {
        if ((uniforms.Length == this._boundFragmentUniformLength)
            && uniforms.SequenceEqual(this._boundFragmentUniforms.AsSpan(0, uniforms.Length)))
        {
            return;
        }

        fixed (byte* pointer = uniforms)
        {
            encoder.SetFragmentBytes((nint)pointer, (nuint)uniforms.Length, 0);
        }

        if (uniforms.Length > this._boundFragmentUniforms.Length)
        {
            this._boundFragmentUniforms = new byte[uniforms.Length];
        }

        uniforms.CopyTo(this._boundFragmentUniforms);
        this._boundFragmentUniformLength = uniforms.Length;
    }

    private void CreateDefaultSampler()
    {
        using var samplerDesc = new MTLSamplerDescriptor();
        samplerDesc.MinFilter = MTLSamplerMinMagFilter.Linear;
        samplerDesc.MagFilter = MTLSamplerMinMagFilter.Linear;
        samplerDesc.SAddressMode = MTLSamplerAddressMode.ClampToEdge;
        samplerDesc.TAddressMode = MTLSamplerAddressMode.ClampToEdge;

        this._defaultSampler = this._device.CreateSamplerState(samplerDesc)
            ?? throw new InvalidOperationException("Failed to create Metal sampler state.");
    }

    private void CompileDefaultShader()
    {
        this._defaultShader = this.CompileShaderFromMSL(
            DefaultShaderSource.VertexShaderMSL,
            DefaultShaderSource.FragmentShaderMSL,
            "main0",
            "main0");
    }

    internal ShaderStrategy CompileShaderFromMSL(
        string vertexSource,
        string fragmentSource,
        string vertexEntryPoint = "main0",
        string fragmentEntryPoint = "main0")
    {
        // NOTE: Most shaders share the default vertex stage, and compiling it again for each is
        // tens of milliseconds on a cold cache. The service holds this one for good, so a shader
        // doesn't own it.
        bool isDefaultVertex = ReferenceEquals(vertexSource, DefaultShaderSource.VertexShaderMSL);
        var vertexFunction = isDefaultVertex
            ? (this._defaultVertexFunction ??= CompileFunction(this._device, vertexSource, vertexEntryPoint, "vertex"))
            : CompileFunction(this._device, vertexSource, vertexEntryPoint, "vertex");
        var fragmentFunction = CompileFunction(this._device, fragmentSource, fragmentEntryPoint, "fragment");

        var pipelineState = this.CreateRenderPipelineState(vertexFunction, fragmentFunction, out var uniformOffsets, out int uniformSize);

        return new ShaderStrategy(
            isDefaultVertex ? null : vertexFunction,
            fragmentFunction,
            pipelineState,
            uniformOffsets,
            uniformSize,
            this);
    }

    private static IMTLFunction CompileFunction(
        IMTLDevice device,
        string source,
        string entryPoint,
        string stage)
    {
        using var options = new MTLCompileOptions();
        using var library = device.CreateLibrary(source, options, out var error);
        if (library is null)
        {
            throw new InvalidOperationException(
                $"Failed to compile {stage} shader: {error?.LocalizedDescription ?? "Unknown error"}");
        }

        return library.CreateFunction(entryPoint)
            ?? throw new InvalidOperationException($"The {stage} function '{entryPoint}' wasn't found.");
    }

    internal IMTLRenderPipelineState CreateRenderPipelineState(
        IMTLFunction vertexFunction,
        IMTLFunction fragmentFunction,
        out Dictionary<string, int> uniformOffsets,
        out int uniformSize,
        BlendMode blend = BlendMode.Alpha,
        bool multisampled = false)
    {
        // Colors are premultiplied
        var (sourceColor, destinationColor, sourceAlpha, destinationAlpha) = blend switch
        {
            BlendMode.Additive => (MTLBlendFactor.One, MTLBlendFactor.One, MTLBlendFactor.One, MTLBlendFactor.One),
            BlendMode.Multiply => (MTLBlendFactor.DestinationColor, MTLBlendFactor.OneMinusSourceAlpha, MTLBlendFactor.One, MTLBlendFactor.OneMinusSourceAlpha),
            BlendMode.Screen => (MTLBlendFactor.One, MTLBlendFactor.OneMinusSourceColor, MTLBlendFactor.One, MTLBlendFactor.OneMinusSourceAlpha),
            BlendMode.Opaque => (MTLBlendFactor.One, MTLBlendFactor.Zero, MTLBlendFactor.One, MTLBlendFactor.Zero),
            _ => (MTLBlendFactor.One, MTLBlendFactor.OneMinusSourceAlpha, MTLBlendFactor.One, MTLBlendFactor.OneMinusSourceAlpha),
        };

        using var pipelineDesc = new MTLRenderPipelineDescriptor();
        pipelineDesc.VertexFunction = vertexFunction;
        pipelineDesc.FragmentFunction = fragmentFunction;
        pipelineDesc.ColorAttachments[0].PixelFormat = MTLPixelFormat.BGRA8Unorm;
        pipelineDesc.RasterSampleCount = multisampled ? (nuint)this._multisampleCount : 1;

        // Premultiplied alpha blending
        pipelineDesc.ColorAttachments[0].BlendingEnabled = true;
        pipelineDesc.ColorAttachments[0].SourceRgbBlendFactor = sourceColor;
        pipelineDesc.ColorAttachments[0].DestinationRgbBlendFactor = destinationColor;
        pipelineDesc.ColorAttachments[0].RgbBlendOperation = MTLBlendOperation.Add;
        pipelineDesc.ColorAttachments[0].SourceAlphaBlendFactor = sourceAlpha;
        pipelineDesc.ColorAttachments[0].DestinationAlphaBlendFactor = destinationAlpha;
        pipelineDesc.ColorAttachments[0].AlphaBlendOperation = MTLBlendOperation.Add;

        // Vertex descriptor: SpriteVertex, 28 bytes
        using var vertexDesc = new MTLVertexDescriptor();

        // Position: float2
        vertexDesc.Attributes[0].Format = MTLVertexFormat.Float2;
        vertexDesc.Attributes[0].Offset = 0;
        vertexDesc.Attributes[0].BufferIndex = 1;

        // Color: four normalized bytes, read as float4
        vertexDesc.Attributes[1].Format = MTLVertexFormat.UChar4Normalized;
        vertexDesc.Attributes[1].Offset = SpriteVertex.ColorOffset;
        vertexDesc.Attributes[1].BufferIndex = 1;

        // TexCoord: float2
        vertexDesc.Attributes[2].Format = MTLVertexFormat.Float2;
        vertexDesc.Attributes[2].Offset = SpriteVertex.TextureCoordinateOffset;
        vertexDesc.Attributes[2].BufferIndex = 1;

        // Custom data: four half floats, read as float4
        vertexDesc.Attributes[3].Format = MTLVertexFormat.Half4;
        vertexDesc.Attributes[3].Offset = SpriteVertex.CustomDataOffset;
        vertexDesc.Attributes[3].BufferIndex = 1;

        vertexDesc.Layouts[1].Stride = SpriteVertex.Size;
        vertexDesc.Layouts[1].StepFunction = MTLVertexStepFunction.PerVertex;

        pipelineDesc.VertexDescriptor = vertexDesc;

        // NOTE: The argument reflection is deprecated from iOS 16, but the framework supports iOS 15, where
        // its replacement doesn't exist
#pragma warning disable CA1422
        var pipelineState = this._device.CreateRenderPipelineState(pipelineDesc, MTLPipelineOption.ArgumentInfo | MTLPipelineOption.BufferTypeInfo, out var reflection, out var error);
        if (pipelineState is null)
        {
            throw new InvalidOperationException(
                $"Failed to create render pipeline state: {error?.LocalizedDescription ?? "Unknown error"}");
        }

        // The fragment stage's uniforms, as Metal lays them out: buffer 0 (BindFragmentUniforms)
        uniformOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        uniformSize = 0;
        foreach (var argument in reflection?.FragmentArguments ?? [])
        {
            if ((argument.Type == MTLArgumentType.Buffer) && (argument.Index == 0) && (argument.BufferStructType is { } layout))
            {
                uniformSize = (int)argument.BufferDataSize;
                foreach (var member in layout.Members)
                {
                    uniformOffsets[member.Name] = (int)member.Offset;
                }
            }
        }
#pragma warning restore CA1422

        return pipelineState;
    }
}
