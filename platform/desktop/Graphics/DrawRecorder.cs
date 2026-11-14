// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using DotGame.Framework.Platform.Common.Graphics;

namespace DotGame.Framework.Desktop.Graphics;

/// <summary>
/// Records a frame's sprites and draws them when the frame is submitted, with one vertex upload
/// and one render pass per render target, instead of an upload and a render pass for every
/// sprite batch flush.
/// </summary>
/// <remarks>
/// <para>
/// Sprites are grouped into segments: runs that share a render target, a pipeline, its fragment
/// uniform values, a texture and a scissor rectangle. A sprite batch's <c>End</c> doesn't draw;
/// a later batch with the same state continues the segment, so ending and beginning a batch
/// around a change that turns out to change nothing costs nothing.
/// </para>
/// <para>
/// The recorder belongs to the graphics service, not to a sprite batch, so sprites drawn by
/// several batches are drawn in the order they were submitted. State that a sprite batch used
/// to read when it flushed (the render target, the scissor rectangle, a pending clear, the
/// shader's uniform values) is captured when a segment starts.
/// </para>
/// </remarks>
internal sealed class DrawRecorder
{
    internal const int VerticesPerSprite = 4;

    private const int IndicesPerSprite = 6;
    private const int InitialCapacity = 256;
    private const int MaxUniformBytes = 256;

    // 16-bit indices address 65,536 vertices; a draw is limited to that many, and longer
    // segments are drawn in parts with a base vertex
    private const int MaxSpritesPerDraw = 65536 / VerticesPerSprite;

    private readonly GraphicsServiceStrategy _graphicsService;

    private SpriteVertex[] _vertices = new SpriteVertex[InitialCapacity * VerticesPerSprite];
    private int _spriteCount;

    private Segment[] _segments = new Segment[64];
    private int _segmentCount;

    private byte[] _uniformData = new byte[64 * MaxUniformBytes];
    private int _uniformLength;

    // GPU buffers, reused from frame to frame. The index buffer holds the fixed quad pattern
    // and is uploaded only when it grows.
    private nint _vertexTransferBuffer;
    private nint _vertexBuffer;
    private int _vertexBufferCapacity;
    private nint _indexBuffer;
    private int _indexBufferCapacity;

    // Binding arrays, reused so drawing a frame allocates nothing
    private readonly SDL.GPUColorTargetInfo[] _colorTargets = new SDL.GPUColorTargetInfo[1];
    private readonly SDL.GPUBufferBinding[] _vertexBinding = new SDL.GPUBufferBinding[1];
    private readonly SDL.GPUTextureSamplerBinding[] _samplerBinding = new SDL.GPUTextureSamplerBinding[1];

    internal DrawRecorder(
        GraphicsServiceStrategy graphicsService)
    {
        this._graphicsService = graphicsService;
    }

    /// <summary>
    /// A run of sprites drawn with the same state.
    /// </summary>
    private nint _sampler;

    private struct Segment
    {
        public int FirstSprite;
        public int SpriteCount;
        public nint RenderTarget;          // zero for the swapchain
        public Vector2Int? RenderTargetSize;
        public bool Clear;
        public SDL.FColor ClearColor;
        public nint Pipeline;
        public ShaderStrategy? Shader;     // null for the default shader
        public int UniformVersion;         // the shader's when the segment started
        public int UniformOffset;
        public int UniformLength;
        public nint Texture;
        public nint Sampler;
        public RectangleInt? Scissor;

        // One more than the index of a pixel readback to make at this point in the frame, or 0
        public int Readback;
    }

    /// <summary>
    /// Starts recording a frame, discarding anything recorded since the last one was
    /// submitted (as happens when a frame had no swapchain to draw to).
    /// </summary>
    // A scissor in the target's texels: its edges convert separately, floor the start and ceil the
    // end, so fractional scales don't lose a pixel, and it stays inside the target, as a rectangle
    // outside is invalid on Vulkan and Metal. An empty one draws nothing.
    private static SDL.Rect ToScissor(
        RectangleInt? scissor,
        float displayScale,
        float viewportW,
        float viewportH)
    {
        if (scissor is not { } rectangle)
        {
            return new SDL.Rect { X = 0, Y = 0, W = (int)viewportW, H = (int)viewportH };
        }

        int left = Math.Clamp((int)MathF.Floor(rectangle.X * displayScale), 0, (int)viewportW);
        int top = Math.Clamp((int)MathF.Floor(rectangle.Y * displayScale), 0, (int)viewportH);
        int right = Math.Clamp((int)MathF.Ceiling((rectangle.X + rectangle.Width) * displayScale), left, (int)viewportW);
        int bottom = Math.Clamp((int)MathF.Ceiling((rectangle.Y + rectangle.Height) * displayScale), top, (int)viewportH);

        return new SDL.Rect { X = left, Y = top, W = right - left, H = bottom - top };
    }

