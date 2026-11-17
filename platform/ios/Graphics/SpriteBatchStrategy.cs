// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Common.Graphics;
using DotGame.Framework.Platform.Graphics;
using Metal;

namespace DotGame.Framework.iOS.Graphics;

internal sealed class SpriteBatchStrategy : ISpriteBatchStrategy, IDisposable
{
    private const int VerticesPerSprite = 4;
    private const int BytesPerSprite = VerticesPerSprite * SpriteVertex.Size;
    internal const int IndicesPerSprite = 6;
    internal const int MaxSpritesPerBatch = 2048;
    // Each ring slot must be large enough to hold all sub-allocations made
    // within a single frame (one region per Flush). Sized for up to 8
    // max-size batches per slot before having to rotate to the next slot.
    private const int BatchesPerRingSlot = 8;
    private const int MaxSpritesPerRingSlot = MaxSpritesPerBatch * BatchesPerRingSlot;
    private const int RingSlotByteSize = MaxSpritesPerRingSlot * BytesPerSprite;
    private const int InitialVertexBufferSlots = 1;

    private readonly GraphicsServiceStrategy _graphicsService;
    private bool _isDrawing;
    private bool _disposed;
    private int _spriteCount;
    private Matrix4x4 _batchTransform;
    // The shader of the batch being drawn, and the shader and texture of the sprites waiting to
    // be drawn: a batch that changes neither continues the waiting sprites
    private ShaderStrategy? _batchShader;
    private BlendMode _blend;
    private Sampler _sampler;
    private ShaderStrategy? _currentShader;
    private IMTLTexture? _currentTexture;

    // A ring of persistent shared-storage vertex buffers, three to start.
    // Each slot is large enough to hold many flushes' worth of vertices;
    // within a slot we sub-allocate per Flush via _activeSlotByteOffset so
    // that earlier draws recorded in the current command buffer keep
    // pointing at their own data. The slot itself only rotates when it
    // fills up. A slot is reused only once the GPU has finished every
    // frame that read from it; if it hasn't (a frame drawing more than the
    // ring holds, or the GPU running behind), a slot is added instead.
    private IMTLBuffer[] _vertexBufferRing;
    private nint[] _vertexBufferContents;
    private long[] _vertexBufferLastFrame;
    private int _vertexBufferRingIndex;
    private int _activeSlotByteOffset;
    private unsafe SpriteVertex* _activeVertexPtr;

    internal SpriteBatchStrategy(
        GraphicsServiceStrategy graphicsService)
    {
        this._graphicsService = graphicsService ?? throw new ArgumentNullException(nameof(graphicsService));

        // Create the vertex buffer ring
        this._vertexBufferRing = new IMTLBuffer[InitialVertexBufferSlots];
        this._vertexBufferContents = new nint[InitialVertexBufferSlots];
        this._vertexBufferLastFrame = new long[InitialVertexBufferSlots];
        for (int i = 0; i < InitialVertexBufferSlots; i++)
        {
            var buf = this.CreateRingSlot();
            this._vertexBufferRing[i] = buf;
            this._vertexBufferContents[i] = buf.Contents;
        }

        this._activeSlotByteOffset = 0;
        unsafe
        {
            this._activeVertexPtr = (SpriteVertex*)this._vertexBufferContents[0];
        }
    }

    ~SpriteBatchStrategy()
    {
        this.Dispose(false);
    }

    private IMTLBuffer CreateRingSlot() =>
        this._graphicsService.Device.CreateBuffer(
            (nuint)RingSlotByteSize,
            MTLResourceOptions.StorageModeShared)
            ?? throw new InvalidOperationException("Failed to create Metal vertex ring buffer.");

