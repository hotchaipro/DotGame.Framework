// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.Opengl;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Graphics;
using static DotGame.Framework.Android.Graphics.GLBindings;

namespace DotGame.Framework.Android.Graphics;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The graphics service lives for the lifetime of the app.")]
internal sealed class GraphicsServiceStrategy : IGraphicsServiceStrategy
{
    private readonly AndroidGameEngine _engine;
    private bool _isDeviceLost;
    private ShaderStrategy? _defaultShader;
    private readonly List<WeakReference<Strategy<ISpriteBatchStrategy>>> _trackedSpriteBatches = new();
    private readonly List<WeakReference<Strategy<ITextureStrategy>>> _trackedRenderTextures = new();

    // Currently-open sprite batch with deferred End() pending sprites.
    // External GL state changes (render target, clear, scissor) must
    // flush these sprites before mutating GL state, otherwise the
    // pending sprites would render against the wrong target/clip.
    private SpriteBatchStrategy? _activeBatch;

    internal void SetActiveBatch(SpriteBatchStrategy? batch)
    {
        if (!ReferenceEquals(this._activeBatch, batch))
        {
            // A different batch is taking over — flush the previous one
            // so its sprites are submitted with its own GL state.
            this._activeBatch?.FlushPending();
            this._activeBatch = batch;
        }
    }

    /// <summary>
    /// Flushes any sprites accumulated by the active sprite batch.
    /// Called once per frame (before the GL context swaps buffers) to
    /// ensure the final batch from the frame actually reaches the GPU.
    /// </summary>
    internal void FlushPendingSprites()
    {
        this._activeBatch?.FlushPending();
    }

    // GL state belongs to the context, not to a sprite batch: a game can
    // have several batches (the engine alone has two), and each binds
    // programs and uploads projections that the others then see. So the
    // bound program, each program's uploaded projection, and the sprite
    // vertex setup are tracked here, once per context.
    private int _boundProgramId;
    private readonly Dictionary<int, ProgramState> _programStates = new();
    private bool _spriteStateConfigured;
    private int _spriteIndexBuffer;

    // The MatrixTransform location of a program, and the projection last
    // uploaded to it (uniforms persist on the program object)
    private sealed class ProgramState
    {
        internal int MatrixTransformLocation;
        internal Vector2Int ProjectionSize;
        internal bool ProjectionIsRenderTexture;
    }

    /// <summary>
    /// Binds a program, skipping the GL call when it's already bound.
    /// Everything that binds a program goes through here.
    /// </summary>
    internal void UseProgram(
        int programId)
    {
        if (programId != this._boundProgramId)
        {
            glUseProgram(programId);
            this._boundProgramId = programId;
        }
    }

    /// <summary>
    /// Binds a sprite program and uploads the current render target's
    /// projection to it, unless the program already has that projection.
    /// </summary>
    internal void UseSpriteProgram(
        int programId)
    {
        this.UseProgram(programId);

        if (!this._programStates.TryGetValue(programId, out var state))
        {
            state = new ProgramState
            {
                MatrixTransformLocation = GLES20.GlGetUniformLocation(programId, "MatrixTransform"),
            };
            this._programStates.Add(programId, state);
        }

        var size = this.RenderTargetSize;
        bool isRenderTexture = this.IsRenderTextureBound;
        if ((size != state.ProjectionSize) || (isRenderTexture != state.ProjectionIsRenderTexture))
        {
            this.ApplyProjectionMatrix(state.MatrixTransformLocation);
            state.ProjectionSize = size;
            state.ProjectionIsRenderTexture = isRenderTexture;
        }
    }

    /// <summary>
    /// Forgets a deleted program, whose name GL may give to a new one.
    /// </summary>
    internal void ForgetProgram(
        int programId)
    {
        this._programStates.Remove(programId);
        if (programId == this._boundProgramId)
        {
            this._boundProgramId = 0;
        }
    }

