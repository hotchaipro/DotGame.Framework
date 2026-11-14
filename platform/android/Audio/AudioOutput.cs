// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using AndroidApp = global::Android.App.Application;

namespace DotGame.Framework.Android.Audio;

/// <summary>
/// The one <c>AudioTrack</c> every sound effect plays through: a
/// low-latency <c>MODE_STREAM</c> float stereo track at the device's
/// native rate, fed by <see cref="AudioMixer"/> on a dedicated
/// audio thread.
/// </summary>
/// <remarks>
/// <para>
/// The thread mixes and writes one burst (the device's native buffer
/// size) at a time, keeping two bursts queued; if the track reports
/// underruns (a GC pause, a busy device) the queue grows by a burst,
/// up to the track's capacity. After 15 seconds with nothing playing
/// the thread pauses the track and parks, so an idle game uses no CPU;
/// the next voice to play wakes it.
/// </para>
/// <para>
/// Every <c>AudioTrack</c> call happens on the audio thread. The game
/// thread only flips flags and, when the thread is parked, pulses it.
/// See <c>docs/architecture/audio.md</c>.
/// </para>
/// </remarks>
internal sealed class AudioOutput
{
    private const int DefaultSampleRate = 48000;
    private const int DefaultFramesPerBurst = 192;
    private const int MinFramesPerBurst = 64;
    // NOTE: Long enough that a tap in a slow-paced game doesn't find the output asleep, since a
    // silent track costs little next to a game drawing frames
    private const int IdleSeconds = 15;

    // How long after the output fails it's tried again, as creating a track commonly fails during
    // focus and routing changes
    private const int RetryMilliseconds = 1000;

    // How often to ask the track about underruns, a call into Java
    private const int UnderrunCheckBursts = 16;

    private static int s_sampleRate;
    private static int s_framesPerBurst;

    private readonly AudioMixer _mixer;
    // A monitor rather than a Lock: parking waits on it
    private readonly object _parkLock = new();
    private volatile Thread? _thread;
    private volatile bool _isParked;
    private volatile bool _isSuspended;
    private volatile bool _isShutDown;
    private bool _wakePending;
    private long _retryAfterTicks;

    internal AudioOutput(
        AudioMixer mixer)
    {
        this._mixer = mixer;
        mixer.SetVoiceStartedHandler(this.Wake);
    }

    /// <summary>
    /// The device's native output rate, which sounds are converted to
    /// at load. Safe to read from any thread.
    /// </summary>
    internal static int SampleRate
    {
        get
        {
            if (s_sampleRate == 0)
            {
                QueryDevice();
            }

            return s_sampleRate;
        }
    }

    private static int FramesPerBurst
    {
        get
        {
            if (s_framesPerBurst == 0)
            {
                QueryDevice();
            }

            return s_framesPerBurst;
        }
    }

    /// <summary>
    /// Pauses output while the activity is in the background.
    /// </summary>
    internal void Suspend()
    {
        this._isSuspended = true;
    }

    /// <summary>
    /// Ends the audio thread, which releases the track. Sounds played after this are silent.
    /// </summary>
    internal void Shutdown()
    {
        lock (this._parkLock)
        {
            this._isShutDown = true;
            Monitor.PulseAll(this._parkLock);
        }
    }

    internal void Resume()
    {
        this._isSuspended = false;
        if (this._thread is not null)
        {
            this.Wake();
        }
    }

    private void Wake()
    {
        // Published before the parked check, and the thread clears it
        // before each mix and checks it before parking, so a voice that
        // starts while the thread is deciding to park is never missed
        Volatile.Write(ref this._wakePending, true);
        Interlocked.MemoryBarrier();

        if ((this._thread is not null) && !this._isParked)
        {
            return;
        }

        lock (this._parkLock)
        {
            if (this._thread is null)
            {
                if (this._isSuspended || this._isShutDown || (Environment.TickCount64 < this._retryAfterTicks))
                {
                    return;
                }

                this._thread = new Thread(this.ThreadMain)
                {
                    IsBackground = true,
                    Name = "DotGame Audio Mixer",
                };
                this._thread.Start();
                return;
            }

            Monitor.Pulse(this._parkLock);
        }
    }

    private void ThreadMain()
    {
        global::Android.OS.Process.SetThreadPriority(
            global::Android.OS.ThreadPriority.UrgentAudio);

        global::Android.Media.AudioTrack? track = null;
        try
        {
            int sampleRate = SampleRate;
            int burst = FramesPerBurst;
            track = CreateTrack(sampleRate, burst);
            if (track is null)
            {
                return;
            }

            this.Run(track, sampleRate, burst);
        }
        catch (Exception ex)
        {
            Trace.Write(0x5be2d4a1, "[AudioOutput] The audio thread failed; trying again at the next sound.", ex);
        }
        finally
        {
            if (track is not null)
            {
                try
                {
                    track.Release();
                }
                catch
                {
                    // Ignored: the track may already be torn down
                }

                track.Dispose();
            }

            // The next sound tries again, shortly
            lock (this._parkLock)
            {
                this._thread = null;
                this._isParked = false;
                this._retryAfterTicks = Environment.TickCount64 + RetryMilliseconds;
            }
        }
    }

