// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Graphics;
using Metal;

namespace DotGame.Framework.iOS.Graphics;

internal sealed class ShaderStrategy : IShaderStrategy, IDisposable
{
    private readonly GraphicsServiceStrategy _graphicsService;
    private IMTLFunction? _vertexFunction;
    private IMTLFunction? _fragmentFunction;
    private IMTLRenderPipelineState? _pipelineState;
    private readonly Dictionary<string, ShaderParameterStrategy> _cachedParameters = new();
    private bool _disposed;

    private readonly Dictionary<string, int> _uniformOffsets;
    private readonly byte[] _uniforms;

    internal GraphicsServiceStrategy GraphicsService => this._graphicsService;

    // NOTE: No vertex function means the service's shared default, which it keeps
    internal ShaderStrategy(
        IMTLFunction? vertexFunction,
        IMTLFunction fragmentFunction,
        IMTLRenderPipelineState pipelineState,
        Dictionary<string, int> uniformOffsets,
        int uniformSize,
        GraphicsServiceStrategy graphicsService)
    {
        this._uniformOffsets = uniformOffsets;
        this._uniforms = new byte[uniformSize];
        this._vertexFunction = vertexFunction;
        this._fragmentFunction = fragmentFunction ?? throw new ArgumentNullException(nameof(fragmentFunction));
        this._pipelineState = pipelineState ?? throw new ArgumentNullException(nameof(pipelineState));
        this._graphicsService = graphicsService ?? throw new ArgumentNullException(nameof(graphicsService));
    }

    ~ShaderStrategy()
    {
        this.Dispose(false);
    }

    // The pipeline state for each other blend mode, made when first used
    private readonly IMTLRenderPipelineState?[] _blendPipelines = new IMTLRenderPipelineState?[10];

    internal IMTLRenderPipelineState GetPipeline(
        BlendMode blend)
    {
        bool multisampled = this._graphicsService.DrawsMultisampled;
        if ((blend == BlendMode.Alpha) && !multisampled)
        {
            return this.PipelineState;
        }

        return this._blendPipelines[(int)blend + (multisampled ? 5 : 0)] ??= this._graphicsService.CreateRenderPipelineState(
            this._vertexFunction ?? this._graphicsService.DefaultVertexFunction,
            this._fragmentFunction ?? throw new ObjectDisposedException(nameof(ShaderStrategy)),
            out _,
            out _,
            blend,
            multisampled);
    }

    internal IMTLRenderPipelineState PipelineState => this._pipelineState
        ?? throw new ObjectDisposedException(nameof(ShaderStrategy));

    public Strategy<IShaderParameterStrategy> GetParameter(
        string name)
    {
        if (!this._cachedParameters.TryGetValue(name, out var parameter))
        {
            parameter = new ShaderParameterStrategy(this, name, this._uniformOffsets.GetValueOrDefault(name, -1));
            this._cachedParameters.Add(name, parameter);
        }

        return new Strategy<IShaderParameterStrategy>(parameter);
    }

    /// <summary>
    /// The fragment stage's uniform buffer, laid out as Metal reflected it.
    /// </summary>
    internal ReadOnlySpan<byte> FragmentUniforms => this._uniforms;

    internal void WriteUniform<T>(
        int offset,
        T value)
        where T : unmanaged, IEquatable<T>
    {
        // NOTE: A name the shader doesn't have, or that its compile removed, is ignored, as on GL
        if ((offset < 0) || ((offset + Unsafe.SizeOf<T>()) > this._uniforms.Length))
        {
            return;
        }

        var bytes = this._uniforms.AsSpan(offset, Unsafe.SizeOf<T>());
        if (MemoryMarshal.Read<T>(bytes).Equals(value))
        {
            return;
        }

        // The values are read when sprites are drawn, and sprites can wait past their batch's End,
        // so the ones queued so far are drawn before a value changes
        this._graphicsService.FlushPendingSprites();
        MemoryMarshal.Write(bytes, in value);
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
            this._pipelineState?.Dispose();
            this._vertexFunction?.Dispose();
            this._fragmentFunction?.Dispose();
        }

        this._pipelineState = null;
        this._vertexFunction = null;
        this._fragmentFunction = null;
    }
}
