// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.Desktop.Audio;

/// <summary>
/// SDL3 representation of a decoded sound. Holds the decoded 16-bit
/// signed little-endian interleaved PCM data shared by every voice
/// created for this sound.
/// </summary>
/// <remarks>
/// The PCM buffer is on the pinned object heap (so voices can queue it
/// into SDL with no copy via <c>SDL_PutAudioStreamDataNoCopy</c>). Voices
/// compute their own byte range into this buffer at construction time.
/// SDL reads the buffer as it plays, so disposing the sound stops its
/// voices first.
/// </remarks>
internal sealed class SoundStrategy : ISoundStrategy
{
    private readonly byte[] _pcmData;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly nint _pinnedBase;

    // Its voices, created and disposed on the game thread
    private bool _isDisposed;

    /// <param name="pcmData">The samples, allocated on the pinned object heap.</param>
    /// <param name="sampleRate">The samples per second.</param>
    /// <param name="channels">How many channels the samples interleave.</param>
    /// <param name="duration">How long it plays for.</param>
    internal SoundStrategy(
        byte[] pcmData,
        int sampleRate,
        int channels,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(pcmData);

        this._pcmData = pcmData;
        this._sampleRate = sampleRate;
        this._channels = channels;
        this.Duration = duration;
        this._pinnedBase = (pcmData.Length > 0) ? Marshal.UnsafeAddrOfPinnedArrayElement(pcmData, 0) : nint.Zero;
    }

    internal int SampleRate => this._sampleRate;

    internal int Channels => this._channels;

    internal int LengthBytes => this._pcmData.Length;

    /// <summary>
    /// Stable native pointer to the start of the PCM buffer.
    /// Valid for the lifetime of the sound.
    /// </summary>
    internal nint PinnedBase => this._pinnedBase;

    internal bool IsDisposed => this._isDisposed;

    public TimeSpan Duration { get; }

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        this._isDisposed = true;

        GC.KeepAlive(this._pcmData);
    }
}
