// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.Audio;

/// <summary>Plays sound: makes voices and volume controls, and holds the master volume. Reach it as <c>Game.Audio</c>.</summary>
public sealed class AudioService
{
    internal AudioService(
        Strategy<IAudioServiceStrategy> audioServiceStrategy)
    {
        ArgumentNullException.ThrowIfNull(audioServiceStrategy);

        this.Strategy = audioServiceStrategy;
    }

    private readonly Strategy<IAudioServiceStrategy> Strategy;

    private VolumeControl? _masterVolumeControl;

    /// <summary>
    /// The volume of everything the game plays, for a settings screen or a mute button: every
    /// other control ends up under it, and so does a voice or music with no control.
    /// </summary>
    public VolumeControl MasterVolumeControl => this._masterVolumeControl ??= new VolumeControl(this.Strategy.Interface.MasterVolumeControl, isMaster: true);

    internal async Task Initialize()
    {
        await this.Strategy.Interface.OnInitialize();
    }

    /// <summary>
    /// Gets whether the system advises starting the game's music now. On a phone it says no while the
    /// player has their own music, a podcast or a call going, as their audio comes first.
    /// </summary>
    /// <remarks>
    /// It's advice about what to do, not a report of what's playing: the signal differs by platform. Where
    /// there's none (desktop, web), it's always <c>true</c>. Read it before starting music, and again later
    /// if it said no. Music already playing is paused and resumed for this on iOS and Android by itself.
    /// </remarks>
    public bool ShouldPlayMusic => this.Strategy.Interface.ShouldPlayMusic();

    /// <summary>
    /// Creates a new volume control.
    /// </summary>
    /// <param name="parent">The control this one's volume is multiplied by, so that what plays through
    /// it also follows the parent's volume and fades: a "sound effects" group, say. With none, it's
    /// directly under <see cref="MasterVolumeControl"/>.</param>
    /// <returns>A new <see cref="VolumeControl"/> instance.</returns>
    /// <exception cref="ObjectDisposedException">The parent has been disposed.</exception>
    public VolumeControl CreateVolumeControl(
        VolumeControl? parent = null)
    {
        // NOTE: Before the platform makes anything that would be left over
        ObjectDisposedException.ThrowIf(parent is { IsDisposed: true }, typeof(VolumeControl));

        var strategy = this.Strategy.Interface.CreateVolumeControl();
        return new VolumeControl(strategy, parent);
    }

    /// <summary>
    /// Creates a pre-allocated voice that plays the entirety of
    /// <paramref name="sound"/>.
    /// </summary>
    /// <remarks>
    /// Voice creation engages the platform once and reserves all
    /// resources required to play <paramref name="sound"/> repeatedly.
    /// Subsequent <see cref="SoundVoice.Play"/> calls reuse those
    /// resources and do not allocate. For overlapping instances of
    /// the same sound create multiple voices, such as a fixed-size ring of
    /// them.
    /// </remarks>
    public SoundVoice CreateVoice(
        Sound sound)
    {
        ArgumentNullException.ThrowIfNull(sound);

        return this.CreateVoice(sound, TimeSpan.Zero, sound.Duration);
    }

    /// <summary>
    /// Creates a pre-allocated voice that plays the specified
    /// <c>(offset, duration)</c> segment of <paramref name="sound"/>.
    /// The segment is baked into the voice at construction time so
    /// <see cref="SoundVoice.Play"/> remains allocation-free on every
    /// platform.
    /// </summary>
    public SoundVoice CreateVoice(
        Sound sound,
        TimeSpan offset,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(sound);

        if (offset < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "Offset must be non-negative.");
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Duration must be positive.");
        }

        var voiceStrategy = this.Strategy.Interface.CreateVoice(
            sound.Strategy,
            offset,
            duration);

        return new SoundVoice(voiceStrategy, sound);
    }

    // Shared validation for the playback range accepted by Music's Play and
    // PrepareAsync, mirroring the CreateVoice checks above.
    internal static void ValidateRange(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint)
    {
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                "Start must be non-negative.");
        }

        if (end < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                "End must be non-negative.");
        }

        if (loopPoint < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(loopPoint),
                "Loop point must be non-negative.");
        }

        if (start.HasValue && end.HasValue && end.Value <= start.Value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                "End must be greater than start.");
        }
    }
}