    /// <summary>
    /// Moves to the next ring slot, adding one before it when the GPU may still be reading it.
    /// </summary>
    private void RotateRingSlot()
    {
        int next = (this._vertexBufferRingIndex + 1) % this._vertexBufferRing.Length;
        if (this._vertexBufferLastFrame[next] > this._graphicsService.CompletedFrameCount)
        {
            // Inserted at the next position, so the slots after it keep their order and the
            // oldest is still the next to be reused
            int count = this._vertexBufferRing.Length;
            var ring = new IMTLBuffer[count + 1];
            var contents = new nint[count + 1];
            var lastFrame = new long[count + 1];
            Array.Copy(this._vertexBufferRing, ring, next);
            Array.Copy(this._vertexBufferContents, contents, next);
            Array.Copy(this._vertexBufferLastFrame, lastFrame, next);
            Array.Copy(this._vertexBufferRing, next, ring, next + 1, count - next);
            Array.Copy(this._vertexBufferContents, next, contents, next + 1, count - next);
            Array.Copy(this._vertexBufferLastFrame, next, lastFrame, next + 1, count - next);

            var slot = this.CreateRingSlot();
            ring[next] = slot;
            contents[next] = slot.Contents;

            this._vertexBufferRing = ring;
            this._vertexBufferContents = contents;
            this._vertexBufferLastFrame = lastFrame;
        }

        this._vertexBufferRingIndex = next;
        this._activeSlotByteOffset = 0;
    }

    public void Begin(
        Matrix4x4? transformMatrix = null,
        BlendMode blend = BlendMode.Alpha,
        Sampler sampler = Sampler.LinearClamp)
    {
        ObjectDisposedException.ThrowIf(this._disposed, nameof(SpriteBatchStrategy));

        if (this._isDrawing)
        {
            throw new InvalidOperationException("The scope from SpriteBatch.Begin() must end before calling Begin() again.");
        }

        this._batchTransform = transformMatrix ?? Matrix4x4.Identity;

        this._batchShader = null;

        // NOTE: Draws what's waiting here, so Draw has nothing more to compare
        if ((blend != this._blend) || (sampler != this._sampler))
        {
            this.FlushPending();
            this._blend = blend;
            this._sampler = sampler;
        }

        this._isDrawing = true;

        // Sprites still waiting from an earlier batch stay, to be continued or drawn
        this._graphicsService.SetActiveBatch(this);
    }

    public void SetShader(
        Strategy<IShaderStrategy>? shaderStrategy)
    {
        this._batchShader = shaderStrategy?.ToConcrete<ShaderStrategy>();
    }

    public unsafe void Draw(
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
        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before Draw().");
        }

        ArgumentNullException.ThrowIfNull(textureStrategy);

        var metalTexture = textureStrategy.ToConcrete<TextureStrategy>();
        var textureSize = metalTexture.Size;
        var nativeTexture = metalTexture.NativeTexture;

        ref var first = ref this.ReserveSprite(nativeTexture);

        SpriteVertex.WriteSprite(
            ref first,
            textureSize,
            position,
            color,
            rotation,
            origin,
            scale,
            sourceRectangle,
            flip,
            in this._batchTransform,
            in customData);

