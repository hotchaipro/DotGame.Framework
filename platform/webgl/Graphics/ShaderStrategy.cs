// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.WebGL.Graphics;

[SupportedOSPlatform("browser")]
internal sealed class ShaderStrategy : IShaderStrategy, IDisposable
{
    private readonly GraphicsServiceStrategy _graphicsService;
    private JSObject? _nativeHandle;

    /// <summary>
    /// Cached parameter handles, keyed by uniform name. The <see cref="Strategy{T}"/>
    /// wrappers are shared with every <c>ShaderParameter</c> handed out to callers, so
    /// they must survive a WebGL context loss: on restore the wrappers are adopted by the
    /// recompiled shader and retargeted at the new uniform locations.
    /// </summary>
    private readonly Dictionary<string, Strategy<IShaderParameterStrategy>> _cachedParameters = new();

    /// <summary>
    /// Initializes a new instance of the Shader class with a pre-compiled shader program and source code references.
    /// </summary>
    /// <param name="jsShader">The pre-compiled WebGL shader program.</param>
    /// <param name="graphicsService">The graphics service that owns the WebGL context.</param>
    public ShaderStrategy(
        JSObject jsShader,
        GraphicsServiceStrategy graphicsService)
    {
        ArgumentNullException.ThrowIfNull(jsShader);
        ArgumentNullException.ThrowIfNull(graphicsService);

        this._nativeHandle = jsShader;
        this._graphicsService = graphicsService;
    }

    ~ShaderStrategy()
    {
        this.Dispose(false);
    }

    /// <summary>
    /// Gets the compiled WebGL program (JSObject).
    /// This is the primary shader representation used for rendering.
    /// </summary>
    internal JSObject NativeHandle => this._nativeHandle ?? throw new ArgumentNullException(nameof(this.NativeHandle));

    /// <summary>
    /// Takes ownership of the parameter handles previously handed out by <paramref name="other"/>,
    /// retargeting each one at this shader's uniform locations and replaying its last value.
    /// </summary>
    /// <remarks>
    /// Callers (for example effect classes) cache <c>ShaderParameter</c> handles for the lifetime
    /// of the shader. Creating fresh handles after a context restore would leave those cached
    /// handles pointing at disposed parameters, so the existing wrappers are reused instead.
    /// </remarks>
    internal void AdoptShaderParameters(
        ShaderStrategy other)
    {
        foreach (var (parameterName, parameterStrategy) in other._cachedParameters)
        {
            var value = parameterStrategy.ToConcrete<ShaderParameterStrategy>().Value;

            var location = WebGLBindings.GetUniformLocation(this._graphicsService.WebGLContext, this.NativeHandle, parameterName);

            // Retargets the shared wrapper and disposes the stale parameter (and its location proxy).
            parameterStrategy.Reset(new ShaderParameterStrategy(this, parameterName, location));

            this._cachedParameters[parameterName] = parameterStrategy;

            var param = parameterStrategy.Interface;
            value.ApplyTo(param);
        }

        // Ownership has transferred; prevent the old shader's Dispose from
        // tearing down the freshly created parameters.
        other._cachedParameters.Clear();
    }

    public Strategy<IShaderParameterStrategy> GetParameter(
        string name)
    {
        if (!this._cachedParameters.TryGetValue(name, out var parameterStrategy))
        {
            var location = WebGLBindings.GetUniformLocation(this._graphicsService.WebGLContext, this.NativeHandle, name);
            parameterStrategy = new Strategy<IShaderParameterStrategy>(new ShaderParameterStrategy(this, name, location));
            this._cachedParameters.Add(name, parameterStrategy);
        }

        return parameterStrategy;
    }

    internal void SetParameterValue(
        JSObject location,
        float value)
    {
        this._graphicsService.Recorder.RecordSetUniform(this.NativeHandle, location, value);
    }

    internal void SetParameterValue(
        JSObject location,
        int value)
    {
        this._graphicsService.Recorder.RecordSetUniform(this.NativeHandle, location, value);
    }

    internal void SetParameterValue(
        JSObject location,
        Vector2 value)
    {
        this._graphicsService.Recorder.RecordSetUniform(this.NativeHandle, location, value);
    }

    internal void SetParameterValue(
        JSObject location,
        Vector4 value)
    {
        this._graphicsService.Recorder.RecordSetUniform(this.NativeHandle, location, value);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this.Dispose(true);
    }

    /// <remarks>
    /// See <see cref="TextureStrategy"/> for why the finalizer path performs no
    /// interop. This type has a second reason to respect the flag: the cached
    /// parameter strategies below are managed objects with their own
    /// finalizers, and disposing them from this one would be reaching into
    /// objects that may already have been reclaimed.
    /// </remarks>
    private void Dispose(
        bool disposing)
    {
        var nativeHandle = this._nativeHandle;
        if (nativeHandle is null)
        {
            return;
        }

        this._nativeHandle = null;

        if (!disposing)
        {
            Trace.Write(0x9c41d7a1, "[ShaderStrategy] Finalized without Dispose; GL program left to the context");
            return;
        }

        // Dispose cached shader parameters (releases uniform location JSObject proxies)
        foreach (var parameterStrategy in this._cachedParameters.Values)
        {
            parameterStrategy.Dispose();
        }

        this._cachedParameters.Clear();

        // Delete the WebGL program on the browser side
        WebGLBindings.DeleteShaderProgram(nativeHandle);

        nativeHandle.Dispose();
    }
}
