// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Common.Graphics;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Desktop.Graphics;

internal sealed class GraphicsServiceStrategy : IGraphicsServiceStrategy
{
    // How long the swapchain can fail to be acquired before the device counts as lost
    private const long AcquireFailureLimitMilliseconds = 5000;

    // When acquiring started to fail, or 0 while it's working
    private long _acquireFailingSince;

    private readonly nint _gpuDevice;
    private readonly nint _window;
    private readonly SDL.GPUShaderFormat _shaderFormat;
    private nint _swapchainTexture;
    private uint _swapchainWidth;
    private uint _swapchainHeight;
    private float _displayScale = 1f;
    private nint _currentCommandBuffer;
    private nint _defaultPipeline;
    private nint _defaultVertexShader;
    private nint _defaultFragmentShader;
    private nint _defaultSampler;

    // What the screen is drawn into when multisampling, resolved to the swapchain, and its size
    private readonly SDL.GPUSampleCount _sampleCount;
    private nint _multisampleTexture;
    private uint _multisampleWidth;
    private uint _multisampleHeight;

    internal GraphicsServiceStrategy(
        nint gpuDevice,
        nint window,
        int multisampleCount = 1)
    {
        if (gpuDevice == nint.Zero)
        {
            throw new ArgumentException("GPU device is not initialized.", nameof(gpuDevice));
        }

        if (window == nint.Zero)
        {
            throw new ArgumentException("Window is not initialized.", nameof(window));
        }

        this._gpuDevice = gpuDevice;
        this._window = window;
        this._sampleCount = this.ChooseSampleCount(multisampleCount);

        // Query the shader format(s) supported by the selected backend.
        // Pick a single preferred format in priority order: SPIRV > DXIL > MSL.
        var supportedFormats = SDL.GetGPUShaderFormats(gpuDevice);
        if (supportedFormats.HasFlag(SDL.GPUShaderFormat.SPIRV))
        {
            this._shaderFormat = SDL.GPUShaderFormat.SPIRV;
        }
        else if (supportedFormats.HasFlag(SDL.GPUShaderFormat.DXIL))
        {
            this._shaderFormat = SDL.GPUShaderFormat.DXIL;
        }
        else if (supportedFormats.HasFlag(SDL.GPUShaderFormat.MSL))
        {
            this._shaderFormat = SDL.GPUShaderFormat.MSL;
        }
        else
        {
            throw new InvalidOperationException($"GPU device does not support any expected shader format. Supported: {supportedFormats}");
        }
    }

    public int? PreferredFrameRate { get; set; }

    public int MultisampleCount => this._sampleCount switch
    {
        SDL.GPUSampleCount.SampleCount2 => 2,
        SDL.GPUSampleCount.SampleCount4 => 4,
        SDL.GPUSampleCount.SampleCount8 => 8,
        _ => 1,
    };

    // The most samples up to the request that the device has for the swapchain's format
    private SDL.GPUSampleCount ChooseSampleCount(
        int requested)
    {
        var format = this.RenderTargetFormat;

        if ((requested >= 8) && SDL.GPUTextureSupportsSampleCount(this._gpuDevice, format, SDL.GPUSampleCount.SampleCount8))
        {
            return SDL.GPUSampleCount.SampleCount8;
        }

        if ((requested >= 4) && SDL.GPUTextureSupportsSampleCount(this._gpuDevice, format, SDL.GPUSampleCount.SampleCount4))
        {
            return SDL.GPUSampleCount.SampleCount4;
        }

        if ((requested >= 2) && SDL.GPUTextureSupportsSampleCount(this._gpuDevice, format, SDL.GPUSampleCount.SampleCount2))
        {
            return SDL.GPUSampleCount.SampleCount2;
        }

        return SDL.GPUSampleCount.SampleCount1;
    }

    // Sprites begun now draw to the screen, which is multisampled, and not to a render texture:
    // a pipeline's sample count must match its target's. The target can't change during a batch.
    internal bool DrawsMultisampled => (this._sampleCount != SDL.GPUSampleCount.SampleCount1) && (this._currentRenderTarget == nint.Zero);

    internal nint MultisampleTexture => this._multisampleTexture;

    internal nint GPUDevice => this._gpuDevice;

    internal nint Window => this._window;

    internal SDL.GPUShaderFormat ShaderFormat => this._shaderFormat;

    internal nint DefaultSampler => this._defaultSampler;

    internal nint DefaultPipeline => this._defaultPipeline;

