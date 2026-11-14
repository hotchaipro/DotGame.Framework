// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace DotGame.Framework.WebGL.Audio;

[SupportedOSPlatform("browser")]
internal sealed partial class AudioBindings
{
    private const string ModuleName = "audio.js";

    // Diagnostics

    [JSImport("audio.initializeAudio", ModuleName)]
    public static partial void InitializeAudio(bool traceAudio);

    // AudioBuffer bindings

    [JSImport("audio.createAudioBuffer", ModuleName)]
    public static partial JSObject CreateAudioBuffer();

    [JSImport("audio.destroyAudioBuffer", ModuleName)]
    public static partial void DestroyAudioBuffer(JSObject audioBuffer);

    [JSImport("audio.getAudioBufferDuration", ModuleName)]
    public static partial double GetAudioBufferDuration(JSObject audioBuffer);

    [JSImport("audio.loadAudioBufferFromContent", ModuleName)]
    public static partial Task LoadAudioBufferFromContent(JSObject audioBuffer, string filePath);

    // Per-voice bindings. A voice owns a cached JS-side gain node
    // connected to either the destination or a volume control's
    // gain node; each Play creates a fresh AudioBufferSourceNode
    // on the JS side and routes it through the cached gain.

    [JSImport("audio.createVoice", ModuleName)]
    public static partial JSObject CreateVoice(
        JSObject audioBuffer,
        double offsetSeconds,
        double durationSeconds);

    [JSImport("audio.destroyVoice", ModuleName)]
    public static partial void DestroyVoice(JSObject voice);

    [JSImport("audio.playVoice", ModuleName)]
    public static partial void PlayVoice(JSObject voice);

    [JSImport("audio.stopVoice", ModuleName)]
    public static partial void StopVoice(JSObject voice);

    [JSImport("audio.pauseVoice", ModuleName)]
    public static partial void PauseVoice(JSObject voice);

    [JSImport("audio.isVoicePlaying", ModuleName)]
    public static partial bool IsVoicePlaying(JSObject voice);

    [JSImport("audio.setVoiceLooping", ModuleName)]
    public static partial void SetVoiceLooping(JSObject voice, bool isLooping);

    [JSImport("audio.setVoiceVolumeControl", ModuleName)]
    public static partial void SetVoiceVolumeControl(JSObject voice, JSObject? volumeControl);

    // AudioStream bindings. An AudioStream is a streaming music track:
    // it is fetched, demuxed, and decoded in packets on a worker thread
    // and played through an AudioWorklet, so none of that work runs on
    // the main (game loop) thread. Creating and loading one is free —
    // all I/O and decoding is deferred to Prepare or Play, and released
    // again by Stop.

    [JSImport("audio.createAudioStream", ModuleName)]
    public static partial JSObject CreateAudioStream();

    [JSImport("audio.destroyAudioStream", ModuleName)]
    public static partial void DestroyAudioStream(JSObject audioStream);

    /// <summary>
    /// Associates the stream with its content URL and the channel count
    /// recorded in the content manifest. Performs no I/O, so this is safe
    /// to call for every music track at load time.
    /// </summary>
    [JSImport("audio.loadAudioStreamFromContent", ModuleName)]
    public static partial void LoadAudioStreamFromContent(JSObject audioStream, string filePath, int channelCount);

    [JSImport("audio.getAudioStreamDuration", ModuleName)]
    public static partial Task<double> GetAudioStreamDuration(JSObject audioStream);

    [JSImport("audio.prepareAudioStream", ModuleName)]
    public static partial Task PrepareAudioStream(JSObject audioStream, double? startSeconds, double? endSeconds, double? loopPointSeconds, bool loop);

    [JSImport("audio.unprepareAudioStream", ModuleName)]
    public static partial void UnprepareAudioStream(JSObject audioStream);

    [JSImport("audio.playAudioStream", ModuleName)]
    public static partial void PlayAudioStream(JSObject audioStream, double? startSeconds, double? endSeconds, double? loopPointSeconds, bool loop);

    [JSImport("audio.stopAudioStream", ModuleName)]
    public static partial void StopAudioStream(JSObject audioStream);

    [JSImport("audio.pauseAudioStream", ModuleName)]
    public static partial void PauseAudioStream(JSObject audioStream);

    /// <summary>
    /// Returns the stream's lifecycle state as the numeric value of
    /// <see cref="DotGame.Framework.Platform.Audio.MusicState"/>. The JS
    /// side tracks this as a plain field, so this is a cheap read and
    /// safe to poll every frame.
    /// </summary>
    [JSImport("audio.getAudioStreamState", ModuleName)]
    public static partial int GetAudioStreamState(JSObject audioStream);

    [JSImport("audio.isAudioStreamPlaying", ModuleName)]
    public static partial bool IsAudioStreamPlaying(JSObject audioStream);

    [JSImport("audio.isAudioStreamPaused", ModuleName)]
    public static partial bool IsAudioStreamPaused(JSObject audioStream);

    [JSImport("audio.setAudioStreamVolumeControl", ModuleName)]
    public static partial void SetAudioStreamVolumeControl(JSObject audioStream, JSObject? volumeControl);

    // VolumeControl bindings

    [JSImport("audio.createVolumeControl", ModuleName)]
    public static partial JSObject CreateVolumeControl();

    [JSImport("audio.getMasterVolumeControl", ModuleName)]
    public static partial JSObject GetMasterVolumeControl();

    [JSImport("audio.setVolumeControlParent", ModuleName)]
    public static partial void SetVolumeControlParent(JSObject volumeControl, JSObject? parent);

    [JSImport("audio.destroyVolumeControl", ModuleName)]
    public static partial void DestroyVolumeControl(JSObject volumeControl);

    [JSImport("audio.getVolume", ModuleName)]
    public static partial float GetVolume(JSObject volumeControl);

    [JSImport("audio.setVolume", ModuleName)]
    public static partial void SetVolume(JSObject volumeControl, float volume);

    /// <summary>
    /// Schedules a fade whose shape is given by <paramref name="samples"/>:
    /// evenly spaced volume values, the first being the volume the fade starts
    /// from and the last the volume it ends on. The curve is evaluated managed
    /// side (see <see cref="DotGame.Framework.Audio.FadeCurve"/>) so
    /// that custom curves work in the browser as well.
    /// </summary>
    /// <remarks>
    /// Marshaled as a memory view, which hands JavaScript a direct view over the
    /// managed buffer instead of copying and boxing every element into a JS
    /// array -- that is what lets the caller reuse a single sample buffer across
    /// fades. The view is only valid for the duration of the call, so the JS side
    /// copies out of it before handing the curve to Web Audio, which retains it
    /// for the whole fade. The samples are <see langword="double"/> rather than
    /// <see langword="float"/> only because a memory view supports byte, int, and
    /// double.
    /// </remarks>
    [JSImport("audio.fadeVolume", ModuleName)]
    public static partial void FadeVolume(
        JSObject volumeControl,
        double durationSeconds,
        [JSMarshalAs<JSType.MemoryView>] Span<double> samples);
}
