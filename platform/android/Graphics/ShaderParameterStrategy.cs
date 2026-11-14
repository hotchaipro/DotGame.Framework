// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Platform.Common.Graphics;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Android.Graphics;

internal sealed class ShaderParameterStrategy : IShaderParameterStrategy, IDisposable
{
    private readonly ShaderStrategy _shader;
    private readonly string _name;
    private readonly int _location;
    private bool _disposed;
    private CachedShaderValue _value;

    internal ShaderParameterStrategy(
        ShaderStrategy shader,
        string name,
        int location)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(name);

        this._shader = shader;
        this._name = name;
        this._location = location;
    }

    public string Name => this._name;

    internal CachedShaderValue Value => this._value;

    public void SetValue(
        float value)
    {
        ObjectDisposedException.ThrowIf(this._disposed, nameof(ShaderParameterStrategy));

        // Setting a uniform draws the sprites queued so far, so an unchanged value is skipped
        // to let batches with the same settings draw together
        if (!this._value.TryChange(value))
        {
            return;
        }

        this._shader.SetParameterValue(this._location, value);
    }

    public void SetValue(
        int value)
    {
        ObjectDisposedException.ThrowIf(this._disposed, nameof(ShaderParameterStrategy));

        // Setting a uniform draws the sprites queued so far, so an unchanged value is skipped
        // to let batches with the same settings draw together
        if (!this._value.TryChange(value))
        {
            return;
        }

        this._shader.SetParameterValue(this._location, value);
    }

    public void SetValue(
        Vector2 value)
    {
        ObjectDisposedException.ThrowIf(this._disposed, nameof(ShaderParameterStrategy));

        // Setting a uniform draws the sprites queued so far, so an unchanged value is skipped
        // to let batches with the same settings draw together
        if (!this._value.TryChange(value))
        {
            return;
        }

        this._shader.SetParameterValue(this._location, value);
    }

    public void SetValue(
        Vector4 value)
    {
        ObjectDisposedException.ThrowIf(this._disposed, nameof(ShaderParameterStrategy));

        // Setting a uniform draws the sprites queued so far, so an unchanged value is skipped
        // to let batches with the same settings draw together
        if (!this._value.TryChange(value))
        {
            return;
        }

        this._shader.SetParameterValue(this._location, value);
    }

    public void Dispose()
    {
        this._disposed = true;
    }
}
