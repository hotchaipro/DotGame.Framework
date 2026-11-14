// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Graphics;

/// <summary>
/// Represents a handle to a shader uniform parameter.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ShaderParameter"/> instances are lightweight handles that reference
/// parameters owned by a <see cref="Shader"/>. They do not independently own
/// resources and should not be disposed. The underlying resources are released
/// when the owning <see cref="Shader"/> is disposed.
/// </para>
/// <para>
/// Obtain instances via <see cref="Shader.GetParameter(string)"/>.
/// </para>
/// </remarks>
public sealed class ShaderParameter
{
    private readonly Strategy<IShaderParameterStrategy> _strategy;

    internal ShaderParameter(
        Strategy<IShaderParameterStrategy> shaderParameterStrategy)
    {
        ArgumentNullException.ThrowIfNull(shaderParameterStrategy);

        this._strategy = shaderParameterStrategy;
    }

    /// <summary>Sets the parameter, for everything drawn from now on.</summary>
    public void SetValue(float value)
    {
        this._strategy.Interface.SetValue(value);
    }

    /// <summary>Sets the parameter, for everything drawn from now on.</summary>
    public void SetValue(int value)
    {
        this._strategy.Interface.SetValue(value);
    }

    /// <summary>Sets the parameter, for everything drawn from now on.</summary>
    public void SetValue(Vector2 value)
    {
        this._strategy.Interface.SetValue(value);
    }

    /// <summary>Sets the parameter, for everything drawn from now on.</summary>
    public void SetValue(Vector4 value)
    {
        this._strategy.Interface.SetValue(value);
    }
}
