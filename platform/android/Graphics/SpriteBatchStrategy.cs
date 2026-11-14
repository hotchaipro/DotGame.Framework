// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Android.Opengl;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Common.Graphics;
using DotGame.Framework.Platform.Graphics;
using static DotGame.Framework.Android.Graphics.GLBindings;

namespace DotGame.Framework.Android.Graphics;

internal sealed class SpriteBatchStrategy : ISpriteBatchStrategy, IDisposable
{
    private const int VerticesPerSprite = 4;
    private const int IndicesPerSprite = 6;

    // Maximum sprites per frame. With 16-bit indices, max vertex index
    // is 65535, so max sprites = 65536 / 4 = 16384. The vertex buffer
    // and index buffer are both sized to this so the entire frame can
    // be uploaded in a single glBufferData call and any segment is
    // drawn with one glDrawElements call at an IBO offset.
    private const int MaxSpritesPerFrame = 16384;

    // Initial capacity of the per-frame vertex buffer (in sprites). The
    // buffer grows on demand up to MaxSpritesPerFrame.
    private const int InitialVertexCapacitySprites = 4096;

    // The GL state every batch draws with (the bound program, each
    // program's projection, the index buffer and vertex setup) belongs to
    // the context, so the service tracks it; a batch holds only its sprites.
    private readonly GraphicsServiceStrategy _graphicsService;
    private bool _isDrawing;
    private bool _disposed;

    // Single-pass vertex buffer: corners are transformed once in Draw() and
    // written here directly (no intermediate per-sprite matrix replay).
    // Submit() uploads it with glBufferData, which gives the driver a new
    // buffer to copy into, so nothing waits on a frame the GPU is still using.
    //
    // NOTE: Measured on an Adreno 615 (Pixel 3a), CPU time in Submit for 5,000
    // sprites in 10 and 100 draws: this 0.6 and 0.9 ms, a mapped ring buffer 3.2
    // and 3.4 ms, client-side vertex arrays 3.5 and 12.8 ms. A driver has to copy
    // client arrays during each draw call, so their cost grows with the draws.
    // Grows on demand if a frame exceeds the initial capacity.
    private SpriteVertex[] _vertexBuffer = new SpriteVertex[VerticesPerSprite * InitialVertexCapacitySprites];

    // Per-state-change draw segment recorded as sprites are emitted.
    // At submit time we replay segments in order, issuing only the GL
    // state changes that actually differ between segments.
    private struct DrawSegment
    {
        public int TextureId;
        public int ProgramId;
        public int StartSprite;
        public int SpriteCount;
        public int RenderState;
    }

    private DrawSegment[] _segments = new DrawSegment[64];
    private int _segmentCount;

    // Index into _segments of the segment currently accumulating
    // sprites. -1 when a new segment must be opened on the next sprite
    // (i.e. just after a state change or after Submit).
    private int _currentSegmentIndex;

    private int _spriteCount;
    private Matrix4x4 _batchTransform;
    private ShaderStrategy? _batchShader;
    private int _batchRenderState;

    // True while sprites have been accumulated but not yet submitted to
    // the GPU. End() leaves this set when it returns without flushing,
    // so the next Begin() can either keep accumulating into the same
    // VBO (when shader/transform are unchanged) or flush before
    // switching state. This is the key to collapsing the engine's
    // many Begin/End pairs per frame (each DrawContext
    // BeginEffect/EndEffect/BeginTransform/EndTransform pair forces a
    // one batch ending and the next beginning) into a small number of GL draws.
    private bool _hasPendingSprites;

    internal SpriteBatchStrategy(
        GraphicsServiceStrategy graphicsService)
    {
        ArgumentNullException.ThrowIfNull(graphicsService);

        this._graphicsService = graphicsService;
        this._currentSegmentIndex = -1;
    }

    /// <summary>
    /// Fills the bound element array buffer with the indices every batch
    /// draws with. Called once per context by the service.
    /// </summary>
    internal static unsafe void FillIndexBuffer()
    {
        // Build indices for every possible sprite in a frame so any
        // draw segment can be issued by passing an offset into the IBO
        // with no further index-buffer updates.
        var indices = new short[MaxSpritesPerFrame * IndicesPerSprite];
        for (int i = 0; i < MaxSpritesPerFrame; i++)
        {
            int vi = i * VerticesPerSprite;
            int ii = i * IndicesPerSprite;
            indices[ii + 0] = (short)(vi + 0);
            indices[ii + 1] = (short)(vi + 1);
            indices[ii + 2] = (short)(vi + 2);
            indices[ii + 3] = (short)(vi + 0);
            indices[ii + 4] = (short)(vi + 2);
            indices[ii + 5] = (short)(vi + 3);
        }

        fixed (short* ptr = indices)
        {
            glBufferData(GLES20.GlElementArrayBuffer, (nint)(indices.Length * sizeof(short)), (nint)ptr, GLES20.GlStaticDraw);
        }
    }

