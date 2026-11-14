// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;

namespace DotGame.Framework.Android.Audio;

/// <summary>
/// Mixes every Android sound voice into one interleaved stereo float
/// stream, which <c>AudioOutput</c> writes to the
/// device's single sound-effect <c>AudioTrack</c>.
/// </summary>
/// <remarks>
/// <para>
/// Voices register when created and unregister when disposed; the
/// list is copy-on-write, so <see cref="Mix"/> on the audio thread
/// reads it without a lock. Registering allocates, but only when a
/// voice is created; playing allocates nothing.
/// </para>
/// <para>
/// This class has no Android dependencies, so the framework tests
/// compile it in and drive <see cref="Mix"/> directly.
/// See <c>docs/architecture/audio.md</c>.
/// </para>
/// </remarks>
internal sealed class AudioMixer
{
    private readonly Lock _voicesLock = new();
    private VoiceStrategy[] _voices = [];
    private Action? _voiceStarted;

    /// <summary>
    /// Called (on the game thread) whenever a voice starts or resumes,
    /// so the output can wake if it parked itself while idle. Set once,
    /// before any voice plays.
    /// </summary>
    internal void SetVoiceStartedHandler(
        Action handler)
    {
        this._voiceStarted = handler;
    }

    internal void Register(
        VoiceStrategy voice)
    {
        lock (this._voicesLock)
        {
            var voices = this._voices;
            var updated = new VoiceStrategy[voices.Length + 1];
            Array.Copy(voices, updated, voices.Length);
            updated[voices.Length] = voice;
            Volatile.Write(ref this._voices, updated);
        }
    }

    internal void Unregister(
        VoiceStrategy voice)
    {
        lock (this._voicesLock)
        {
            var voices = this._voices;
            int index = Array.IndexOf(voices, voice);
            if (index < 0)
            {
                return;
            }

            var updated = new VoiceStrategy[voices.Length - 1];
            Array.Copy(voices, 0, updated, 0, index);
            Array.Copy(voices, index + 1, updated, index, voices.Length - index - 1);
            Volatile.Write(ref this._voices, updated);
        }
    }

    internal void OnVoiceStarted()
    {
        this._voiceStarted?.Invoke();
    }

    /// <summary>
    /// Overwrites the first <paramref name="frames"/> stereo frames of
    /// <paramref name="output"/> with the mix of every playing voice.
    /// Called on the audio thread only. Returns <c>false</c> when no
    /// voice was playing (the output is silence).
    /// </summary>
    internal bool Mix(
        float[] output,
        int frames)
    {
        Array.Clear(output, 0, frames * 2);

        bool anyPlaying = false;
        var voices = Volatile.Read(ref this._voices);
        for (int i = 0; i < voices.Length; i++)
        {
            if (voices[i].MixInto(output, frames))
            {
                anyPlaying = true;
            }
        }

        return anyPlaying;
    }
}
