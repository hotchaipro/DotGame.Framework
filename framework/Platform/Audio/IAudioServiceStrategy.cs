// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;

namespace DotGame.Framework.Platform.Audio;

/// <summary>
/// Platform audio backend.
/// </summary>
/// <remarks>
/// Music playback is on each track's <see cref="IMusicStrategy"/>.
/// </remarks>
internal interface IAudioServiceStrategy
{
    Task OnInitialize();

    /// <summary>
    /// The control over everything played: every control with no parent, and every voice and
    /// music with no control.
    /// </summary>
    Strategy<IVolumeControlStrategy> MasterVolumeControl { get; }

    /// <summary>
    /// Creates a new volume control.
    /// </summary>
    Strategy<IVolumeControlStrategy> CreateVolumeControl();

    /// <summary>
    /// Creates a pre-allocated voice that plays the specified
    /// <c>(offset, duration)</c> segment of the given sound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Voice creation is the only time the platform needs to allocate
    /// per-voice resources for sound playback. The segment is baked
    /// into the voice at construction time (e.g. validated, converted
    /// to a sample-frame range, pinned, marshaled across JS interop)
    /// so subsequent <see cref="IVoiceStrategy.Play"/> calls reuse
    /// those resources and must not allocate on the managed heap.
    /// </para>
    /// <para>
    /// The framework guarantees <paramref name="offset"/> is
    /// non-negative and <paramref name="duration"/> is positive, but
    /// implementations are responsible for clamping the resulting
    /// range to the sound's decoded audio.
    /// </para>
    /// </remarks>
    Strategy<IVoiceStrategy> CreateVoice(
        Strategy<ISoundStrategy> sound,
        TimeSpan offset,
        TimeSpan duration);

    /// <summary>
    /// Whether the system advises starting the game's music now: <c>false</c> while the
    /// player's own audio comes first. On platforms with no equivalent system signal
    /// (desktop, web) this is always <c>true</c>.
    /// </summary>
    bool ShouldPlayMusic();
}
