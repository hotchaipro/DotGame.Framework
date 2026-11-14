// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Desktop.Graphics;

internal sealed class ShaderStrategy : IShaderStrategy, IDisposable
{
    private readonly GraphicsServiceStrategy _graphicsService;
    private nint _vertexShader;
    private nint _fragmentShader;
    private nint _pipeline;
    private readonly bool _ownsVertexShader;
    private readonly uint _fragmentUniformBufferCount;
    private readonly Dictionary<string, uint> _uniformLayout;
    private readonly Dictionary<string, ShaderParameterStrategy> _cachedParameters = new();
    private byte[] _uniforms = new byte[64];
    private int _uniformSize;

    internal ShaderStrategy(
        GraphicsServiceStrategy graphicsService,
        nint vertexShader,
        nint fragmentShader,
        uint fragmentUniformBufferCount = 0,
        Dictionary<string, uint>? uniformLayout = null,
        bool ownsVertexShader = true)
    {
        this._graphicsService = graphicsService ?? throw new ArgumentNullException(nameof(graphicsService));
        this._vertexShader = vertexShader;
        this._fragmentShader = fragmentShader;
        this._fragmentUniformBufferCount = fragmentUniformBufferCount;
        this._uniformLayout = uniformLayout ?? new();
        this._ownsVertexShader = ownsVertexShader;

        // Create the graphics pipeline for this shader combination
        this._pipeline = graphicsService.CreateGraphicsPipeline(vertexShader, fragmentShader);
    }

    ~ShaderStrategy()
    {
        this.Dispose(false);
    }

    internal nint Pipeline => this._pipeline;

    // The pipeline for each other blend mode, made when first used
    private readonly nint[] _blendPipelines = new nint[10];

    internal nint GetPipeline(
        BlendMode blend)
    {
        bool multisampled = this._graphicsService.DrawsMultisampled;
        if ((blend == BlendMode.Alpha) && !multisampled)
        {
            return this._pipeline;
        }

        ref nint pipeline = ref this._blendPipelines[(int)blend + (multisampled ? 5 : 0)];
        if (pipeline == nint.Zero)
        {
            pipeline = this._graphicsService.CreateGraphicsPipeline(this._vertexShader, this._fragmentShader, blend, multisampled);
        }

        return pipeline;
    }

    internal nint VertexShader => this._vertexShader;

    internal nint FragmentShader => this._fragmentShader;

    internal uint FragmentUniformBufferCount => this._fragmentUniformBufferCount;

    /// <summary>
    /// Copies the parameters set so far to a byte buffer for the fragment uniform push. Each is at
    /// its SPIR-V reflected byte offset, so the order of GetParameter calls is irrelevant.
    /// </summary>
    internal uint GetFragmentUniformData(
        Span<byte> buffer)
    {
        int size = Math.Min(this._uniformSize, buffer.Length);
        this._uniforms.AsSpan(0, size).CopyTo(buffer);
        return (uint)size;
    }

    /// <summary>
    /// Goes up each time a parameter's value changes, so the draw recorder can tell, sprite by
    /// sprite, whether the values its current segment captured still hold.
    /// </summary>
    internal int UniformVersion { get; private set; }

    internal void WriteUniform<T>(
        uint byteOffset,
        T value)
        where T : unmanaged
    {
        int end = (int)byteOffset + Unsafe.SizeOf<T>();
        if (end > this._uniforms.Length)
        {
            Array.Resize(ref this._uniforms, end);
        }

        // NOTE: Setting a value a parameter already has changes nothing, so the sprites either
        // side of it can still share a draw
        var bytes = this._uniforms.AsSpan((int)byteOffset, Unsafe.SizeOf<T>());
        if ((end <= this._uniformSize) && bytes.SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))))
        {
            return;
        }

        MemoryMarshal.Write(bytes, in value);
        this._uniformSize = Math.Max(this._uniformSize, end);
        this.UniformVersion++;
    }

    public Strategy<IShaderParameterStrategy> GetParameter(
        string name)
    {
        if (!this._cachedParameters.TryGetValue(name, out var parameter))
        {
            if (!this._uniformLayout.TryGetValue(name, out uint byteOffset))
            {
                throw new InvalidOperationException(
                    $"Shader parameter '{name}' not found in reflected uniform layout.");
            }

            parameter = new ShaderParameterStrategy(this, name, byteOffset);
            this._cachedParameters.Add(name, parameter);
        }

        return new Strategy<IShaderParameterStrategy>(parameter);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this.Dispose(true);
    }

    private void Dispose(
        bool _)
    {
        // NOTE: A shader the game still holds can be finalized after the device has gone
        if (!this._graphicsService.IsDeviceAlive)
        {
            return;
        }

        var device = this._graphicsService.GPUDevice;

        if (this._pipeline != nint.Zero)
        {
            SDL.ReleaseGPUGraphicsPipeline(device, this._pipeline);
            this._pipeline = nint.Zero;
        }

        for (int i = 0; i < this._blendPipelines.Length; i++)
        {
            if (this._blendPipelines[i] != nint.Zero)
            {
                SDL.ReleaseGPUGraphicsPipeline(device, this._blendPipelines[i]);
                this._blendPipelines[i] = nint.Zero;
            }
        }

        if (this._ownsVertexShader && this._vertexShader != nint.Zero)
        {
            SDL.ReleaseGPUShader(device, this._vertexShader);
            this._vertexShader = nint.Zero;
        }

        if (this._fragmentShader != nint.Zero)
        {
            SDL.ReleaseGPUShader(device, this._fragmentShader);
            this._fragmentShader = nint.Zero;
        }
    }
}