    /// <summary>
    /// Describes a <see cref="SpriteVertex"/> to the bound array buffer. Called once per context by
    /// the service.
    /// </summary>
    internal static void SetVertexLayout()
    {
        glVertexAttribPointer(0, 2, GLES20.GlFloat, 0, SpriteVertex.Size, 0);
        glVertexAttribPointer(1, 4, GLES20.GlUnsignedByte, 1, SpriteVertex.Size, SpriteVertex.ColorOffset);
        glVertexAttribPointer(2, 2, GLES20.GlFloat, 0, SpriteVertex.Size, SpriteVertex.TextureCoordinateOffset);
        glVertexAttribPointer(3, 4, GLES30.GlHalfFloat, 0, SpriteVertex.Size, SpriteVertex.CustomDataOffset);
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
        this.SetShader(null);

        // NOTE: A new segment for a change here, so Draw has nothing more to compare
        int renderState = GraphicsServiceStrategy.RenderState(blend, sampler);
        if (renderState != this._batchRenderState)
        {
            this._batchRenderState = renderState;
            this._currentSegmentIndex = -1;
        }
        this._isDrawing = true;

        // The transform is baked into vertex positions in Draw, so a
        // transform change between Begin/End cycles does NOT require a
        // new draw segment — only a shader change does. The next Draw
        // will compare its shader/texture against the current segment
        // and open a new one only if they differ. This collapses the
        // engine's many Begin/End pairs (each DrawContext
        // BeginEffect/EndEffect/BeginTransform/EndTransform forces a
        // one batch ending and the next beginning) into a tiny number of segments
        // per frame.

        // The vertex setup, sticky across batches, is configured once per
        // context.
        this._graphicsService.EnsureSpriteState();

        // Register as the active batch so the service can submit our
        // recorded segments before any external GL state change (render
        // target, clear, scissor) and at end-of-frame before swap.
        this._graphicsService.SetActiveBatch(this);
    }

    public void SetShader(
        Strategy<IShaderStrategy>? shaderStrategy)
    {
        this._batchShader = shaderStrategy?.ToConcrete<ShaderStrategy>() ?? this._graphicsService.DefaultShader;
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
        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before Draw().");
        }

        // textureStrategy is never null on the framework path; the
        // public SpriteBatch.Draw overloads already null-check the
        // Texture wrapper. Skipping the redundant check trims one
        // method call per draw on the hot path.
        var glTexture = textureStrategy.ToConcrete<TextureStrategy>();
        int textureId = glTexture.TextureId;
        var textureSize = glTexture.Size;
        int programId = this._batchShader!.ProgramId;

        ref var first = ref this.ReserveSprite(textureId, programId);

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

