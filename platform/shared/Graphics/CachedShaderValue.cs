// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Numerics;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Platform.Common.Graphics;

/// <summary>
/// The last value a shader parameter was set to, without boxing it: setting a uniform flushes
/// the sprites queued so far, so an unchanged value is skipped, and an animated one comes every frame.
/// </summary>
internal struct CachedShaderValue
{
    private enum Kind : byte
    {
        None,
        Float,
        Int,
        Vector2,
        Vector4,
    }

    private Kind _kind;
    private Vector4 _value;
    private int _intValue;

    /// <summary>
    /// Sets the value on another parameter, as after a shader's program is replaced.
    /// </summary>
    public readonly void ApplyTo(
        IShaderParameterStrategy parameter)
    {
        switch (this._kind)
        {
            case Kind.Float:
                parameter.SetValue(this._value.X);
                break;
            case Kind.Int:
                parameter.SetValue(this._intValue);
                break;
            case Kind.Vector2:
                parameter.SetValue(new Vector2(this._value.X, this._value.Y));
                break;
            case Kind.Vector4:
                parameter.SetValue(this._value);
                break;
        }
    }

    public bool TryChange(
        float value)
    {
        if ((this._kind == Kind.Float) && (this._value.X == value))
        {
            return false;
        }

        this._kind = Kind.Float;
        this._value.X = value;
        return true;
    }

    public bool TryChange(
        int value)
    {
        if ((this._kind == Kind.Int) && (this._intValue == value))
        {
            return false;
        }

        this._kind = Kind.Int;
        this._intValue = value;
        return true;
    }

    public bool TryChange(
        Vector2 value)
    {
        if ((this._kind == Kind.Vector2) && (this._value.X == value.X) && (this._value.Y == value.Y))
        {
            return false;
        }

        this._kind = Kind.Vector2;
        this._value = new Vector4(value, 0, 0);
        return true;
    }

    public bool TryChange(
        Vector4 value)
    {
        if ((this._kind == Kind.Vector4) && (this._value == value))
        {
            return false;
        }

        this._kind = Kind.Vector4;
        this._value = value;
        return true;
    }
}
