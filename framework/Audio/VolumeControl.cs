// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.Audio;

/// <summary>A volume that voices and music play through, which can be set, faded and chained under other controls. Sharing one between sounds makes a group.</summary>
public sealed class VolumeControl : IDisposable
{
    private readonly bool _isMaster;
    private readonly VolumeControl? _parent;
    private int _users;
    private bool _isDisposed;

    internal VolumeControl(
        Strategy<IVolumeControlStrategy> volumeControlStrategy,
        VolumeControl? parent = null,
        bool isMaster = false)
    {
        ArgumentNullException.ThrowIfNull(volumeControlStrategy);

        this.Strategy = volumeControlStrategy;
        this._isMaster = isMaster;

        // NOTE: The master is what no parent means, and a voice or music's platform code treats it so
        if (parent is not null)
        {
            parent.AddUser();
            this._parent = parent;
            this.Strategy.Interface.Parent = parent._isMaster ? null : parent.Strategy;
        }
    }

    [PlatformContract]
    internal readonly Strategy<IVolumeControlStrategy> Strategy;

    internal bool IsDisposed => this._isDisposed;

    /// <summary>
    /// Gets or sets the volume level, from 0 (silent) to 1 (maximum volume). A value outside that is
    /// clamped to it on every platform, as one worked out with floats can land just past it.
    /// </summary>
    /// <remarks>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The control has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a number.</exception>
    public float Volume
    {
        get
        {
            ObjectDisposedException.ThrowIf(this._isDisposed, this);

            return this.Strategy.Interface.Volume;
        }

        set
        {
            ObjectDisposedException.ThrowIf(this._isDisposed, this);

            this.Strategy.Interface.Volume = ClampVolume(value);
        }
    }

    /// <summary>
    /// Gradually changes the volume to a specified value over a duration.
    /// </summary>
    /// <param name="toVolume">The target volume level, from 0 (silent) to 1 (full volume). A value outside that is clamped to it.</param>
    /// <param name="duration">The total time over which the fade occurs. Must be a positive value.</param>
    /// <param name="curve">
    /// The shape the fade follows, or <see langword="null"/> to use
    /// <see cref="FadeCurve.Exponential"/>.
    /// </param>
    /// <remarks>
    /// The default fade is exponential (a constant number of decibels per second), so it is
    /// perceived as changing at a steady rate rather than dropping away abruptly at the end of a
    /// fade-out. <see cref="FadeCurve"/> offers other built-in shapes, and custom shapes are
    /// supported by deriving from it. The same curve is applied whether the volume is rising or
    /// falling.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The control has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The volume is not a number.</exception>
    public void FadeTo(
        float toVolume,
        TimeSpan duration,
        FadeCurve? curve = null)
    {
        ObjectDisposedException.ThrowIf(this._isDisposed, this);

        this.Strategy.Interface.FadeTo(ClampVolume(toVolume), duration, curve ?? FadeCurve.Exponential);
    }

    private static float ClampVolume(
        float volume,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(volume))] string? name = null)
    {
        if (float.IsNaN(volume))
        {
            throw new ArgumentOutOfRangeException(name, volume, "A volume can't be not a number.");
        }

        return Math.Clamp(volume, 0f, 1f);
    }

    /// <summary>
    /// Releases the control. Whatever is still under it goes silent, as a game that disposes a control
    /// before the sounds and controls it made under it has a bug that shouldn't get louder. The master
    /// control belongs to the audio service, and this does nothing to it.
    /// </summary>
    public void Dispose()
    {
        if (this._isMaster || this._isDisposed)
        {
            return;
        }

        this._isDisposed = true;
        GC.SuppressFinalize(this);

        // NOTE: The platform's control stays until nothing is under it, so nothing there changes
        if (this._users > 0)
        {
            this.Strategy.Interface.Volume = 0f;
        }

        this.ReleaseIfUnused();
    }

    // A control, voice or music under this one, which keeps the platform's control alive.
    internal void AddUser()
    {
        ObjectDisposedException.ThrowIf(this._isDisposed, this);

        this._users++;
    }

    internal void RemoveUser()
    {
        this._users--;
        this.ReleaseIfUnused();
    }

    private void ReleaseIfUnused()
    {
        if (this._isDisposed && (this._users == 0))
        {
            this.Strategy.Dispose();
            this._parent?.RemoveUser();
        }
    }
}