        this.CommitSprite();
    }

    public void DrawTriangle(
        Strategy<ITextureStrategy> textureStrategy,
        in Vertex a,
        in Vertex b,
        in Vertex c)
    {
        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before DrawTriangle().");
        }

        int textureId = textureStrategy.ToConcrete<TextureStrategy>().TextureId;
        ref var first = ref this.ReserveSprite(textureId, this._batchShader!.ProgramId);
        SpriteVertex.WriteTriangle(ref first, in a, in b, in c, in this._batchTransform);
        this.CommitSprite();
    }

    // Opens a segment if this can't join the last, and returns where the next sprite's vertices go
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref SpriteVertex ReserveSprite(
        int textureId,
        int programId)
    {
        // Open a new segment when shader or texture differs from the
        // segment currently being appended to, or when no segment is
        // open yet (start of frame, or just after Submit). No GL calls
        // are issued here — segments are replayed in bulk by Submit().
        int segIndex = this._currentSegmentIndex;
        if (segIndex < 0)
        {
            this.OpenSegment(textureId, programId);
        }
        else
        {
            ref var curSeg = ref this._segments[segIndex];
            if (curSeg.TextureId != textureId || curSeg.ProgramId != programId)
            {
                this.OpenSegment(textureId, programId);
            }
        }

        // If the frame's vertex buffer is full, submit what we have and
        // start a new frame. Sprites within a single frame normally fit
        // (initial capacity 4096; observed worst-case ~800), so this is
        // a safety net rather than a hot path.
        if (this._spriteCount >= MaxSpritesPerFrame)
        {
            this.Submit();
            this.OpenSegment(textureId, programId);
        }
        else if ((this._spriteCount * VerticesPerSprite) + VerticesPerSprite > this._vertexBuffer.Length)
        {
            // Grow the managed vertex buffer (doubling, capped at the
            // index-buffer limit). With client-side arrays there is no
            // VBO to resize — Submit() pins whatever the current array
            // reference is.
            int newSpriteCapacity = Math.Min(MaxSpritesPerFrame, (this._vertexBuffer.Length / VerticesPerSprite) * 2);
            Array.Resize(ref this._vertexBuffer, newSpriteCapacity * VerticesPerSprite);
        }

        // Written through a ref so the JIT elides the array bounds checks
        return ref Unsafe.Add(
            ref MemoryMarshal.GetArrayDataReference(this._vertexBuffer),
            this._spriteCount * VerticesPerSprite);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitSprite()
    {
        this._spriteCount++;
        // ref local avoids a redundant array bounds check on the hot path.
        ref var segSpriteCount = ref this._segments[this._currentSegmentIndex].SpriteCount;
        segSpriteCount++;
        this._hasPendingSprites = true;
    }

    // Records a new draw segment with the given GL state. Called from
    // Draw when texture or shader changes. No GL calls — segments are
    // replayed in bulk by Submit().
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenSegment(int textureId, int programId)
    {
        if (this._segmentCount == this._segments.Length)
        {
            Array.Resize(ref this._segments, this._segments.Length * 2);
        }

        this._segments[this._segmentCount] = new DrawSegment
        {
            TextureId = textureId,
            ProgramId = programId,
            StartSprite = this._spriteCount,
            SpriteCount = 0,
            RenderState = this._batchRenderState,
        };
        this._currentSegmentIndex = this._segmentCount;
        this._segmentCount++;
    }

    public void End()
    {
        ObjectDisposedException.ThrowIf(this._disposed, nameof(SpriteBatchStrategy));

        if (!this._isDrawing)
        {
            throw new InvalidOperationException("SpriteBatch.Begin() must be called before End().");
        }

        // Intentionally do NOT submit here. Recorded segments stay
        // queued until either (a) the service submits us before
        // changing GL state (render target, clear, scissor), or (b)
        // end-of-frame FlushPending() is called by the engine.

        this._isDrawing = false;
    }

    /// <summary>
    /// Submits any pending sprites to the GPU. Called by the service
    /// before mutating GL state (render target, clear, scissor) and at
    /// end-of-frame before the GL surface swaps buffers. Safe to call
    /// even when no sprites are pending.
    /// </summary>
    internal void FlushPending()
    {
        if (this._hasPendingSprites)
        {
            this.Submit();
        }
    }

    private unsafe void Submit()
    {
        int spriteCount = this._spriteCount;
        if (spriteCount == 0)
        {
            this._hasPendingSprites = false;
            this._segmentCount = 0;
            this._currentSegmentIndex = -1;
            return;
        }

        fixed (SpriteVertex* ptr = this._vertexBuffer)
        {
            glBufferData(GLES20.GlArrayBuffer, spriteCount * VerticesPerSprite * SpriteVertex.Size, (nint)ptr, GLES20.GlDynamicDraw);
        }

        // Replay segments, issuing only the GL state changes that
        // actually differ between consecutive segments. The service
        // skips a program bind or projection upload that's already
        // current in the context.
        int lastTextureId = 0;
        int lastProgramId = 0;
        int drawCalls = 0;
        int drawnSprites = 0;

        for (int i = 0; i < this._segmentCount; i++)
        {
            ref var seg = ref this._segments[i];
            if (seg.SpriteCount == 0)
            {
                continue;
            }

            this._graphicsService.ApplyRenderState(seg.RenderState);

            if (seg.ProgramId != lastProgramId)
            {
                this._graphicsService.UseSpriteProgram(seg.ProgramId);
                lastProgramId = seg.ProgramId;
            }

            if (seg.TextureId != lastTextureId)
            {
                glBindTexture(GLES20.GlTexture2d, seg.TextureId);
                lastTextureId = seg.TextureId;
            }

            // Offset into the IBO in bytes: each sprite occupies
            // IndicesPerSprite shorts
            nint indexOffset = (nint)(seg.StartSprite * IndicesPerSprite * sizeof(short));
            glDrawElements(GLES20.GlTriangles, seg.SpriteCount * IndicesPerSprite, GLES20.GlUnsignedShort, indexOffset);
            drawCalls++;
            drawnSprites += seg.SpriteCount;
        }

        GameHost.CountDraws(drawCalls, drawnSprites);

        this._spriteCount = 0;
        this._segmentCount = 0;
        this._currentSegmentIndex = -1;
        this._hasPendingSprites = false;
    }

    public void Dispose()
    {
        this._disposed = true;
    }
}