    /// <summary>
    /// Releases the recorder's GPU buffers, before the device is destroyed.
    /// </summary>
    internal void ReleaseBuffers()
    {
        var device = this._graphicsService.GPUDevice;

        if (this._vertexTransferBuffer != nint.Zero)
        {
            SDL.ReleaseGPUTransferBuffer(device, this._vertexTransferBuffer);
            this._vertexTransferBuffer = nint.Zero;
        }

        if (this._vertexBuffer != nint.Zero)
        {
            SDL.ReleaseGPUBuffer(device, this._vertexBuffer);
            this._vertexBuffer = nint.Zero;
        }

        if (this._indexBuffer != nint.Zero)
        {
            SDL.ReleaseGPUBuffer(device, this._indexBuffer);
            this._indexBuffer = nint.Zero;
        }
    }

    internal void Reset()
    {
        this._spriteCount = 0;
        this._segmentCount = 0;
        this._uniformLength = 0;
    }

    // NOTE: A change closes the segment here, at a batch's Begin, so adding a sprite has nothing
    // more to compare: the empty segment's texture matches no sprite's, and replay skips it
    internal void SetSampler(
        nint sampler)
    {
        if (sampler != this._sampler)
        {
            this._sampler = sampler;
            if (this._segmentCount > 0)
            {
                this.StartSegment(null, nint.Zero, nint.Zero);
            }
        }
    }