    /// <summary>
    /// Sets up the vertex state every sprite batch draws with, once per
    /// context: blending, the vertex buffer and its attributes, and one index
    /// buffer, all shared by every batch.
    /// </summary>
    internal void EnsureSpriteState()
    {
        if (this._spriteStateConfigured)
        {
            return;
        }

        glEnable(GLES20.GlBlend);
        glBlendFunc(GLES20.GlOne, GLES20.GlOneMinusSrcAlpha);

        int[] buffers = new int[2];
        GLES20.GlGenBuffers(2, buffers, 0);
        this._spriteIndexBuffer = buffers[0];
        glBindBuffer(GLES20.GlElementArrayBuffer, this._spriteIndexBuffer);
        SpriteBatchStrategy.FillIndexBuffer();

        // Batches fill the buffer as they submit; the layout within it never changes
        glBindBuffer(GLES20.GlArrayBuffer, buffers[1]);
        SpriteBatchStrategy.SetVertexLayout();

        glEnableVertexAttribArray(0);
        glEnableVertexAttribArray(1);
        glEnableVertexAttribArray(2);
        glEnableVertexAttribArray(3);

        glActiveTexture(GLES20.GlTexture0);

        this._spriteStateConfigured = true;
    }

    // The blend mode and sampler in force, and the sampler objects, which are made when first used.
    // Sampler 0 is none: textures are made for LinearClamp, the usual case.
    private int _renderState;
    private readonly int[] _samplerObjects = new int[4];

    internal static int RenderState(
        BlendMode blend,
        Sampler sampler)
    {
        return (int)blend | ((int)sampler << 4);
    }

    /// <summary>
    /// Puts a draw's blend mode and sampler in force, unless they already are.
    /// </summary>
    internal void ApplyRenderState(
        int state)
    {
        if (state == this._renderState)
        {
            return;
        }

        int changed = state ^ this._renderState;
        this._renderState = state;

        if ((changed & 0xF) != 0)
        {
            switch ((BlendMode)(state & 0xF))
            {
                case BlendMode.Additive:
                    GLES20.GlBlendFuncSeparate(GLES20.GlOne, GLES20.GlOne, GLES20.GlOne, GLES20.GlOne);
                    break;
                case BlendMode.Multiply:
                    GLES20.GlBlendFuncSeparate(GLES20.GlDstColor, GLES20.GlOneMinusSrcAlpha, GLES20.GlOne, GLES20.GlOneMinusSrcAlpha);
                    break;
                case BlendMode.Screen:
                    GLES20.GlBlendFuncSeparate(GLES20.GlOne, GLES20.GlOneMinusSrcColor, GLES20.GlOne, GLES20.GlOneMinusSrcAlpha);
                    break;
                case BlendMode.Opaque:
                    GLES20.GlBlendFuncSeparate(GLES20.GlOne, GLES20.GlZero, GLES20.GlOne, GLES20.GlZero);
                    break;
                default:
                    GLES20.GlBlendFuncSeparate(GLES20.GlOne, GLES20.GlOneMinusSrcAlpha, GLES20.GlOne, GLES20.GlOneMinusSrcAlpha);
                    break;
            }
        }

        if ((changed >> 4) != 0)
        {
            int sampler = state >> 4;
            GLES30.GlBindSampler(0, (sampler == 0) ? 0 : this.GetSamplerObject((Sampler)sampler));
        }
    }

    private int GetSamplerObject(
        Sampler sampler)
    {
        int index = (int)sampler;
        if (this._samplerObjects[index] == 0)
        {
            int[] ids = new int[1];
            GLES30.GlGenSamplers(1, ids, 0);

            bool isPoint = (sampler == Sampler.PointClamp) || (sampler == Sampler.PointRepeat);
            bool isRepeat = (sampler == Sampler.LinearRepeat) || (sampler == Sampler.PointRepeat);
            int filter = isPoint ? GLES20.GlNearest : GLES20.GlLinear;
            int wrap = isRepeat ? GLES20.GlRepeat : GLES20.GlClampToEdge;
            GLES30.GlSamplerParameteri(ids[0], GLES20.GlTextureMinFilter, filter);
            GLES30.GlSamplerParameteri(ids[0], GLES20.GlTextureMagFilter, filter);
            GLES30.GlSamplerParameteri(ids[0], GLES20.GlTextureWrapS, wrap);
            GLES30.GlSamplerParameteri(ids[0], GLES20.GlTextureWrapT, wrap);
            this._samplerObjects[index] = ids[0];
        }

        return this._samplerObjects[index];
    }

