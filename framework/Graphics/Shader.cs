// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Content;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Graphics;

/// <summary>A custom way to draw sprites, compiled by the content pipeline from Slang. Load it from content, and begin a batch with it.</summary>
public sealed class Shader : IContent
{
    [PlatformContract]
    internal Shader(
        Strategy<IShaderStrategy> shaderStrategy)
    {
        ArgumentNullException.ThrowIfNull(shaderStrategy);

        this.Strategy = shaderStrategy;
    }

    [PlatformContract]
    internal readonly Strategy<IShaderStrategy> Strategy;

    /// <summary>Whether it has been released.</summary>
    public bool IsDisposed => this.Strategy.IsDisposed;

    /// <summary>
    /// Gets a handle to a named shader uniform parameter.
    /// </summary>
    /// <param name="name">The name of the uniform parameter in the shader source.</param>
    /// <returns>
    /// A <see cref="ShaderParameter"/> handle that can be used to set the parameter value.
    /// </returns>
    /// <remarks>
    /// The returned <see cref="ShaderParameter"/> is a lightweight handle and does not
    /// own resources independently. Its lifetime is tied to this <see cref="Shader"/>
    /// instance; disposing the shader invalidates all associated parameter handles.
    /// </remarks>
    public ShaderParameter GetParameter(
        string name)
    {
        return new ShaderParameter(this.Strategy.Interface.GetParameter(name));
    }

    void IDisposable.Dispose()
    {
        GC.SuppressFinalize(this);
        this.Strategy.Dispose();
    }
}