        this._spriteCount++;
    }

    public void DrawTriangle(
        Strategy<ITextureStrategy> textureStrategy,
        in Vertex a,
        in Vertex b,
        in Vertex c,
        in ShaderCustomData customData)
    {
        ObjectDisposedException.ThrowIf(this._disposed, nameof(SpriteBatchStrategy));

        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before DrawTriangle().");
        }

        ref var first = ref this.ReserveSprite(textureStrategy.ToConcrete<TextureStrategy>().NativeTexture);
        SpriteVertex.WriteTriangle(ref first, in a, in b, in c, in this._batchTransform, in customData);
        this._spriteCount++;
    }

    // Flushes what can't share a draw with this, and returns where the next sprite's vertices go
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe ref SpriteVertex ReserveSprite(
        IMTLTexture nativeTexture)
    {
        // Flush on a texture or shader change
        if ((this._spriteCount > 0)
            && (!ReferenceEquals(this._currentTexture, nativeTexture) || !ReferenceEquals(this._currentShader, this._batchShader)))
        {
            this.Flush();
        }
        this._currentTexture = nativeTexture;
        this._currentShader = this._batchShader;

        // Auto-flush when we hit the batch limit; capacity is fixed at
        // MaxSpritesPerBatch because we write directly into the active
        // ring buffer's shared-memory backing store.
        if (this._spriteCount >= MaxSpritesPerBatch)
        {
            this.Flush();
        }

        return ref this._activeVertexPtr[this._spriteCount * VerticesPerSprite];
    }

    public void End()
    {
        ObjectDisposedException.ThrowIf(this._disposed, nameof(SpriteBatchStrategy));

        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before End().");
        }

        // Not drawn here: a later batch with the same shader and texture continues them, and
        // the service draws them before anything changes the encoder's state
        this._isDrawing = false;
    }

    internal void FlushPendingIfUsing(
        IMTLTexture texture)
    {
        if (ReferenceEquals(this._currentTexture, texture))
        {
            this.FlushPending();
        }
    }

    /// <summary>
    /// Draws the sprites waiting to be drawn, if any.
    /// </summary>
    internal void FlushPending()
    {
        if (!this._disposed && (this._spriteCount > 0))
        {
            this.Flush();
        }
    }

    private unsafe void Flush()
    {
        int spriteCount = this._spriteCount;
        if (spriteCount == 0)
        {
            return;
        }

        var encoder = this._graphicsService.GetOrBeginRenderEncoder();
        if (encoder is null)
        {
            this._spriteCount = 0;
            return;
        }

        var service = this._graphicsService;
        var shader = this._currentShader ?? service.DefaultShader;

        // Only the state that differs from what the encoder has bound is set
        service.BindPipeline(encoder, shader.GetPipeline(this._blend));

        // Bind the active ring buffer directly — no allocation, no copy.
        // The shared-storage buffer was written in-place by Draw() via the
        // Contents pointer. Each Flush uses its own sub-region of the slot
        // (offset = _activeSlotByteOffset) so previously recorded draws in
        // the same command buffer continue to reference valid data.
        service.BindVertexBuffer(
            encoder,
            this._vertexBufferRing[this._vertexBufferRingIndex],
            (nuint)this._activeSlotByteOffset);
        this._vertexBufferLastFrame[this._vertexBufferRingIndex] = service.FrameNumber;

        // Projection matrix as vertex uniform (buffer index 0, matching shader's [[buffer(0)]])
        service.BindProjection(encoder);

        // Set fragment shader uniforms if custom shader has parameters
        if ((this._currentShader is { } currentShader) && !currentShader.FragmentUniforms.IsEmpty)
        {
            service.BindFragmentUniforms(encoder, currentShader.FragmentUniforms);
        }

        if (this._currentTexture is not null)
        {
            service.BindSampler(encoder, this._sampler);
            service.BindTexture(encoder, this._currentTexture);
        }

        // Draw indexed triangles
        encoder.DrawIndexedPrimitives(
            MTLPrimitiveType.Triangle,
            (nuint)(spriteCount * IndicesPerSprite),
            MTLIndexType.UInt16,
            service.SpriteIndexBuffer,
            0);
        GameHost.CountDraws(1, spriteCount);

        // Advance the write offset within the current ring slot so the
        // next Flush sub-allocates a fresh region. If the next max-size
        // batch would not fit, rotate to the next ring slot and reset.
        int nextOffset = this._activeSlotByteOffset + (spriteCount * BytesPerSprite);
        if (nextOffset + (MaxSpritesPerBatch * BytesPerSprite) > RingSlotByteSize)
        {
            this.RotateRingSlot();
        }
        else
        {
            this._activeSlotByteOffset = nextOffset;
        }
        this._activeVertexPtr = (SpriteVertex*)(this._vertexBufferContents[this._vertexBufferRingIndex] + this._activeSlotByteOffset);
        this._spriteCount = 0;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this.Dispose(true);
    }

    private void Dispose(
        bool isDisposing)
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;

        if (isDisposing)
        {
            this._graphicsService.ReleaseActiveBatch(this);

            for (int i = 0; i < this._vertexBufferRing.Length; i++)
            {
                this._vertexBufferRing[i]?.Dispose();
            }
        }
    }
}