    // Tracks the size of the active render target so sprite batches can
    // compute the orthographic projection at flush time, matching the
    // SDL3/WebGL pattern (projection always reflects the current target).
    private Vector2Int _viewportSize;
    private Vector2Int? _renderTargetSize;

    // Scratch buffer reused for projection-matrix uploads.
    private readonly float[] _orthoMatrix = new float[16];

    // The render-target size the cached _orthoMatrix was built for. The
    // orthographic projection only changes when the target is resized, so
    // we rebuild the float[16] lazily instead of on every program bind.
    private int _orthoMatrixWidth = -1;
    private int _orthoMatrixHeight = -1;
    private bool _orthoMatrixIsRenderTexture;

    public int? PreferredFrameRate { get; set; }

    public int MultisampleCount => this._engine.MultisampleCount;

    internal GraphicsServiceStrategy(
        AndroidGameEngine engine)
    {
        GLResources.TextureDeleted = this.DeleteFramebuffer;
        GLResources.ProgramDeleted = this.ForgetProgram;

        this._engine = engine;
    }

    internal bool IsDeviceLost => this._isDeviceLost;

    internal ShaderStrategy DefaultShader => this._defaultShader
        ?? throw new InvalidOperationException("Default shader is not initialized.");

    public Task OnInitialize()
    {
        this.CompileDefaultShader();
        return Task.CompletedTask;
    }

    internal void HandleContextLost()
    {
        this._isDeviceLost = true;

        // The new context has none of the old one's framebuffers, and draws to its window
        this._fboMap.Clear();
        this._boundFramebuffer = 0;
        this._renderTargetSize = null;

        // All GL resources are already invalid; just clear references
        this._defaultShader = null;
        this._activeBatch = null;

        // The restored context starts with GL's defaults
        this._boundProgramId = 0;
        this._programStates.Clear();
        this._spriteStateConfigured = false;
        this._spriteIndexBuffer = 0;
        this._renderState = 0;
        Array.Clear(this._samplerObjects);
    }

    internal async Task HandleContextRestored()
    {
        // Reload all GL resources (textures, shaders)
        await this._engine.ContentService.HandleContextRestored();

        // Recompile the default shader program
        this.CompileDefaultShader();

        // Recreate tracked sprite batches (depends on default shader program)
        this.RecreateTrackedSpriteBatches();

        // Recreate tracked render textures
        this.RecreateTrackedRenderTextures();

        // All resources recreated; allow rendering to resume
        this._isDeviceLost = false;

        // The recovery isn't game time
        this._engine.Host.ResetFrameClock();
    }

    public Strategy<ISpriteBatchStrategy> CreateSpriteBatch()
    {
        var spriteBatchStrategy = this.CreateSpriteBatchStrategy();

        var strategy = new Strategy<ISpriteBatchStrategy>(spriteBatchStrategy);

        this._trackedSpriteBatches.Add(new WeakReference<Strategy<ISpriteBatchStrategy>>(strategy));

        return strategy;
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

        int textureId = CreateTextureFromData(size.X, size.Y, data);

        return new Strategy<ITextureStrategy>(new TextureStrategy(textureId, size) { Owner = this });
    }

    public Strategy<ITextureStrategy> CreateRenderTexture(
        Vector2Int size)
    {
        int textureId = CreateRenderTextureGL(size.X, size.Y);

        var strategy = new Strategy<ITextureStrategy>(new TextureStrategy(textureId, size));

        this._trackedRenderTextures.Add(new WeakReference<Strategy<ITextureStrategy>>(strategy));

        return strategy;
    }

    public void SetRenderTarget(
        Strategy<ITextureStrategy>? renderTextureStrategy)
    {
        // Flush any sprites accumulated against the previous render
        // target before binding a new framebuffer.
        this._activeBatch?.FlushPending();

        if (renderTextureStrategy is not null)
        {
            var glTexture = renderTextureStrategy.ToConcrete<TextureStrategy>();
            int textureId = glTexture.TextureId;

            // Bind to FBO associated with this render texture
            int fbo = this.GetOrCreateFramebuffer(textureId);
            glBindFramebuffer(GLES20.GlFramebuffer, fbo);
            this._boundFramebuffer = fbo;

            // Track render-target size and resize the GL viewport to match
            // so subsequent flushes project into the correct space.
            var size = glTexture.Size;
            this._renderTargetSize = size;
            glViewport(0, 0, size.X, size.Y);
        }
        else
        {
            glBindFramebuffer(GLES20.GlFramebuffer, 0);
            this._boundFramebuffer = 0;

            // Restore the swapchain viewport
            this._renderTargetSize = null;
            glViewport(0, 0, this._viewportSize.X, this._viewportSize.Y);
        }
    }