    /// <summary>
    /// Returns where to write the next sprite's four vertices, extending
    /// the current segment or starting one when the state differs.
    /// </summary>
    /// <param name="shader">The batch's shader, or null for the default one.</param>
    /// <param name="pipeline">The pipeline for the shader and the batch's blend.</param>
    /// <param name="texture">The sprite's texture.</param>
    /// <remarks>
    /// A parameter set between two sprites starts a segment with the new values, as on every
    /// platform: the shader's <see cref="ShaderStrategy.UniformVersion"/> tells.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref SpriteVertex AddSprite(
        ShaderStrategy? shader,
        nint pipeline,
        nint texture)
    {
        var service = this._graphicsService;

        bool continues = false;
        if ((this._segmentCount > 0) && !service._hasPendingClear)
        {
            ref var last = ref this._segments[this._segmentCount - 1];
            continues = (last.Texture == texture)
                && (last.Pipeline == pipeline)
                && (last.RenderTarget == service.CurrentRenderTarget)
                && (last.Scissor == service._scissorRectangle)
                && (last.Shader == shader)
                && (last.UniformVersion == (shader?.UniformVersion ?? 0));
        }

        if (!continues)
        {
            this.StartSegment(shader, pipeline, texture);
        }

        this._segments[this._segmentCount - 1].SpriteCount++;

        int start = this._spriteCount * VerticesPerSprite;
        if (start + VerticesPerSprite > this._vertices.Length)
        {
            Array.Resize(ref this._vertices, this._vertices.Length * 2);
        }

        this._spriteCount++;
        return ref this._vertices[start];
    }

    private static int GetUniformData(
        ShaderStrategy? shader,
        Span<byte> buffer)
    {
        if ((shader is null) || (shader.FragmentUniformBufferCount == 0))
        {
            return 0;
        }

        buffer.Clear();
        return (int)shader.GetFragmentUniformData(buffer);
    }

    private void StartSegment(
        ShaderStrategy? shader,
        nint pipeline,
        nint texture)
    {
        var service = this._graphicsService;

        if (this._segmentCount == this._segments.Length)
        {
            Array.Resize(ref this._segments, this._segments.Length * 2);
        }

        if (this._uniformLength + MaxUniformBytes > this._uniformData.Length)
        {
            Array.Resize(ref this._uniformData, this._uniformData.Length * 2);
        }

        int uniformLength = GetUniformData(shader, this._uniformData.AsSpan(this._uniformLength, MaxUniformBytes));

        this._segments[this._segmentCount++] = new Segment
        {
            FirstSprite = this._spriteCount,
            SpriteCount = 0,
            Sampler = this._sampler,
            RenderTarget = service.CurrentRenderTarget,
            RenderTargetSize = service.CurrentRenderTargetSize,
            Clear = service._hasPendingClear,
            ClearColor = service._clearColor,
            Pipeline = pipeline,
            Shader = shader,
            UniformVersion = shader?.UniformVersion ?? 0,
            UniformOffset = this._uniformLength,
            UniformLength = uniformLength,
            Texture = texture,
            Scissor = service._scissorRectangle,
        };

        this._uniformLength += uniformLength;
        service._hasPendingClear = false;
    }

    /// <summary>
    /// Records a pending clear of the current render target as a segment of its own, for when
    /// the target changes before anything is drawn after the clear.
    /// </summary>
    internal void RecordPendingClear()
    {
        if (this._graphicsService._hasPendingClear)
        {
            this.StartSegment(null, this._graphicsService.DefaultPipeline, nint.Zero);
        }
    }

    /// <summary>
    /// Records a readback of a render texture's pixels, made once everything recorded before it
    /// has been drawn (see <see cref="GraphicsServiceStrategy.EncodeReadback"/>).
    /// </summary>
    internal void RecordReadback(
        int readbackIndex)
    {
        // A clear of the target made before the readback belongs before it
        this.RecordPendingClear();

        if (this._segmentCount == this._segments.Length)
        {
            Array.Resize(ref this._segments, this._segments.Length * 2);
        }

        this._segments[this._segmentCount++] = new Segment
        {
            FirstSprite = this._spriteCount,
            SpriteCount = 0,
            RenderTarget = this._graphicsService.CurrentRenderTarget,
            RenderTargetSize = this._graphicsService.CurrentRenderTargetSize,
            Readback = readbackIndex + 1,
        };
    }

    /// <summary>
    /// Draws everything recorded this frame into the command buffer: one upload of the frame's
    /// vertices, then a render pass per render target (and per clear), with the state changes
    /// between segments.
    /// </summary>
    internal unsafe void Submit(
        nint commandBuffer)
    {
        var service = this._graphicsService;

        // A clear with nothing drawn after it still clears
        this.RecordPendingClear();

        if (this._segmentCount == 0)
        {
            return;
        }

        var device = service.GPUDevice;
        if (this._spriteCount > 0)
        {
            this.Upload(device, commandBuffer);
        }

        nint renderPass = nint.Zero;
        nint passTarget = nint.Zero;
        nint boundPipeline = nint.Zero;
        nint boundTexture = nint.Zero;
        nint boundSampler = nint.Zero;
        RectangleInt? boundScissor = null;
        float displayScale = 1f;
        float viewportW = 0f;
        float viewportH = 0f;
        this._vertexBinding[0] = new SDL.GPUBufferBinding { Buffer = this._vertexBuffer, Offset = 0 };
        Span<byte> projection = stackalloc byte[64];
        int drawCalls = 0;
        int drawnSprites = 0;

        for (int i = 0; i < this._segmentCount; i++)
        {
            ref var segment = ref this._segments[i];

            // A readback copies its texture out between render passes
            if (segment.Readback != 0)
            {
                if (renderPass != nint.Zero)
                {
                    SDL.EndGPURenderPass(renderPass);
                    renderPass = nint.Zero;
                }

                service.EncodeReadback(commandBuffer, segment.Readback - 1);
                continue;
            }

            nint target = (segment.RenderTarget != nint.Zero) ? segment.RenderTarget : service.SwapchainTexture;
            if (target == nint.Zero)
            {
                continue;
            }

            if ((renderPass != nint.Zero) && ((target != passTarget) || segment.Clear))
            {
                SDL.EndGPURenderPass(renderPass);
                renderPass = nint.Zero;
            }

            if (renderPass == nint.Zero)
            {
                // The screen, when multisampled, is drawn in its own texture and resolved at the
                // end of each pass. That texture is stored too, as a later pass in the frame goes on
                // from it: one that follows a render texture's, say.
                nint multisample = (segment.RenderTarget == nint.Zero) ? service.MultisampleTexture : nint.Zero;
                this._colorTargets[0] = new SDL.GPUColorTargetInfo
                {
                    Texture = (multisample != nint.Zero) ? multisample : target,
                    LoadOp = segment.Clear ? SDL.GPULoadOp.Clear : SDL.GPULoadOp.Load,
                    StoreOp = (multisample != nint.Zero) ? SDL.GPUStoreOp.ResolveAndStore : SDL.GPUStoreOp.Store,
                    ClearColor = segment.ClearColor,
                    ResolveTexture = (multisample != nint.Zero) ? target : nint.Zero,
                };

                renderPass = SDL.BeginGPURenderPass(commandBuffer, in this._colorTargets, 1, nint.Zero);
                passTarget = target;

                // The swapchain is drawn in logical (DPI-independent) units at its physical
                // resolution; an offscreen render target in its own texels
                displayScale = (segment.RenderTargetSize is null) ? service.DisplayScale : 1f;
                viewportW = (segment.RenderTargetSize is { } size) ? size.X : service.SwapchainWidth;
                viewportH = (segment.RenderTargetSize is { } size2) ? size2.Y : service.SwapchainHeight;
                WriteProjection(projection, viewportW / displayScale, viewportH / displayScale);

                // NOTE: Once per pass: SDL keeps uniforms for the command buffer
                fixed (byte* projectionPtr = projection)
                {
                    SDL.PushGPUVertexUniformData(commandBuffer, 0, (nint)projectionPtr, 64);
                }

                var viewport = new SDL.GPUViewport { X = 0, Y = 0, W = viewportW, H = viewportH, MinDepth = 0f, MaxDepth = 1f };
                SDL.SetGPUViewport(renderPass, in viewport);

                if (this._spriteCount > 0)
                {
                    SDL.BindGPUVertexBuffers(renderPass, 0, this._vertexBinding, 1);
                    var indexBinding = new SDL.GPUBufferBinding { Buffer = this._indexBuffer, Offset = 0 };
                    SDL.BindGPUIndexBuffer(renderPass, in indexBinding, SDL.GPUIndexElementSize.IndexElementSize16Bit);
                }

                boundPipeline = nint.Zero;
                boundTexture = nint.Zero;
                boundSampler = nint.Zero;
                boundScissor = null;
            }

            if ((segment.SpriteCount == 0) || (segment.Pipeline == nint.Zero))
            {
                continue;
            }

            if (segment.Pipeline != boundPipeline)
            {
                SDL.BindGPUGraphicsPipeline(renderPass, segment.Pipeline);
                boundPipeline = segment.Pipeline;
            }

            // The fragment values were captured per segment
            if (segment.UniformLength > 0)
            {
                fixed (byte* uniformPtr = &this._uniformData[segment.UniformOffset])
                {
                    SDL.PushGPUFragmentUniformData(commandBuffer, 0, (nint)uniformPtr, (uint)segment.UniformLength);
                }
            }

            if (segment.Scissor != boundScissor)
            {
                var rect = ToScissor(segment.Scissor, displayScale, viewportW, viewportH);
                SDL.SetGPUScissor(renderPass, in rect);
                boundScissor = segment.Scissor;
            }

            if ((segment.Texture != nint.Zero) && ((segment.Texture != boundTexture) || (segment.Sampler != boundSampler)))
            {
                this._samplerBinding[0] = new SDL.GPUTextureSamplerBinding { Texture = segment.Texture, Sampler = segment.Sampler };
                SDL.BindGPUFragmentSamplers(renderPass, 0, this._samplerBinding, 1);
                boundTexture = segment.Texture;
                boundSampler = segment.Sampler;
            }

            for (int drawn = 0; drawn < segment.SpriteCount; drawn += MaxSpritesPerDraw)
            {
                int count = Math.Min(MaxSpritesPerDraw, segment.SpriteCount - drawn);
                SDL.DrawGPUIndexedPrimitives(
                    renderPass,
                    (uint)(count * IndicesPerSprite),
                    1,
                    0,
                    (segment.FirstSprite + drawn) * VerticesPerSprite,
                    0);
                drawCalls++;
                drawnSprites += count;
            }
        }

        if (renderPass != nint.Zero)
        {
            SDL.EndGPURenderPass(renderPass);
        }

        GameHost.CountDraws(drawCalls, drawnSprites);
    }

    // Orthographic projection from Y-down logical coordinates to SDL GPU's Y-up clip space:
    // (0,0) to (-1,+1) top-left, (W,H) to (+1,-1) bottom-right. Slang's mul(v, M) reads the
    // matrix row-major, so the translation is in column 3.
    private static void WriteProjection(
        Span<byte> destination,
        float width,
        float height)
    {
        Span<float> m = stackalloc float[16]
        {
            2f / width, 0f, 0f, -1f,
            0f, -2f / height, 0f, 1f,
            0f, 0f, -1f, 0f,
            0f, 0f, 0f, 1f,
        };

        System.Runtime.InteropServices.MemoryMarshal.AsBytes(m).CopyTo(destination);
    }

    private unsafe void Upload(
        nint device,
        nint commandBuffer)
    {
        this.EnsureCapacity(device, commandBuffer);

        uint size = (uint)(this._spriteCount * VerticesPerSprite * SpriteVertex.Size);

        // cycle: true lets SDL use fresh memory while the GPU still reads the last frame's
        void* mapped = (void*)SDL.MapGPUTransferBuffer(device, this._vertexTransferBuffer, true);
        fixed (SpriteVertex* source = this._vertices)
        {
            Buffer.MemoryCopy(source, mapped, size, size);
        }

        SDL.UnmapGPUTransferBuffer(device, this._vertexTransferBuffer);

        var copyPass = SDL.BeginGPUCopyPass(commandBuffer);
        var from = new SDL.GPUTransferBufferLocation { TransferBuffer = this._vertexTransferBuffer, Offset = 0 };
        var to = new SDL.GPUBufferRegion { Buffer = this._vertexBuffer, Offset = 0, Size = size };
        SDL.UploadToGPUBuffer(copyPass, in from, in to, true);
        SDL.EndGPUCopyPass(copyPass);
    }

    private unsafe void EnsureCapacity(
        nint device,
        nint commandBuffer)
    {
        if (this._spriteCount > this._vertexBufferCapacity)
        {
            this.ReleaseVertexBuffers(device);

            int capacity = Math.Max(InitialCapacity, this._vertexBufferCapacity);
            while (capacity < this._spriteCount)
            {
                capacity *= 2;
            }

            uint size = (uint)(capacity * VerticesPerSprite * SpriteVertex.Size);
            var transferInfo = new SDL.GPUTransferBufferCreateInfo { Usage = SDL.GPUTransferBufferUsage.Upload, Size = size };
            this._vertexTransferBuffer = SDL.CreateGPUTransferBuffer(device, in transferInfo);
            var bufferInfo = new SDL.GPUBufferCreateInfo { Usage = SDL.GPUBufferUsageFlags.Vertex, Size = size };
            this._vertexBuffer = SDL.CreateGPUBuffer(device, in bufferInfo);
            this._vertexBufferCapacity = capacity;
        }

        // The index pattern repeats, so the buffer only needs to cover the longest draw
        int indexSprites = Math.Min(this._vertexBufferCapacity, MaxSpritesPerDraw);
        if (indexSprites > this._indexBufferCapacity)
        {
            if (this._indexBuffer != nint.Zero)
            {
                SDL.ReleaseGPUBuffer(device, this._indexBuffer);
            }

            uint size = (uint)(indexSprites * IndicesPerSprite * sizeof(ushort));
            var bufferInfo = new SDL.GPUBufferCreateInfo { Usage = SDL.GPUBufferUsageFlags.Index, Size = size };
            this._indexBuffer = SDL.CreateGPUBuffer(device, in bufferInfo);

            var transferInfo = new SDL.GPUTransferBufferCreateInfo { Usage = SDL.GPUTransferBufferUsage.Upload, Size = size };
            var transfer = SDL.CreateGPUTransferBuffer(device, in transferInfo);
            var indices = new Span<ushort>((void*)SDL.MapGPUTransferBuffer(device, transfer, false), indexSprites * IndicesPerSprite);
            for (int i = 0; i < indexSprites; i++)
            {
                int vertex = i * VerticesPerSprite;
                int index = i * IndicesPerSprite;
                indices[index + 0] = (ushort)(vertex + 0);
                indices[index + 1] = (ushort)(vertex + 1);
                indices[index + 2] = (ushort)(vertex + 2);
                indices[index + 3] = (ushort)(vertex + 0);
                indices[index + 4] = (ushort)(vertex + 2);
                indices[index + 5] = (ushort)(vertex + 3);
            }

            SDL.UnmapGPUTransferBuffer(device, transfer);

            var copyPass = SDL.BeginGPUCopyPass(commandBuffer);
            var from = new SDL.GPUTransferBufferLocation { TransferBuffer = transfer, Offset = 0 };
            var to = new SDL.GPUBufferRegion { Buffer = this._indexBuffer, Offset = 0, Size = size };
            SDL.UploadToGPUBuffer(copyPass, in from, in to, false);
            SDL.EndGPUCopyPass(copyPass);

            // Released once the command buffer that uses it completes
            SDL.ReleaseGPUTransferBuffer(device, transfer);
            this._indexBufferCapacity = indexSprites;
        }
    }

    private void ReleaseVertexBuffers(
        nint device)
    {
        if (this._vertexBuffer != nint.Zero)
        {
            SDL.ReleaseGPUBuffer(device, this._vertexBuffer);
            this._vertexBuffer = nint.Zero;
        }

        if (this._vertexTransferBuffer != nint.Zero)
        {
            SDL.ReleaseGPUTransferBuffer(device, this._vertexTransferBuffer);
            this._vertexTransferBuffer = nint.Zero;
        }

        this._vertexBufferCapacity = 0;
    }
}
