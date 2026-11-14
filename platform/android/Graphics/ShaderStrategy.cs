// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Android.Opengl;
using DotGame.Framework.Platform.Graphics;
using static DotGame.Framework.Android.Graphics.GLBindings;

namespace DotGame.Framework.Android.Graphics;

internal sealed class ShaderStrategy : IShaderStrategy, IDisposable
{
    private readonly GraphicsServiceStrategy _graphicsService;
    private int _programId;
    private readonly int _generation = GLResources.Generation;
    private readonly string _vertexSource;
    private readonly string _fragmentSource;
    private readonly Dictionary<string, ShaderParameterStrategy> _cachedParameters = new();

    internal ShaderStrategy(
        int programId,
        string vertexSource,
        string fragmentSource,
        GraphicsServiceStrategy graphicsService)
    {
        if (programId == 0)
        {
            throw new ArgumentException("Invalid GL program.", nameof(programId));
        }

        ArgumentNullException.ThrowIfNull(graphicsService);

        this._programId = programId;
        this._vertexSource = vertexSource;
        this._fragmentSource = fragmentSource;
        this._graphicsService = graphicsService;
    }

    ~ShaderStrategy()
    {
        this.Dispose(false);
    }

    internal int ProgramId => this._programId != 0
        ? this._programId
        : throw new ObjectDisposedException(nameof(ShaderStrategy));

    internal string VertexSource => this._vertexSource;

    internal string FragmentSource => this._fragmentSource;

    internal void CopyShaderParameters(
        ShaderStrategy other)
    {
        foreach (var (parameterName, otherParameter) in other._cachedParameters)
        {
            var param = this.GetParameter(parameterName).Interface;
            var value = otherParameter.Value;
            value.ApplyTo(param);
        }
    }

    public Strategy<IShaderParameterStrategy> GetParameter(
        string name)
    {
        if (!this._cachedParameters.TryGetValue(name, out var parameter))
        {
            int location = GLES20.GlGetUniformLocation(this.ProgramId, name);
            parameter = new ShaderParameterStrategy(this, name, location);
            this._cachedParameters.Add(name, parameter);
        }

        return new Strategy<IShaderParameterStrategy>(parameter);
    }

    internal void SetParameterValue(
        int location,
        float value)
    {
        // Uniforms persist on the program object. Any sprites already
        // queued in the active batch that reference this program would
        // otherwise replay with the new value at Submit time. Flush
        // first so prior sprites render with their intended uniforms.
        this._graphicsService.FlushPendingSprites();
        this._graphicsService.UseProgram(this.ProgramId);
        glUniform1f(location, value);
    }

    internal void SetParameterValue(
        int location,
        int value)
    {
        this._graphicsService.FlushPendingSprites();
        this._graphicsService.UseProgram(this.ProgramId);
        glUniform1i(location, value);
    }

    internal void SetParameterValue(
        int location,
        Vector2 value)
    {
        this._graphicsService.FlushPendingSprites();
        this._graphicsService.UseProgram(this.ProgramId);
        glUniform2f(location, value.X, value.Y);
    }

    internal void SetParameterValue(
        int location,
        Vector4 value)
    {
        this._graphicsService.FlushPendingSprites();
        this._graphicsService.UseProgram(this.ProgramId);
        glUniform4f(location, value.X, value.Y, value.Z, value.W);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        this.Dispose(true);
    }

    private void Dispose(
        bool _)
    {
        var programId = this._programId;
        if (programId != 0)
        {
            this._programId = 0;

            foreach (var parameter in this._cachedParameters.Values)
            {
                parameter.Dispose();
            }

            GLResources.DeleteProgram(programId, this._generation);
        }
    }
}