    private void Run(
        global::Android.Media.AudioTrack track,
        int sampleRate,
        int burst)
    {
        var buffer = new float[burst * 2];
        int byteCount = buffer.Length * sizeof(float);

        // NOTE: Writing a float[] would copy it through a new Java array each burst, about 375 KB/s of
        // garbage on the most timing-sensitive thread. A direct buffer's memory is written in place.
        using var directBuffer = Java.Nio.ByteBuffer.AllocateDirect(byteCount)!;
        using var nativeOrder = Java.Nio.ByteOrder.NativeOrder()!;
        directBuffer.Order(nativeOrder);
        nint address = directBuffer.GetDirectBufferAddress();

        int idleLimit = sampleRate * IdleSeconds;
        int idleFrames = 0;
        int underruns = 0;
        int untilUnderrunCheck = UnderrunCheckBursts;
        bool canTune = OperatingSystem.IsAndroidVersionAtLeast(24);

        track.Play();

        while (!this._isShutDown)
        {
            if (this._isSuspended || (idleFrames >= idleLimit))
            {
                track.Pause();
                track.Flush();
                this.Park();
                if (this._isShutDown)
                {
                    return;
                }

                idleFrames = 0;
                track.Play();
                continue;
            }

            Volatile.Write(ref this._wakePending, false);
            if (this._mixer.Mix(buffer, burst))
            {
                idleFrames = 0;
            }
            else
            {
                idleFrames += burst;
            }

            unsafe
            {
                new ReadOnlySpan<float>(buffer).CopyTo(new Span<float>((void*)address, buffer.Length));
            }

            int offset = 0;
            while (offset < byteCount)
            {
                directBuffer.Position(offset);
                int written = track.Write(
                    directBuffer,
                    byteCount - offset,
                    global::Android.Media.WriteMode.Blocking);
                if (written < 0)
                {
                    Trace.Write(0x2f9c61e8, $"[AudioOutput] AudioTrack.Write failed ({written}); trying again at the next sound.");
                    return;
                }

                offset += written;
            }

            if (canTune && (--untilUnderrunCheck == 0))
            {
                untilUnderrunCheck = UnderrunCheckBursts;
                underruns = GrowOnUnderrun(track, burst, underruns);
            }
        }
    }

    // Grows the track's queue by a burst whenever it has underrun since
    // the last check, up to its capacity
    [System.Runtime.Versioning.SupportedOSPlatform("android24.0")]
    private static int GrowOnUnderrun(
        global::Android.Media.AudioTrack track,
        int burst,
        int underruns)
    {
        int count = track.UnderrunCount;
        if (count > underruns)
        {
            int size = track.BufferSizeInFrames + burst;
            if (size <= track.BufferCapacityInFrames)
            {
                track.SetBufferSizeInFrames(size);
            }
        }

        return count;
    }

    private void Park()
    {
        lock (this._parkLock)
        {
            this._isParked = true;
            Interlocked.MemoryBarrier();

            while ((!Volatile.Read(ref this._wakePending) || this._isSuspended) && !this._isShutDown)
            {
                Monitor.Wait(this._parkLock);
            }

            this._isParked = false;
        }
    }

    private static global::Android.Media.AudioTrack? CreateTrack(
        int sampleRate,
        int burst)
    {
        var attributes = new global::Android.Media.AudioAttributes.Builder()
            .SetUsage(global::Android.Media.AudioUsageKind.Game)!
            .SetContentType(global::Android.Media.AudioContentType.Sonification)!
            .Build()!;

        var format = new global::Android.Media.AudioFormat.Builder()
            .SetEncoding(global::Android.Media.Encoding.PcmFloat)!
            .SetSampleRate(sampleRate)!
            .SetChannelMask(global::Android.Media.ChannelOut.Stereo)!
            .Build()!;

        // Room for eight bursts, so underruns have space to grow into;
        // the queue starts at two
        const int BytesPerFrame = 2 * sizeof(float);
        int minimum = global::Android.Media.AudioTrack.GetMinBufferSize(
            sampleRate,
            global::Android.Media.ChannelOut.Stereo,
            global::Android.Media.Encoding.PcmFloat);
        int capacity = Math.Max(minimum, burst * 8 * BytesPerFrame);

        var builder = new global::Android.Media.AudioTrack.Builder()
            .SetAudioAttributes(attributes)!
            .SetAudioFormat(format)!
            .SetBufferSizeInBytes(capacity)!
            .SetTransferMode(global::Android.Media.AudioTrackMode.Stream)!;

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            builder = builder.SetPerformanceMode(
                global::Android.Media.AudioTrackPerformanceMode.LowLatency)!;
        }

        try
        {
            var track = builder.Build()!;
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                track.SetBufferSizeInFrames(burst * 2);
            }

            return track;
        }
        catch (Exception ex)
        {
            Trace.Write(0x6a07c3f5, "[AudioOutput] Failed to create the AudioTrack; trying again at the next sound.", ex);
            return null;
        }
        finally
        {
            attributes.Dispose();
            format.Dispose();
        }
    }

    private static void QueryDevice()
    {
        int sampleRate = DefaultSampleRate;
        int framesPerBurst = DefaultFramesPerBurst;

        var manager = AndroidApp.Context.GetSystemService(
            global::Android.Content.Context.AudioService) as global::Android.Media.AudioManager;
        if (manager is not null)
        {
            if (int.TryParse(manager.GetProperty(global::Android.Media.AudioManager.PropertyOutputSampleRate), out int rate)
                && (rate > 0))
            {
                sampleRate = rate;
            }

            if (int.TryParse(manager.GetProperty(global::Android.Media.AudioManager.PropertyOutputFramesPerBuffer), out int frames)
                && (frames > 0))
            {
                framesPerBurst = Math.Max(frames, MinFramesPerBurst);
            }
        }

        s_framesPerBurst = framesPerBurst;
        Volatile.Write(ref s_sampleRate, sampleRate);
    }
}
