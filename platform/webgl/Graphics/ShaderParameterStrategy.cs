// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotGame.Framework.Platform.Common.Graphics;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.WebGL.Graphics;

[SupportedOSPlatform("browser")]
internal sealed class ShaderParameterStrategy : IShaderParameterStrategy, IDisposable
{
    private readonly ShaderStrategy _shader;
    private readonly string _name;

    // Null for a uniform the shader doesn't have or the driver optimized out: setting it does
    // nothing, as on the other platforms, since what's optimized out varies by GPU
    private JSObject? _location;
    private bool _isDisposed;
    private CachedShaderValue _value;

    internal ShaderParameterStrategy(
        ShaderStrategy shader,
        string name,
        JSObject? location)
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
        ObjectDisposedException.ThrowIf(this._isDisposed, this);
        if (!this._value.TryChange(value))
        {
            return;
        }

        if (this._location is { } location)
        {
            this._shader.SetParameterValue(location, value);
        }
    }

    public void SetValue(
        int value)
    {
        ObjectDisposedException.ThrowIf(this._isDisposed, this);
        if (!this._value.TryChange(value))
        {
            return;
        }

        if (this._location is { } location)
        {
            this._shader.SetParameterValue(location, value);
        }
    }

    public void SetValue(
        Vector2 value)
    {
        ObjectDisposedException.ThrowIf(this._isDisposed, this);
        if (!this._value.TryChange(value))
        {
            return;
        }

        if (this._location is { } location)
        {
            this._shader.SetParameterValue(location, value);
        }
    }

    public void SetValue(
        Vector4 value)
    {
        ObjectDisposedException.ThrowIf(this._isDisposed, this);
        if (!this._value.TryChange(value))
        {
            return;
        }

        if (this._location is { } location)
        {
            this._shader.SetParameterValue(location, value);
        }
    }

    public void Dispose()
    {
        this._isDisposed = true;

        var location = this._location;
        if (location is not null)
        {
            this._location = null;
            location.Dispose();
        }
    }
}