    // The default shader's pipeline for each other blend mode, and the samplers other than the
    // default, made when first used
    private readonly nint[] _defaultBlendPipelines = new nint[10];
    private readonly nint[] _samplers = new nint[4];

    internal nint GetDefaultPipeline(
        BlendMode blend)
    {
        bool multisampled = this.DrawsMultisampled;
        if ((blend == BlendMode.Alpha) && !multisampled)
        {
            return this._defaultPipeline;
        }

        ref nint pipeline = ref this._defaultBlendPipelines[(int)blend + (multisampled ? 5 : 0)];
        if (pipeline == nint.Zero)
        {
            pipeline = this.CreateGraphicsPipeline(this._defaultVertexShader, this._defaultFragmentShader, blend, multisampled);
        }

        return pipeline;
    }

    internal nint GetSampler(
        Sampler sampler)
    {
        if (sampler == Sampler.LinearClamp)
        {
            return this._defaultSampler;
        }

        ref nint created = ref this._samplers[(int)sampler];
        if (created == nint.Zero)
        {
            var filter = ((sampler == Sampler.PointClamp) || (sampler == Sampler.PointRepeat)) ? SDL.GPUFilter.Nearest : SDL.GPUFilter.Linear;
            var address = ((sampler == Sampler.LinearRepeat) || (sampler == Sampler.PointRepeat)) ? SDL.GPUSamplerAddressMode.Repeat : SDL.GPUSamplerAddressMode.ClampToEdge;
            var info = new SDL.GPUSamplerCreateInfo
            {
                MinFilter = filter,
                MagFilter = filter,
                MipmapMode = SDL.GPUSamplerMipmapMode.Nearest,
                AddressModeU = address,
                AddressModeV = address,
                AddressModeW = address,
            };
            created = SDL.CreateGPUSampler(this._gpuDevice, in info);
        }

        return created;
    }

    internal nint DefaultVertexShader => this._defaultVertexShader;

    internal nint DefaultFragmentShader => this._defaultFragmentShader;

    public Task OnInitialize()
    {
        this.EnsureDefaultResources();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the default sampler, shaders and sprite pipeline, once: when the game starts, or
    /// earlier for the launch screen, which draws before it does.
    /// </summary>
    internal void EnsureDefaultResources()
    {
        if (this._defaultPipeline != nint.Zero)
        {
            return;
        }

        this.CreateDefaultSampler();
        this.CreateDefaultShaders();
        this.CreateDefaultPipeline();
    }

    public Strategy<ISpriteBatchStrategy> CreateSpriteBatch()
    {
        return new Strategy<ISpriteBatchStrategy>(new SpriteBatchStrategy(this));
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

        var texInfo = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = SDL.GPUTextureFormat.R8G8B8A8Unorm,
            Width = (uint)size.X,
            Height = (uint)size.Y,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
            Usage = SDL.GPUTextureUsageFlags.Sampler,
        };

        var gpuTexture = SDL.CreateGPUTexture(this._gpuDevice, in texInfo);
        if (gpuTexture == nint.Zero)
        {
            throw new InvalidOperationException($"Failed to create GPU texture: {SDL.GetError()}");
        }

        // Upload pixel data
        this.UploadTextureData(gpuTexture, size, data);

        return new Strategy<ITextureStrategy>(new TextureStrategy(gpuTexture, size, this));
    }

    public Strategy<ITextureStrategy> CreateRenderTexture(
        Vector2Int size)
    {
        var texInfo = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            // NOTE: The swapchain's format, as the one pipeline per shader is built for it, and
            // drawing with a pipeline of another format is invalid in all three APIs
            Format = this.RenderTargetFormat,
            Width = (uint)size.X,
            Height = (uint)size.Y,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
            Usage = SDL.GPUTextureUsageFlags.Sampler | SDL.GPUTextureUsageFlags.ColorTarget,
        };

        var gpuTexture = SDL.CreateGPUTexture(this._gpuDevice, in texInfo);
        if (gpuTexture == nint.Zero)
        {
            throw new InvalidOperationException($"Failed to create render texture: {SDL.GetError()}");
        }

