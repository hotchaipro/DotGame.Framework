// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.WebGL.Audio;

/// <summary>
/// WebGL representation of a decoded sound. Wraps the JavaScript-side
/// <c>AudioBuffer</c> wrapper produced by the loader.
/// </summary>
/// <remarks>
/// The decoded JavaScript <c>AudioBuffer</c> is shared by every voice
/// created for this sound, with zero per-voice copying — the Web
/// Audio API natively supports multiple
/// <c>AudioBufferSourceNode</c> instances pointing at one
/// <c>AudioBuffer</c>. Each voice bakes its own
/// <c>(offsetSeconds, durationSeconds)</c> sub-range into the
/// JS-side voice object at construction time.
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class SoundStrategy : ISoundStrategy
{
    private JSObject? _audioBuffer;
    private readonly string? _name;

    internal SoundStrategy(
        JSObject audioBuffer,
        TimeSpan duration,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(audioBuffer);

        this._audioBuffer = audioBuffer;
        this.Duration = duration;
        this._name = name;
    }

    internal string? Name => this._name;

    internal JSObject AudioBuffer =>
        this._audioBuffer ?? throw new ObjectDisposedException(nameof(SoundStrategy));

    public TimeSpan Duration { get; }

    public void Dispose()
    {
        var buffer = this._audioBuffer;
        if (buffer is null)
        {
            return;
        }

        this._audioBuffer = null;

        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3d4, $"[SoundTrace][Web] SoundStrategy.Dispose (destroy AudioBuffer): '{this._name}'");
        }

        AudioBindings.DestroyAudioBuffer(buffer);
        buffer.Dispose();
    }
}