    // The framebuffer drawn to: a render texture's, or 0 for the screen
    private int _boundFramebuffer;

    public unsafe Task ReadPixelsAsync(
        Strategy<ITextureStrategy> textureStrategy,
        Memory<Color32> destination)
    {
        ArgumentNullException.ThrowIfNull(textureStrategy);

        // GL runs in order, so the pixels are those drawn so far once the batch has drawn
        this._activeBatch?.FlushPending();

        var glTexture = textureStrategy.ToConcrete<TextureStrategy>();
        var size = glTexture.Size;

        int fbo = this.GetOrCreateFramebuffer(glTexture.TextureId);
        glBindFramebuffer(GLES20.GlFramebuffer, fbo);

        var pixels = destination.Span;
        fixed (Color32* pixelsPointer = pixels)
        {
            glReadPixels(0, 0, size.X, size.Y, GLES20.GlRgba, GLES20.GlUnsignedByte, (nint)pixelsPointer);
        }

        glBindFramebuffer(GLES20.GlFramebuffer, this._boundFramebuffer);

        // GL reads the bottom row first, and render textures are drawn upside down (see
        // ApplyProjectionMatrix), so the first row read is the picture's top
        return Task.CompletedTask;
    }

    public void Clear(Color color)
    {
        // Flush so pending sprites aren't wiped by the upcoming clear.
        this._activeBatch?.FlushPending();

        glClearColor(color.R, color.G, color.B, color.A);
        glClear(GLES20.GlColorBufferBit);
    }

    public void SetScissorRectangle(
        RectangleInt? rectangle)
    {
        // Flush before changing the scissor so prior sprites are clipped
        // by the prior rectangle, not the new one.
        this._activeBatch?.FlushPending();

        if (rectangle is null)
        {
            glDisable(GLES20.GlScissorTest);
        }
        else
        {
            // The game uses a top-left origin (Y-down), but glScissor uses
            // the target's bottom-left origin. The screen is the right way
            // up, so the rectangle flips against its height; a render
            // texture is drawn upside down (see ApplyProjectionMatrix), so
            // its rows already count from the top.
            var r = rectangle.Value;
            int glY = this.IsRenderTextureBound ? r.Y : (this.RenderTargetSize.Y - r.Y - r.Height);
            glEnable(GLES20.GlScissorTest);
            glScissor(r.X, glY, r.Width, r.Height);
        }
    }

    internal void SetViewportSize(
        int width,
        int height)
    {
        this._viewportSize = new Vector2Int(width, height);

        // Only resize the live GL viewport if no render target is currently
        // bound; SetRenderTarget owns the viewport while a target is active.
        if (this._renderTargetSize is null)
        {
            glViewport(0, 0, width, height);
        }
    }

    /// <summary>
    /// Size (in pixels) of the currently-bound render target — the active
    /// render texture if one is bound, otherwise the swapchain viewport.
    /// Sprite batches read this at flush time to compute the orthographic
    /// projection so drawing is always correctly sized for the target.
    /// </summary>
    internal Vector2Int RenderTargetSize => this._renderTargetSize ?? this._viewportSize;

    /// <summary>
    /// Whether a render texture is bound, rather than the screen: its
    /// projection is upside down (see <see cref="ApplyProjectionMatrix"/>).
    /// </summary>
    internal bool IsRenderTextureBound => (this._renderTargetSize is not null);

