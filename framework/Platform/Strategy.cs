// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;

namespace DotGame.Framework.Platform;

internal sealed class Strategy<T>
    where T : class
{
    private T? _value;

    public Strategy(
        T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        this._value = value;
    }

    public T Interface
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (this._value is null)
            {
                throw this.ObjectDisposedException();
            }

            return this._value;
        }
    }

    internal bool IsDisposed => this._value is null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public U ToConcrete<U>()
        where U : T
    {
        if (this._value is null)
        {
            throw this.ObjectDisposedException();
        }

        // NOTE: Only Debug builds check: the platform always asks for its own type, and a
        // wrong one is memory corruption rather than an exception, so Debug builds catch it
        if (FeatureSwitches.IsDebug && (this._value is not U))
        {
            throw new InvalidCastException(FormattableString.Invariant(
                $"The strategy is a {DiagnosticInfo.TypeName(this._value)}, not a {DiagnosticInfo.TypeName<U>()}."));
        }

        return Unsafe.As<T, U>(ref this._value);
    }

    public void Dispose()
    {
        if (this._value is null)
        {
            // Already released, do nothing
            return;
        }

        var iDisposable = this._value as IDisposable;

        this._value = null;

        iDisposable?.Dispose();
    }

    /// <summary>
    /// Replaces the wrapped value with a new one and disposes the old value.
    /// </summary>
    internal void Reset(T newValue)
    {
        ArgumentNullException.ThrowIfNull(newValue);

        var oldValue = this._value;

        this._value = newValue;

        (oldValue as IDisposable)?.Dispose();
    }

    private ObjectDisposedException ObjectDisposedException()
    {
        return new ObjectDisposedException(nameof(Strategy<>));
    }
}
