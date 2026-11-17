// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.Platform.Common.Audio;

/// <summary>
/// A decoded sound on a platform that mixes sounds itself (Android
/// and desktop): interleaved float samples at the mixer's output rate,
/// shared by every voice that plays the sound.
/// </summary>
/// <remarks>
/// The loader converts the sound once, at load, to the rate and
/// format <see cref="AudioMixer"/> mixes in (<see cref="PcmConverter"/>), so mixing is a
/// multiply-add per sample with no conversion. Mono sounds stay mono;
/// the mixer writes them to both channels.
/// </remarks>
internal sealed class MixerSoundStrategy : ISoundStrategy
{
    internal MixerSoundStrategy(
        float[] samples,
        int channels,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if ((channels != 1) && (channels != 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        this.Samples = samples;
        this.Channels = channels;
        this.FrameCount = samples.Length / channels;
        this.Duration = duration;
    }

    /// <summary>
    /// Interleaved samples at the output rate.
    /// </summary>
    internal float[] Samples { get; }

    /// <summary>
    /// 1 (mono) or 2 (stereo).
    /// </summary>
    internal int Channels { get; }

    internal int FrameCount { get; }

    public TimeSpan Duration { get; }

    public void Dispose()
    {
        // The samples are a plain managed buffer; the GC reclaims them
        // once no voice references them.
    }
}
