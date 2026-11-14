// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotGame.Framework.Audio;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.WebGL.Audio;

[SupportedOSPlatform("browser")]
internal sealed class VolumeControlStrategy : IVolumeControlStrategy, IDisposable
{
    // Resolution of the fade shape handed to Web Audio. The browser
    // interpolates linearly between the samples, so this only has to be fine
    // enough that the straight segments are inaudible -- which, over a fade
    // measured in seconds, 128 points comfortably is.
    private const int FadeCurveSamples = 128;

    private JSObject? _nativeHandle;

    // Reused across fades: the array is copied into a Float32Array on the JS
    // side, so nothing retains it after the call returns.
    private double[]? _fadeSamples;

    public VolumeControlStrategy(
        JSObject nativeHandle)
    {
        ArgumentNullException.ThrowIfNull(nativeHandle);

        this._nativeHandle = nativeHandle;
    }

    // Deliberately no finalizer -- see the note in MusicStrategy. Interop from a
    // finalizer can throw once the JSObject has already been finalized, and an
    // exception escaping a finalizer aborts the WASM runtime (observed as the
    // browser page reloading) instead of surfacing through FatalError.

    public JSObject NativeHandle => this._nativeHandle ?? throw new ObjectDisposedException(nameof(VolumeControlStrategy));

    public Strategy<IVolumeControlStrategy>? Parent
    {
        set => AudioBindings.SetVolumeControlParent(this.NativeHandle, value?.ToConcrete<VolumeControlStrategy>().NativeHandle);
    }

    public float Volume
    {
        get => AudioBindings.GetVolume(this.NativeHandle);
        set => AudioBindings.SetVolume(this.NativeHandle, value);
    }

    // Web Audio schedules a fade ahead of time rather than being polled, so the
    // shape cannot be evaluated frame by frame the way the other platforms do
    // it. The curve is therefore sampled here, in managed code, and the samples
    // are scheduled as a value curve on the gain node. That keeps a single
    // implementation of every shape -- including a game's own custom curve,
    // which the browser could not otherwise evaluate at all.
    public void FadeTo(
        float toVolume,
        TimeSpan duration,
        FadeCurve curve)
    {
        var nativeHandle = this.NativeHandle;

        if (duration <= TimeSpan.Zero)
        {
            AudioBindings.SetVolume(nativeHandle, toVolume);
            return;
        }

        var samples = this._fadeSamples ??= new double[FadeCurveSamples];

        // The fade starts from the gain the node actually has right now, which
        // may itself be part-way through a previous fade.
        float fromVolume = AudioBindings.GetVolume(nativeHandle);

        Span<float> shape = stackalloc float[FadeCurveSamples];
        curve.Sample(fromVolume, toVolume, shape);

        for (int i = 0; i < FadeCurveSamples; i++)
        {
            samples[i] = shape[i];
        }

        AudioBindings.FadeVolume(nativeHandle, duration.TotalSeconds, samples);
    }

    public void Dispose()
    {
        var nativeHandle = this._nativeHandle;
        if (nativeHandle is null)
        {
            return;
        }

        this._nativeHandle = null;

        try
        {
            AudioBindings.DestroyVolumeControl(nativeHandle);

            nativeHandle.Dispose();
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x5c04b7a9, e);
        }
    }
}
