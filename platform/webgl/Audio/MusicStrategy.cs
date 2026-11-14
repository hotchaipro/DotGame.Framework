// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.WebGL.Audio;

[SupportedOSPlatform("browser")]
internal sealed class MusicStrategy : IMusicStrategy, IDisposable
{
    private JSObject? _nativeHandle;
    private readonly TimeSpan _duration;
    private readonly string? _name;

    public MusicStrategy(
        JSObject nativeHandle,
        TimeSpan duration,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(nativeHandle);

        this._nativeHandle = nativeHandle;
        this._duration = duration;
        this._name = name;
    }

    // Deliberately no finalizer. Every member below crosses the JS interop
    // boundary, and a finalizer may only run after the JSObject it depends on
    // has itself been finalized (finalization order is unordered), at which
    // point the interop call throws. An exception escaping a finalizer cannot
    // be routed through FatalError -- it aborts the WASM runtime outright, and
    // the host reloads the page with no crash report. JSObject already owns
    // and releases the underlying JS reference, so omitting the finalizer
    // leaks nothing: the worst case for a MusicStrategy that is never disposed is
    // that the JS-side AudioStream record is dropped without an explicit
    // destroyAudioStream, which JSObject's own finalizer makes collectable.

    internal string? Name => this._name;

    public JSObject NativeHandle => this._nativeHandle ?? throw new ObjectDisposedException(nameof(MusicStrategy));

    public TimeSpan Duration => this._duration;

    /// <summary>
    /// The lifecycle state of this track. The browser backend runs the
    /// same state machine as the native platforms, in JavaScript, so
    /// this is a plain field read across the interop boundary.
    /// </summary>
    public MusicState State
    {
        get
        {
            var nativeHandle = this._nativeHandle;
            return (nativeHandle is null)
                ? MusicState.Unloaded
                : (MusicState)AudioBindings.GetAudioStreamState(nativeHandle);
        }
    }

    /// <inheritdoc />
    public bool IsPlaying
    {
        get
        {
            var nativeHandle = this._nativeHandle;
            return (nativeHandle is not null) && AudioBindings.IsAudioStreamPlaying(nativeHandle);
        }
    }

    /// <inheritdoc />
    public bool IsPaused
    {
        get
        {
            var nativeHandle = this._nativeHandle;
            return (nativeHandle is not null) && AudioBindings.IsAudioStreamPaused(nativeHandle);
        }
    }

    public Strategy<IVolumeControlStrategy>? VolumeControl
    {
        set
        {
            var nativeHandle = value?.ToConcrete<VolumeControlStrategy>().NativeHandle;
            AudioBindings.SetAudioStreamVolumeControl(this.NativeHandle, nativeHandle);
        }
    }

    /// <inheritdoc />
    public void Play(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop)
    {
        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3de, $"[SoundTrace][Web] MusicStrategy.Play: '{this.Name}' start={start?.TotalSeconds:F3} end={end?.TotalSeconds:F3} loop={loop}");
        }

        AudioBindings.PlayAudioStream(
            this.NativeHandle,
            start?.TotalSeconds,
            end?.TotalSeconds,
            loopPoint?.TotalSeconds,
            loop: loop);
    }

    /// <inheritdoc />
    public Task Prepare(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop)
    {
        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3df, $"[SoundTrace][Web] MusicStrategy.Prepare: '{this.Name}' start={start?.TotalSeconds:F3} end={end?.TotalSeconds:F3} loop={loop}");
        }

        return AudioBindings.PrepareAudioStream(
            this.NativeHandle,
            start?.TotalSeconds,
            end?.TotalSeconds,
            loopPoint?.TotalSeconds,
            loop: loop);
    }

    /// <inheritdoc />
    public void Unprepare()
    {
        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3e0, $"[SoundTrace][Web] MusicStrategy.Unprepare: '{this.Name}'");
        }

        AudioBindings.UnprepareAudioStream(this.NativeHandle);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3e1, $"[SoundTrace][Web] MusicStrategy.Stop: '{this.Name}'");
        }

        AudioBindings.StopAudioStream(this.NativeHandle);
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (FeatureSwitches.TraceAudio)
        {
            Trace.Write(0x6c19a3e2, $"[SoundTrace][Web] MusicStrategy.Pause: '{this.Name}'");
        }

        AudioBindings.PauseAudioStream(this.NativeHandle);
    }

    public void Dispose()
    {
        var nativeHandle = this._nativeHandle;
        if (nativeHandle is null)
        {
            return;
        }

        // Clear the field first so a failure below cannot leave this instance
        // repeatedly re-entering teardown against a half-destroyed handle.
        this._nativeHandle = null;

        Exception? error = null;
        try
        {
            if (FeatureSwitches.TraceAudio)
            {
                Trace.Write(0x6c19a3db, $"[SoundTrace][Web] MusicStrategy.Dispose (destroy AudioStream): '{this._name}'");
            }
            AudioBindings.DestroyAudioStream(nativeHandle);
        }
        catch (Exception e)
        {
            error = e;
        }

        try
        {
            nativeHandle.Dispose();
        }
        catch (Exception e)
        {
            error ??= e;
        }

        if (error is not null)
        {
            // Dispose runs on the game thread, so unlike the removed finalizer
            // this can be reported properly instead of aborting the runtime.
            GameHost.Terminate(0x3a71c8e5, error);
        }
    }
}