    /// <summary>
    /// Uploads an orthographic projection matrix sized to
    /// <see cref="RenderTargetSize"/> to the bound program's
    /// <c>MatrixTransform</c> uniform. Safe to call for programs that
    /// do not declare <c>MatrixTransform</c>; pass a negative
    /// <paramref name="matrixTransformLocation"/> and the call is a no-op.
    /// </summary>
    private unsafe void ApplyProjectionMatrix(
        int matrixTransformLocation)
    {
        if (matrixTransformLocation < 0)
        {
            return;
        }

        var size = this.RenderTargetSize;
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        // Rebuild the projection only when the render target changes size or
        // kind; it is otherwise invariant across the many program binds per
        // frame.
        bool isRenderTexture = this.IsRenderTextureBound;
        if ((size.X != this._orthoMatrixWidth) || (size.Y != this._orthoMatrixHeight) || (isRenderTexture != this._orthoMatrixIsRenderTexture))
        {
            // Orthographic projection: (0,0) top-left, (size.X, size.Y)
            // bottom-right on the screen. A render texture is drawn upside
            // down, its top row first in memory, so it samples the right way
            // up when it's drawn like any other texture (texture row 0 is at
            // the top of a sprite).
            Matrix4x4 proj = isRenderTexture
                ? Matrix4x4.CreateOrthographicOffCenter(0, size.X, 0, size.Y, -1, 1)
                : Matrix4x4.CreateOrthographicOffCenter(0, size.X, size.Y, 0, -1, 1);
            this._orthoMatrix[0] = proj.M11; this._orthoMatrix[1] = proj.M12; this._orthoMatrix[2] = proj.M13; this._orthoMatrix[3] = proj.M14;
            this._orthoMatrix[4] = proj.M21; this._orthoMatrix[5] = proj.M22; this._orthoMatrix[6] = proj.M23; this._orthoMatrix[7] = proj.M24;
            this._orthoMatrix[8] = proj.M31; this._orthoMatrix[9] = proj.M32; this._orthoMatrix[10] = proj.M33; this._orthoMatrix[11] = proj.M34;
            this._orthoMatrix[12] = proj.M41; this._orthoMatrix[13] = proj.M42; this._orthoMatrix[14] = proj.M43; this._orthoMatrix[15] = proj.M44;

            this._orthoMatrixWidth = size.X;
            this._orthoMatrixHeight = size.Y;
            this._orthoMatrixIsRenderTexture = isRenderTexture;
        }

        // Pin and pass a native pointer to bypass the Mono.Android
        // GlUniformMatrix4fv binding, which otherwise marshals the
        // managed float[16] through a temporary Java float[] every call.
        fixed (float* p = this._orthoMatrix)
        {
            glUniformMatrix4fv(matrixTransformLocation, 1, 0, (nint)p);
        }
    }

    private void RecreateTrackedSpriteBatches()
    {
        for (int i = this._trackedSpriteBatches.Count - 1; i >= 0; i--)
        {
            if (this._trackedSpriteBatches[i].TryGetTarget(out var strategy))
            {
                var newStrategy = this.CreateSpriteBatchStrategy();
                strategy.Reset(newStrategy);
            }
            else
            {
                this._trackedSpriteBatches.RemoveAt(i);
            }
        }
    }

    private SpriteBatchStrategy CreateSpriteBatchStrategy()
    {
        return new SpriteBatchStrategy(this);
    }

    private void CompileDefaultShader()
    {
        int programId = CompileShaderProgram(
            DefaultShaderSource.VertexShader,
            DefaultShaderSource.FragmentShader);

        this._defaultShader = new ShaderStrategy(
            programId,
            DefaultShaderSource.VertexShader,
            DefaultShaderSource.FragmentShader,
            this);
    }

    private void RecreateTrackedRenderTextures()
    {
        for (int i = this._trackedRenderTextures.Count - 1; i >= 0; i--)
        {
            if (this._trackedRenderTextures[i].TryGetTarget(out var strategy))
            {
                strategy.ToConcrete<TextureStrategy>().Invalidate();

                var size = strategy.Interface.Size;
                int textureId = CreateRenderTextureGL(size.X, size.Y);
                strategy.Reset(new TextureStrategy(textureId, size));
            }
            else
            {
                this._trackedRenderTextures.RemoveAt(i);
            }
        }
    }

    // ──────── Static GL helpers ────────

    internal static int CompileShaderProgram(
        string vertexSource,
        string fragmentSource)
    {
        int vertexShader = LoadShader(GLES20.GlVertexShader, vertexSource);
        int fragmentShader = LoadShader(GLES20.GlFragmentShader, fragmentSource);

        int program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(program, vertexShader);
        GLES20.GlAttachShader(program, fragmentShader);
        GLES20.GlLinkProgram(program);

        int[] linkStatus = new int[1];
        GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, linkStatus, 0);
        if (linkStatus[0] == 0)
        {
            string log = GLES20.GlGetProgramInfoLog(program) ?? "Unknown error";
            GLES20.GlDeleteProgram(program);
            throw new InvalidOperationException($"Shader program link failed: {log}");
        }