        return new Strategy<ITextureStrategy>(new TextureStrategy(gpuTexture, size, this));
    }

    private SDL.GPUTextureFormat RenderTargetFormat => SDL.GetGPUSwapchainTextureFormat(this._gpuDevice, this._window);

    public void SetRenderTarget(
        Strategy<ITextureStrategy>? textureStrategy)
    {
        // Render target switching is handled when the recorder submits the frame: each segment
        // records its target. Store the current target for the segments that follow.
        var target = textureStrategy?.ToConcrete<TextureStrategy>().NativeHandle ?? nint.Zero;

        // A clear belongs to the target it was made on, even if nothing was drawn there before
        // switching away
        if (this._hasPendingClear && (target != this._currentRenderTarget))
        {
            this.Recorder.RecordPendingClear();
        }

        if (textureStrategy is not null)
        {
            var texture = textureStrategy.ToConcrete<TextureStrategy>();
            this._currentRenderTarget = texture.NativeHandle;
            this._currentRenderTargetSize = texture.Size;
        }
        else
        {
            this._currentRenderTarget = nint.Zero;
            this._currentRenderTargetSize = null;
        }
    }

    private nint _currentRenderTarget;
    private Vector2Int? _currentRenderTargetSize;

    internal nint CurrentRenderTarget => this._currentRenderTarget;

    /// <summary>
    /// The size, in texels, of the currently bound render target, or null when
    /// rendering to the swapchain.
    /// </summary>
    internal Vector2Int? CurrentRenderTargetSize => this._currentRenderTargetSize;

    /// <summary>
    /// A render texture's pixels to read back: recorded in the frame where they were asked
    /// for, copied out in the frame's command buffer, and read once it has run.
    /// </summary>
    private sealed class Readback
    {
        public required TextureStrategy Texture;
        public required Memory<Color32> Destination;
        public required TaskCompletionSource Completion;
        public nint TransferBuffer;
    }

    // Frames submitted with readbacks that the GPU hasn't finished, oldest first
    private readonly List<(nint Fence, Readback[] Readbacks)> _pendingReadbacks = new();

    private readonly List<Readback> _readbacks = new List<Readback>();
    private readonly List<nint> _texturesToRelease = new List<nint>();

    public Task ReadPixelsAsync(
        Strategy<ITextureStrategy> textureStrategy,
        Memory<Color32> destination)
    {
        ArgumentNullException.ThrowIfNull(textureStrategy);

        var readback = new Readback()
        {
            Texture = textureStrategy.ToConcrete<TextureStrategy>(),
            Destination = destination,
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        // Made at this point in the frame's drawing, once the frame is submitted
        this.Recorder.RecordReadback(this._readbacks.Count);
        this._readbacks.Add(readback);

        return readback.Completion.Task;
    }

    /// <summary>
    /// Copies a readback's texture into a transfer buffer, in a copy pass of its own, as the
    /// recorder reaches it in the frame.
    /// </summary>
    internal void EncodeReadback(
        nint commandBuffer,
        int readbackIndex)
    {
        var readback = this._readbacks[readbackIndex];
        var size = readback.Texture.Size;

        var transferInfo = new SDL.GPUTransferBufferCreateInfo
        {
            Usage = SDL.GPUTransferBufferUsage.Download,
            Size = (uint)(size.X * size.Y * 4),
        };

        readback.TransferBuffer = SDL.CreateGPUTransferBuffer(this._gpuDevice, in transferInfo);
        if (readback.TransferBuffer == nint.Zero)
        {
            readback.Completion.TrySetException(new InvalidOperationException($"Failed to create a readback buffer: {SDL.GetError()}"));
            return;
        }

        var copyPass = SDL.BeginGPUCopyPass(commandBuffer);

        var source = new SDL.GPUTextureRegion
        {
            Texture = readback.Texture.NativeHandle,
            W = (uint)size.X,
            H = (uint)size.Y,
            D = 1,
        };

        var destination = new SDL.GPUTextureTransferInfo
        {
            TransferBuffer = readback.TransferBuffer,
            Offset = 0,
        };

        SDL.DownloadFromGPUTexture(copyPass, in source, in destination);
        SDL.EndGPUCopyPass(copyPass);
    }

    // Hands over the pixels of the readbacks whose frames the GPU has finished. Nothing waits for
    // the GPU, so a video capture's readback each frame doesn't stall the game thread.
    private void CompleteFinishedReadbacks()
    {
        while (this._pendingReadbacks.Count > 0)
        {
            var (fence, readbacks) = this._pendingReadbacks[0];
            if (!SDL.QueryGPUFence(this._gpuDevice, fence))
            {
                return;
            }

            SDL.ReleaseGPUFence(this._gpuDevice, fence);
            this._pendingReadbacks.RemoveAt(0);
            this.CompleteReadbacks(readbacks);
        }
    }

    private unsafe void CompleteReadbacks(
        Readback[] readbacks)
    {
        foreach (var readback in readbacks)
        {
            if (readback.TransferBuffer == nint.Zero)
            {
                readback.Completion.TrySetException(new InvalidOperationException("The render texture wasn't read back: the frame had nothing to draw on."));
                continue;
            }

            if (readback.TransferBuffer == nint.Zero)
            {
                // Never encoded: the frame wasn't drawn
                readback.Completion.TrySetException(new InvalidOperationException("The frame the pixels were drawn in wasn't drawn."));
                continue;
            }

            var size = readback.Texture.Size;
            int pixelCount = size.X * size.Y;
            void* mapped = (void*)SDL.MapGPUTransferBuffer(this._gpuDevice, readback.TransferBuffer, false);
            if (mapped is null)
            {
                readback.Completion.TrySetException(new InvalidOperationException($"Failed to read back a render texture: {SDL.GetError()}"));
            }
            else
            {
                // Top row first, in the swapchain's format, which is usually BGRA
                var pixels = new ReadOnlySpan<Color32>(mapped, pixelCount);
                var destination = readback.Destination.Span;
                pixels.CopyTo(destination);
                if (this.RenderTargetFormat == SDL.GPUTextureFormat.B8G8R8A8Unorm)
                {
                    foreach (ref var pixel in destination.Slice(0, pixelCount))
                    {
                        pixel = Color32.FromPremultiplied(pixel.B, pixel.G, pixel.R, pixel.A);
                    }
                }

                SDL.UnmapGPUTransferBuffer(this._gpuDevice, readback.TransferBuffer);
                readback.Completion.TrySetResult();
            }

            SDL.ReleaseGPUTransferBuffer(this._gpuDevice, readback.TransferBuffer);
        }
    }

    /// <summary>
    /// Finishes with the device, before it's destroyed: readbacks in flight complete, and textures
    /// and shaders disposed or finalized later don't touch it.
    /// </summary>
    internal void Shutdown()
    {
        SDL.WaitForGPUIdle(this._gpuDevice);
        this.CompleteFinishedReadbacks();
        this.ReleasePendingTextures();

        this.Recorder.ReleaseBuffers();

        if (this._multisampleTexture != nint.Zero)
        {
            SDL.ReleaseGPUTexture(this._gpuDevice, this._multisampleTexture);
            this._multisampleTexture = nint.Zero;
        }

        if (this._defaultPipeline != nint.Zero)
        {
            SDL.ReleaseGPUGraphicsPipeline(this._gpuDevice, this._defaultPipeline);
            this._defaultPipeline = nint.Zero;
        }

        if (this._defaultVertexShader != nint.Zero)
        {
            SDL.ReleaseGPUShader(this._gpuDevice, this._defaultVertexShader);
            this._defaultVertexShader = nint.Zero;
        }

        if (this._defaultFragmentShader != nint.Zero)
        {
            SDL.ReleaseGPUShader(this._gpuDevice, this._defaultFragmentShader);
            this._defaultFragmentShader = nint.Zero;
        }

        if (this._defaultSampler != nint.Zero)
        {
            SDL.ReleaseGPUSampler(this._gpuDevice, this._defaultSampler);
            this._defaultSampler = nint.Zero;
        }

        for (int i = 0; i < this._samplers.Length; i++)
        {
            if (this._samplers[i] != nint.Zero)
            {
                SDL.ReleaseGPUSampler(this._gpuDevice, this._samplers[i]);
                this._samplers[i] = nint.Zero;
            }
        }

        for (int i = 0; i < this._defaultBlendPipelines.Length; i++)
        {
            if (this._defaultBlendPipelines[i] != nint.Zero)
            {
                SDL.ReleaseGPUGraphicsPipeline(this._gpuDevice, this._defaultBlendPipelines[i]);
                this._defaultBlendPipelines[i] = nint.Zero;
            }
        }

        this.IsDeviceAlive = false;
    }

    /// <summary>
    /// False once the device is destroyed.
    /// </summary>
    internal bool IsDeviceAlive { get; private set; } = true;

    public void Clear(Color color)
    {
        // Clear is handled via the render pass load operation with a clear color
        this._clearColor = new SDL.FColor { R = color.R, G = color.G, B = color.B, A = color.A };
        this._hasPendingClear = true;
    }

    internal SDL.FColor _clearColor;
    internal bool _hasPendingClear;

    public void SetScissorRectangle(
        RectangleInt? rectangle)
    {
        this._scissorRectangle = rectangle;
    }

    internal RectangleInt? _scissorRectangle;

    /// <summary>
    /// Records the frame's sprites, from every sprite batch, and draws them when the frame is
    /// submitted.
    /// </summary>
    internal DrawRecorder Recorder => this._recorder ??= new DrawRecorder(this);

    private DrawRecorder? _recorder;

    // Releases a GPU texture once no recorded frame can refer to it. The recorder holds native
    // handles until the frame is submitted, and a texture can be disposed right after it's
    // drawn, or on the finalizer thread. SDL keeps a released texture alive for the frames in
    // flight, but not for one that hasn't been recorded yet.
    internal void ReleaseTexture(
        nint nativeHandle)
    {
        lock (this._texturesToRelease)
        {
            if (this.IsDeviceAlive)
            {
                this._texturesToRelease.Add(nativeHandle);
            }
        }
    }

    private void ReleasePendingTextures()
    {
        lock (this._texturesToRelease)
        {
            foreach (var nativeHandle in this._texturesToRelease)
            {
                SDL.ReleaseGPUTexture(this._gpuDevice, nativeHandle);
            }

            this._texturesToRelease.Clear();
        }
    }

    internal bool AcquireSwapchain()
    {
        this.Recorder.Reset();

        this.ReleasePendingTextures();
        this.CompleteFinishedReadbacks();

        this._currentCommandBuffer = SDL.AcquireGPUCommandBuffer(this._gpuDevice);
        if (this._currentCommandBuffer == nint.Zero)
        {
            this.OnAcquireFailed();
            return false;
        }

        if (!SDL.WaitAndAcquireGPUSwapchainTexture(
            this._currentCommandBuffer,
            this._window,
            out this._swapchainTexture,
            out this._swapchainWidth,
            out this._swapchainHeight))
        {
            // Failed to acquire swapchain, submit the empty command buffer
            SDL.SubmitGPUCommandBuffer(this._currentCommandBuffer);
            this._currentCommandBuffer = nint.Zero;
            this.OnAcquireFailed();
            return false;
        }

        // NOTE: From here SDL is working, even if this frame has no texture
        this._acquireFailingSince = 0;

        if (this._swapchainTexture == nint.Zero)
        {
            // Too many frames in flight, or window is minimized/occluded
            SDL.SubmitGPUCommandBuffer(this._currentCommandBuffer);
            this._currentCommandBuffer = nint.Zero;
            return false;
        }

        // Compute the display scale (physical / logical) so the sprite batch
        // can use logical coordinates for the projection matrix while
        // rendering at the full physical resolution.
        SDL.GetWindowSize(this._window, out int logicalW, out _);
        this._displayScale = (logicalW > 0)
            ? (float)this._swapchainWidth / logicalW
            : 1f;

        this.PrepareMultisampleTexture();

        return true;
    }

    // Sized to the swapchain, which follows the window
    private void PrepareMultisampleTexture()
    {
        if ((this._sampleCount == SDL.GPUSampleCount.SampleCount1)
            || ((this._multisampleWidth == this._swapchainWidth) && (this._multisampleHeight == this._swapchainHeight)))
        {
            return;
        }

        if (this._multisampleTexture != nint.Zero)
        {
            SDL.ReleaseGPUTexture(this._gpuDevice, this._multisampleTexture);
        }

        var info = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = this.RenderTargetFormat,
            Width = this._swapchainWidth,
            Height = this._swapchainHeight,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = this._sampleCount,
            Usage = SDL.GPUTextureUsageFlags.ColorTarget,
        };

        this._multisampleTexture = SDL.CreateGPUTexture(this._gpuDevice, in info);
        if (this._multisampleTexture == nint.Zero)
        {
            throw new InvalidOperationException($"Failed to create the multisampled target: {SDL.GetError()}");
        }

        this._multisampleWidth = this._swapchainWidth;
        this._multisampleHeight = this._swapchainHeight;
    }

    // A failed acquire, unlike having no texture for now, is an error from SDL. One can pass, as
    // a window changes, but a lost device (a driver update, an unplugged GPU) never recovers, and
    // would be a frozen window with no report.
    private void OnAcquireFailed()
    {
        long now = Environment.TickCount64;

        if (this._acquireFailingSince == 0)
        {
            this._acquireFailingSince = now;
            Trace.Write(0x71d4c8e6, SDL.GetError());
        }
        else if ((now - this._acquireFailingSince) > AcquireFailureLimitMilliseconds)
        {
            GameHost.Terminate(0x0be97a35, new InvalidOperationException(
                $"The GPU device stopped responding: {SDL.GetError()}"));
        }
    }

    internal nint CurrentCommandBuffer => this._currentCommandBuffer;

    internal nint SwapchainTexture => this._swapchainTexture;

    internal uint SwapchainWidth => this._swapchainWidth;

    internal uint SwapchainHeight => this._swapchainHeight;

    internal float DisplayScale => this._displayScale;

    internal void SubmitFrame()
    {
        if (this._currentCommandBuffer != nint.Zero)
        {
            this.Recorder.Submit(this._currentCommandBuffer);

            if (this._readbacks.Count > 0)
            {
                // The readbacks complete on a later frame, once the GPU has done this one
                var fence = SDL.SubmitGPUCommandBufferAndAcquireFence(this._currentCommandBuffer);
                if (fence == nint.Zero)
                {
                    GameHost.ReportNonFatal(0x3c8a06f2, new InvalidOperationException(
                        $"Submitting a frame failed: {SDL.GetError()}"));
                    this.CompleteReadbacks(this._readbacks.ToArray());
                }
                else
                {
                    this._pendingReadbacks.Add((fence, this._readbacks.ToArray()));
                }

                this._readbacks.Clear();
            }
            else
            {
                if (!SDL.SubmitGPUCommandBuffer(this._currentCommandBuffer))
                {
                    GameHost.ReportNonFatal(0x3c8a06f1, new InvalidOperationException(
                        $"Submitting a frame failed: {SDL.GetError()}"));
                }
            }

            this._currentCommandBuffer = nint.Zero;
        }
        else if (this._readbacks.Count > 0)
        {
            this.CompleteReadbacks(this._readbacks.ToArray());
            this._readbacks.Clear();
        }

        this.Recorder.Reset();
    }

    private void UploadTextureData(
        nint gpuTexture,
        Vector2Int size,
        ReadOnlySpan<Color32> data)
    {
        // NOTE: Color32 and the texture share a layout
        this.UploadPixels(gpuTexture, size, MemoryMarshal.AsBytes(data));
    }

    /// <summary>
    /// Fills a texture with RGBA8 pixels.
    /// </summary>
    internal unsafe void UploadPixels(
        nint gpuTexture,
        Vector2Int size,
        ReadOnlySpan<byte> pixels,
        int x = 0,
        int y = 0)
    {
        var transferInfo = new SDL.GPUTransferBufferCreateInfo
        {
            Usage = SDL.GPUTransferBufferUsage.Upload,
            Size = (uint)pixels.Length,
        };

        var transferBuffer = SDL.CreateGPUTransferBuffer(this._gpuDevice, in transferInfo);
        if (transferBuffer == nint.Zero)
        {
            throw new InvalidOperationException($"Failed to create an upload buffer: {SDL.GetError()}");
        }

        void* mapped = (void*)SDL.MapGPUTransferBuffer(this._gpuDevice, transferBuffer, false);
        if (mapped is null)
        {
            SDL.ReleaseGPUTransferBuffer(this._gpuDevice, transferBuffer);
            throw new InvalidOperationException($"Failed to map an upload buffer: {SDL.GetError()}");
        }

        pixels.CopyTo(new Span<byte>(mapped, pixels.Length));
        SDL.UnmapGPUTransferBuffer(this._gpuDevice, transferBuffer);

        var cmdBuf = SDL.AcquireGPUCommandBuffer(this._gpuDevice);
        var copyPass = SDL.BeginGPUCopyPass(cmdBuf);

        var source = new SDL.GPUTextureTransferInfo
        {
            TransferBuffer = transferBuffer,
            Offset = 0,
        };

        var destination = new SDL.GPUTextureRegion
        {
            Texture = gpuTexture,
            X = (uint)x,
            Y = (uint)y,
            W = (uint)size.X,
            H = (uint)size.Y,
            D = 1,
        };

        SDL.UploadToGPUTexture(copyPass, in source, in destination, false);
        SDL.EndGPUCopyPass(copyPass);
        SDL.SubmitGPUCommandBuffer(cmdBuf);
        SDL.ReleaseGPUTransferBuffer(this._gpuDevice, transferBuffer);
    }

    private unsafe void CreateDefaultSampler()
    {
        var samplerInfo = new SDL.GPUSamplerCreateInfo
        {
            MinFilter = SDL.GPUFilter.Linear,
            MagFilter = SDL.GPUFilter.Linear,
            MipmapMode = SDL.GPUSamplerMipmapMode.Nearest,
            AddressModeU = SDL.GPUSamplerAddressMode.ClampToEdge,
            AddressModeV = SDL.GPUSamplerAddressMode.ClampToEdge,
            AddressModeW = SDL.GPUSamplerAddressMode.ClampToEdge,
        };

        this._defaultSampler = SDL.CreateGPUSampler(this._gpuDevice, in samplerInfo);
    }

    private unsafe void CreateDefaultShaders()
    {
        switch (this._shaderFormat)
        {
            case SDL.GPUShaderFormat.SPIRV:
                this.CreateDefaultShadersFromBinary(
                    DefaultShaderSource.VertexShaderSpirV,
                    DefaultShaderSource.FragmentShaderSpirV,
                    SDL.GPUShaderFormat.SPIRV);
                break;

            case SDL.GPUShaderFormat.DXIL:
                this.CreateDefaultShadersFromBinary(
                    DefaultShaderSource.VertexShaderDXIL,
                    DefaultShaderSource.FragmentShaderDXIL,
                    SDL.GPUShaderFormat.DXIL);
                break;

            case SDL.GPUShaderFormat.MSL:
                this.CreateDefaultShadersFromMSL(
                    DefaultShaderSource.VertexShaderMSL,
                    DefaultShaderSource.FragmentShaderMSL);
                break;

            default:
                throw new InvalidOperationException($"Unsupported shader format: {this._shaderFormat}");
        }
    }

    private unsafe void CreateDefaultShadersFromBinary(
        ReadOnlySpan<byte> vertexCode,
        ReadOnlySpan<byte> fragmentCode,
        SDL.GPUShaderFormat format)
    {
        fixed (byte* vertexPtr = vertexCode)
        {
            var vertexInfo = new SDL.GPUShaderCreateInfo
            {
                Code = (nint)vertexPtr,
                CodeSize = (nuint)vertexCode.Length,
                Entrypoint = "main",
                Format = format,
                Stage = SDL.GPUShaderStage.Vertex,
                NumSamplers = 0,
                NumStorageBuffers = 0,
                NumStorageTextures = 0,
                NumUniformBuffers = 1,
            };

            this._defaultVertexShader = SDL.CreateGPUShader(this._gpuDevice, in vertexInfo);
            if (this._defaultVertexShader == nint.Zero)
            {
                throw new InvalidOperationException($"Failed to create default vertex shader: {SDL.GetError()}");
            }
        }

        fixed (byte* fragmentPtr = fragmentCode)
        {
            var fragmentInfo = new SDL.GPUShaderCreateInfo
            {
                Code = (nint)fragmentPtr,
                CodeSize = (nuint)fragmentCode.Length,
                Entrypoint = "main",
                Format = format,
                Stage = SDL.GPUShaderStage.Fragment,
                NumSamplers = 1,
                NumStorageBuffers = 0,
                NumStorageTextures = 0,
                NumUniformBuffers = 0,
            };

            this._defaultFragmentShader = SDL.CreateGPUShader(this._gpuDevice, in fragmentInfo);
            if (this._defaultFragmentShader == nint.Zero)
            {
                throw new InvalidOperationException($"Failed to create default fragment shader: {SDL.GetError()}");
            }
        }
    }

    private unsafe void CreateDefaultShadersFromMSL(
        string vertexMsl,
        string fragmentMsl)
    {
        var vertexBytes = Encoding.UTF8.GetBytes(vertexMsl);
        var fragmentBytes = Encoding.UTF8.GetBytes(fragmentMsl);

        fixed (byte* vertexPtr = vertexBytes)
        {
            var vertexInfo = new SDL.GPUShaderCreateInfo
            {
                Code = (nint)vertexPtr,
                CodeSize = (nuint)vertexBytes.Length,
                Entrypoint = "main0",
                Format = SDL.GPUShaderFormat.MSL,
                Stage = SDL.GPUShaderStage.Vertex,
                NumSamplers = 0,
                NumStorageBuffers = 0,
                NumStorageTextures = 0,
                NumUniformBuffers = 1,
            };

            this._defaultVertexShader = SDL.CreateGPUShader(this._gpuDevice, in vertexInfo);
            if (this._defaultVertexShader == nint.Zero)
            {
                throw new InvalidOperationException($"Failed to create default vertex shader (MSL): {SDL.GetError()}");
            }
        }

        fixed (byte* fragmentPtr = fragmentBytes)
        {
            var fragmentInfo = new SDL.GPUShaderCreateInfo
            {
                Code = (nint)fragmentPtr,
                CodeSize = (nuint)fragmentBytes.Length,
                Entrypoint = "main0",
                Format = SDL.GPUShaderFormat.MSL,
                Stage = SDL.GPUShaderStage.Fragment,
                NumSamplers = 1,
                NumStorageBuffers = 0,
                NumStorageTextures = 0,
                NumUniformBuffers = 0,
            };

            this._defaultFragmentShader = SDL.CreateGPUShader(this._gpuDevice, in fragmentInfo);
            if (this._defaultFragmentShader == nint.Zero)
            {
                throw new InvalidOperationException($"Failed to create default fragment shader (MSL): {SDL.GetError()}");
            }
        }
    }

    private void CreateDefaultPipeline()
    {
        this._defaultPipeline = this.CreateGraphicsPipeline(
            this._defaultVertexShader,
            this._defaultFragmentShader);
    }

    internal unsafe nint CreateGraphicsPipeline(
        nint vertexShader,
        nint fragmentShader,
        BlendMode blend = BlendMode.Alpha,
        bool multisampled = false)
    {
        // Colors are premultiplied
        var (srcColor, dstColor, srcAlpha, dstAlpha) = blend switch
        {
            BlendMode.Additive => (SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.One),
            BlendMode.Multiply => (SDL.GPUBlendFactor.DstColor, SDL.GPUBlendFactor.OneMinusSrcAlpha, SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.OneMinusSrcAlpha),
            BlendMode.Screen => (SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.OneMinusSrcColor, SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.OneMinusSrcAlpha),
            BlendMode.Opaque => (SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.Zero, SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.Zero),
            _ => (SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.OneMinusSrcAlpha, SDL.GPUBlendFactor.One, SDL.GPUBlendFactor.OneMinusSrcAlpha),
        };

        var colorTargetDesc = new SDL.GPUColorTargetDescription
        {
            Format = SDL.GetGPUSwapchainTextureFormat(this._gpuDevice, this._window),
            BlendState = new SDL.GPUColorTargetBlendState
            {
                SrcColorBlendFactor = srcColor,
                DstColorBlendFactor = dstColor,
                ColorBlendOp = SDL.GPUBlendOp.Add,
                SrcAlphaBlendFactor = srcAlpha,
                DstAlphaBlendFactor = dstAlpha,
                AlphaBlendOp = SDL.GPUBlendOp.Add,
                EnableBlend = true,
                ColorWriteMask = SDL.GPUColorComponentFlags.R | SDL.GPUColorComponentFlags.G | SDL.GPUColorComponentFlags.B | SDL.GPUColorComponentFlags.A,
            },
        };

        var vertexAttributes = new SDL.GPUVertexAttribute[]
        {
            new() { Location = 0, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 0 },                                        // Position
            new() { Location = 1, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Ubyte4Norm, Offset = SpriteVertex.ColorOffset },              // Color
            new() { Location = 2, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = SpriteVertex.TextureCoordinateOffset },     // TexCoord
            new() { Location = 3, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Half4, Offset = SpriteVertex.CustomDataOffset },             // CustomData
        };

        var vertexBufferDesc = new SDL.GPUVertexBufferDescription
        {
            Slot = 0,
            Pitch = SpriteVertex.Size,
            InputRate = SDL.GPUVertexInputRate.Vertex,
            InstanceStepRate = 0,
        };

        fixed (SDL.GPUVertexAttribute* attribsPtr = vertexAttributes)
        {
            var vertexInputState = new SDL.GPUVertexInputState
            {
                VertexBufferDescriptions = (nint)(&vertexBufferDesc),
                NumVertexBuffers = 1,
                VertexAttributes = (nint)attribsPtr,
                NumVertexAttributes = (uint)vertexAttributes.Length,
            };

            var pipelineInfo = new SDL.GPUGraphicsPipelineCreateInfo
            {
                VertexShader = vertexShader,
                FragmentShader = fragmentShader,
                VertexInputState = vertexInputState,
                PrimitiveType = SDL.GPUPrimitiveType.TriangleList,
                RasterizerState = new SDL.GPURasterizerState
                {
                    CullMode = SDL.GPUCullMode.None,
                    FillMode = SDL.GPUFillMode.Fill,
                    FrontFace = SDL.GPUFrontFace.CounterClockwise,
                    EnableDepthClip = true,
                },
                MultisampleState = new SDL.GPUMultisampleState
                {
                    SampleCount = multisampled ? this._sampleCount : SDL.GPUSampleCount.SampleCount1,
                },
                TargetInfo = new SDL.GPUGraphicsPipelineTargetInfo
                {
                    ColorTargetDescriptions = (nint)(&colorTargetDesc),
                    NumColorTargets = 1,
                },
            };

            var pipeline = SDL.CreateGPUGraphicsPipeline(this._gpuDevice, in pipelineInfo);
            if (pipeline == nint.Zero)
            {
                throw new InvalidOperationException($"Failed to create graphics pipeline: {SDL.GetError()}");
            }

            return pipeline;
        }
    }
}
