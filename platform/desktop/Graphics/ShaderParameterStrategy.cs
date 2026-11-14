// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Desktop.Graphics;

internal sealed class ShaderParameterStrategy : IShaderParameterStrategy
{
    private readonly string _name;
    private readonly uint _byteOffset;
    private readonly ShaderStrategy _shader;

    internal ShaderParameterStrategy(
        ShaderStrategy shader,
        string name,
        uint byteOffset)
    {
        this._shader = shader;
        this._name = name ?? throw new ArgumentNullException(nameof(name));
        this._byteOffset = byteOffset;
    }

    public string Name => this._name;

    public void SetValue(
        float value)
    {
        this._shader.WriteUniform(this._byteOffset, value);
    }

    public void SetValue(
        int value)
    {
        this._shader.WriteUniform(this._byteOffset, value);
    }

    public void SetValue(
        Vector2 value)
    {
        this._shader.WriteUniform(this._byteOffset, value);
    }

    public void SetValue(
        Vector4 value)
    {
        this._shader.WriteUniform(this._byteOffset, value);
    }
}
