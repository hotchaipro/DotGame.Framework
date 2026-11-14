// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.iOS.Graphics;

internal sealed class ShaderParameterStrategy : IShaderParameterStrategy
{
    private readonly ShaderStrategy _shader;
    private readonly string _name;
    private readonly int _offset;

    internal ShaderParameterStrategy(
        ShaderStrategy shader,
        string name,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(name);

        this._shader = shader;
        this._name = name;
        this._offset = offset;
    }

    public string Name => this._name;

    public void SetValue(
        float value)
    {
        this._shader.WriteUniform(this._offset, value);
    }

    public void SetValue(
        int value)
    {
        this._shader.WriteUniform(this._offset, value);
    }

    public void SetValue(
        Vector2 value)
    {
        this._shader.WriteUniform(this._offset, value);
    }

    public void SetValue(
        Vector4 value)
    {
        this._shader.WriteUniform(this._offset, value);
    }
}
