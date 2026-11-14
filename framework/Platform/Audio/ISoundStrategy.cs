// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Platform.Audio;

/// <summary>
/// Platform-specific representation of a decoded sound. Holds the
/// decoded audio data shared by every voice created for this sound.
/// </summary>
/// <remarks>
/// Sounds are produced by the platform's sound content loader and
/// consumed by <see cref="IAudioServiceStrategy.CreateVoice"/>. A
/// single sound may back any number of voices — each potentially
/// playing a different (offset, duration) sub-range — and the
/// framework never makes a managed copy of the decoded audio per
/// voice or per playback.
/// </remarks>
internal interface ISoundStrategy : IDisposable
{
    /// <summary>
    /// Duration of this sound's decoded audio.
    /// </summary>
    TimeSpan Duration { get; }
}