        GLES20.GlDeleteShader(vertexShader);
        GLES20.GlDeleteShader(fragmentShader);

        return program;
    }

    private static int LoadShader(
        int shaderType,
        string source)
    {
        int shader = GLES20.GlCreateShader(shaderType);
        GLES20.GlShaderSource(shader, source);
        GLES20.GlCompileShader(shader);

        int[] compiled = new int[1];
        GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, compiled, 0);
        if (compiled[0] == 0)
        {
            string log = GLES20.GlGetShaderInfoLog(shader) ?? "Unknown error";
            GLES20.GlDeleteShader(shader);
            throw new InvalidOperationException($"Shader compile failed: {log}");
        }

        return shader;
    }

    // NOTE: Color32 is laid out as R, G, B, A bytes, so the pixels upload from where they are, with no
    // copy through a Java buffer
    internal unsafe void UpdateTexture(
        int textureId,
        RectangleInt region,
        ReadOnlySpan<Color32> pixels)
    {
        // NOTE: GL runs in order, so what was drawn with it so far goes first
        this.FlushPendingSprites();

        GLES20.GlBindTexture(GLES20.GlTexture2d, textureId);

        fixed (Color32* pointer = pixels)
        {
            glTexSubImage2D(GLES20.GlTexture2d, 0, region.X, region.Y, region.Width, region.Height, GLES20.GlRgba, GLES20.GlUnsignedByte, (nint)pointer);
        }

        GLES20.GlBindTexture(GLES20.GlTexture2d, 0);
    }

    internal static unsafe int CreateTextureFromData(
        int width,
        int height,
        ReadOnlySpan<Color32> pixels)
    {
        int[] texIds = new int[1];
        GLES20.GlGenTextures(1, texIds, 0);
        int texId = texIds[0];

        GLES20.GlBindTexture(GLES20.GlTexture2d, texId);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);

        fixed (Color32* pointer = pixels)
        {
            glTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, width, height, 0, GLES20.GlRgba, GLES20.GlUnsignedByte, (nint)pointer);
        }

        GLES20.GlBindTexture(GLES20.GlTexture2d, 0);

        return texId;
    }

    private static int CreateRenderTextureGL(
        int width,
        int height)
    {
        int[] texIds = new int[1];
        GLES20.GlGenTextures(1, texIds, 0);
        int texId = texIds[0];

        GLES20.GlBindTexture(GLES20.GlTexture2d, texId);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
        GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba, width, height, 0, GLES20.GlRgba, GLES20.GlUnsignedByte, null);
        GLES20.GlBindTexture(GLES20.GlTexture2d, 0);

        return texId;
    }

    // ──────── FBO management ────────

    private readonly Dictionary<int, int> _fboMap = new();

    // A texture's name is reused once it's deleted, and a framebuffer left behind would keep drawing
    // to the old storage. If it's the one bound, the texture was the render target.
    private void DeleteFramebuffer(
        int textureId)
    {
        if (this._fboMap.Remove(textureId, out int fbo))
        {
            if (this._boundFramebuffer == fbo)
            {
                glBindFramebuffer(GLES20.GlFramebuffer, 0);
                this._boundFramebuffer = 0;
            }

            GLES20.GlDeleteFramebuffers(1, [fbo], 0);
        }
    }

    private int GetOrCreateFramebuffer(
        int textureId)
    {
        if (this._fboMap.TryGetValue(textureId, out int fbo))
        {
            return fbo;
        }

        int[] fbos = new int[1];
        GLES20.GlGenFramebuffers(1, fbos, 0);
        fbo = fbos[0];

        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, fbo);
        GLES20.GlFramebufferTexture2D(GLES20.GlFramebuffer, GLES20.GlColorAttachment0, GLES20.GlTexture2d, textureId, 0);

        int status = GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer);
        if (status != GLES20.GlFramebufferComplete)
        {
            throw new InvalidOperationException($"Framebuffer incomplete: 0x{status:X}");
        }

        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);

        this._fboMap[textureId] = fbo;

        return fbo;
    }
}
