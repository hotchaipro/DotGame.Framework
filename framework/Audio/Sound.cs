// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using DotGame.Framework.Content;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.Audio;

/// <summary>
/// A decoded sound cue. Sounds are produced by
/// <c>Content.LoadAsync&lt;Sound&gt;(contentKey)</c>. To play a sound, create
/// one or more reusable <see cref="SoundVoice"/> objects via
/// <see cref="AudioService.CreateVoice(Sound)"/>; each voice owns
/// pre-allocated platform resources and plays this sound's data with
/// no further managed-side allocation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Sound"/> is the framework's atomic, single-buffer
/// audio resource — the audio analogue of
/// <see cref="Graphics.Texture"/>. The framework deliberately has no
/// notion of a sub-range within a sound; higher-level concepts such
/// as sound banks express named cues by pairing a sound with an
/// offset and duration at voice-creation time.
/// </para>
/// <para>
/// <see cref="Sound"/> is <see cref="IContent"/>: its decoded audio
/// is released by <c>Content.UnloadAsync</c>. Callers do not see a public
/// <c>Dispose</c> method and must not attempt to dispose a sound
/// directly; the content service owns that lifecycle.
/// </para>
/// </remarks>
public sealed class Sound : IContent
{
    private bool _isDisposed;

    [PlatformContract]
    internal Sound(
        Strategy<ISoundStrategy> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        this.Strategy = strategy;
    }

    // The voices made from this sound and not yet disposed
    private readonly List<SoundVoice> _voices = [];

    [PlatformContract]
    internal readonly Strategy<ISoundStrategy> Strategy;

    /// <summary>
    /// Duration of this sound's decoded audio.
    /// </summary>
    public TimeSpan Duration => this.Strategy.Interface.Duration;

    /// <summary>Whether it has been released.</summary>
    public bool IsDisposed => this._isDisposed;

    internal void AddVoice(
        SoundVoice voice)
    {
        this._voices.Add(voice);
    }

    internal void RemoveVoice(
        SoundVoice voice)
    {
        this._voices.Remove(voice);
    }

    void IDisposable.Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        // NOTE: Before the flag, which stops a voice's Stop reaching the platform. No voice plays on
        // through freed samples, and none plays on where the platform holds its own copy
        foreach (var voice in this._voices)
        {
            voice.Stop();
        }

        this._voices.Clear();

        this._isDisposed = true;
        GC.SuppressFinalize(this);

        this.Strategy.Dispose();
    }
}
