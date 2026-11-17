// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using DotGame.Framework.Platform.Common.Audio;

namespace DotGame.Framework.Desktop.Audio;

/// <summary>
/// The one SDL stream every sound effect plays through: float stereo at
/// <see cref="SampleRate"/>, connected to the default playback device and
/// fed by <see cref="AudioMixer"/> on a dedicated audio thread.
/// </summary>
/// <remarks>
/// <para>
/// The thread mixes one burst (5 ms) at a time and keeps the stream holding what the device takes
/// at once plus a burst, checking every 2 ms. If the stream is found empty while a voice plays (a
/// collection, a busy machine) it keeps a burst more from then on, up to a limit. After 15 seconds
/// with nothing playing the thread pauses the stream and parks, so an idle game uses no CPU; the
/// next voice to play wakes it.
/// </para>
/// <para>
/// A thread of the framework's own and not the stream's get callback, as for music: the callback
/// runs on SDL's audio thread holding the device's lock, so a collection that suspended it would
/// hold up the music's stream and any game thread calling SDL's audio, and an exception in it
/// would end the process. The price is the burst kept queued.
/// </para>
/// <para>
/// With no device to play on (none attached, or it can't be opened) the thread still mixes, at
/// about the pace a device would and to nowhere, so that voices finish when they would have and
/// <c>IsPlaying</c> turns false; it tries the device again every second.
/// </para>
/// <para>
/// Every call on the stream happens on the audio thread. The game thread only flips flags and,
/// when the thread is parked, pulses it. See <c>docs/architecture/audio.md</c>.
/// </para>
/// </remarks>
internal sealed class AudioOutput
{
    /// <summary>
    /// The rate the mixer runs at, which sounds are converted to at load. The content pipeline
    /// builds sounds at this rate, so that's usually no conversion; SDL converts the stream to
    /// the device's own rate if it differs, and goes on doing so when the default device changes.
    /// </summary>
    internal const int SampleRate = 48000;

    private const int BurstFrames = 240;
    private const int BytesPerFrame = 2 * sizeof(float);
    private const ulong PollNanoseconds = 2_000_000;
    private const ulong BurstNanoseconds = 1_000_000_000UL * BurstFrames / SampleRate;

    // What a device is taken to read at once when it won't say
    private const int DefaultDeviceFrames = 1024;

    // How many bursts beyond the device's own buffer the stream may come to hold
    private const int MaxExtraBursts = 16;

    // NOTE: Long enough that a click in a slow-paced game doesn't find the output asleep
    private const int IdleSeconds = 15;

    // How long after the device fails to open it's tried again
    private const int RetryMilliseconds = 1000;

    private readonly AudioMixer _mixer;
    // A monitor rather than a Lock: parking waits on it
    private readonly object _parkLock = new();
    private volatile Thread? _thread;
    private volatile bool _isParked;
    private volatile bool _isShutDown;
    private bool _wakePending;

    internal AudioOutput(
        AudioMixer mixer)
    {
        this._mixer = mixer;
        mixer.SetVoiceStartedHandler(this.Wake);
    }

    /// <summary>
    /// Ends the audio thread, which closes the stream, and waits for it. Call before SDL quits.
    /// Sounds played after this are silent and never finish.
    /// </summary>
    internal void Shutdown()
    {
        Thread? thread;
        lock (this._parkLock)
        {
            this._isShutDown = true;
            thread = this._thread;
            Monitor.PulseAll(this._parkLock);
        }

        thread?.Join(TimeSpan.FromSeconds(1));
    }

    private void Wake()
    {
        // Published before the parked check, and the thread clears it before each mix and checks
        // it before parking, so a voice that starts while the thread is deciding to park is never
        // missed
        Volatile.Write(ref this._wakePending, true);
        Interlocked.MemoryBarrier();

        if ((this._thread is not null) && !this._isParked)
        {
            return;
        }

        lock (this._parkLock)
        {
            if (this._isShutDown)
            {
                return;
            }

            if (this._thread is null)
            {
                this._thread = new Thread(this.ThreadMain)
                {
                    IsBackground = true,
                    Name = "DotGame Audio Mixer",
                    Priority = ThreadPriority.Highest,
                };
                this._thread.Start();
                return;
            }

            Monitor.Pulse(this._parkLock);
        }
    }

    private void ThreadMain()
    {
        nint stream = nint.Zero;
        try
        {
            this.Run(ref stream);
        }
        catch (Exception ex)
        {
            Trace.Write(0x41d86b3e, "[AudioOutput] The audio thread failed; trying again at the next sound.", ex);
        }
        finally
        {
            if (stream != nint.Zero)
            {
                SDL.DestroyAudioStream(stream);
            }

            // The next sound starts a new thread
            lock (this._parkLock)
            {
                this._thread = null;
                this._isParked = false;
            }
        }
    }

    private void Run(
        ref nint stream)
    {
        // NOTE: Pinned, as SDL copies from its address each burst
        var buffer = GC.AllocateArray<float>(BurstFrames * 2, pinned: true);
        nint address;
        unsafe
        {
            fixed (float* pointer = buffer)
            {
                address = (nint)pointer;
            }
        }

        const int IdleLimit = SampleRate * IdleSeconds;
        int idleFrames = 0;
        int deviceFrames = DefaultDeviceFrames;
        int extraBursts = 1;
        bool wasPlaying = false;
        long retryAtTicks = 0;

        while (!this._isShutDown)
        {
            if (idleFrames >= IdleLimit)
            {
                if (stream != nint.Zero)
                {
                    SDL.PauseAudioStreamDevice(stream);
                    SDL.ClearAudioStream(stream);
                }

                this.Park();
                if (this._isShutDown)
                {
                    return;
                }

                idleFrames = 0;
                wasPlaying = false;
                if (stream != nint.Zero)
                {
                    // (The default device may have changed while this was parked)
                    deviceFrames = QueryDeviceFrames(stream);
                    SDL.ResumeAudioStreamDevice(stream);
                }

                continue;
            }

            if ((stream == nint.Zero) && (Environment.TickCount64 >= retryAtTicks))
            {
                stream = OpenStream();
                retryAtTicks = Environment.TickCount64 + RetryMilliseconds;
                if (stream != nint.Zero)
                {
                    deviceFrames = QueryDeviceFrames(stream);
                    SDL.ResumeAudioStreamDevice(stream);
                    Trace.Write(0x58c02e7b, $"[AudioOutput] Sound output open; the device reads {deviceFrames} frames at a time.");
                }
            }

            Volatile.Write(ref this._wakePending, false);

            if (stream == nint.Zero)
            {
                // No device: mix to nowhere at a device's pace, so voices finish
                wasPlaying = this._mixer.Mix(buffer, BurstFrames);
                idleFrames = wasPlaying ? 0 : idleFrames + BurstFrames;
                SDL.DelayNS(BurstNanoseconds);
                continue;
            }

            int queuedBytes = SDL.GetAudioStreamQueued(stream);
            if (queuedBytes < 0)
            {
                Trace.Write(0x7c15e9a2, $"[AudioOutput] The stream failed ({SDL.GetError()}); opening it again.");
                SDL.DestroyAudioStream(stream);
                stream = nint.Zero;
                continue;
            }

            // Found empty while a voice played: the device ran out, so keep more from now on
            if ((queuedBytes == 0) && wasPlaying && (extraBursts < MaxExtraBursts))
            {
                extraBursts++;
                Trace.Write(0x1f7a94c6, $"[AudioOutput] The stream ran out; keeping {extraBursts} bursts beyond the device's {deviceFrames} frames.");
            }

            int queuedFrames = queuedBytes / BytesPerFrame;
            int targetFrames = deviceFrames + (extraBursts * BurstFrames);
            while (queuedFrames < targetFrames)
            {
                wasPlaying = this._mixer.Mix(buffer, BurstFrames);
                idleFrames = wasPlaying ? 0 : idleFrames + BurstFrames;

                if (!SDL.PutAudioStreamData(stream, address, BurstFrames * BytesPerFrame))
                {
                    Trace.Write(0x2be6f074, $"[AudioOutput] Writing to the stream failed ({SDL.GetError()}); opening it again.");
                    SDL.DestroyAudioStream(stream);
                    stream = nint.Zero;
                    break;
                }

                queuedFrames += BurstFrames;
            }

            SDL.DelayNS(PollNanoseconds);
        }
    }

    private void Park()
    {
        lock (this._parkLock)
        {
            this._isParked = true;
            Interlocked.MemoryBarrier();

            while (!Volatile.Read(ref this._wakePending) && !this._isShutDown)
            {
                Monitor.Wait(this._parkLock);
            }

            this._isParked = false;
        }
    }

    private static nint OpenStream()
    {
        var spec = new SDL.AudioSpec
        {
            Freq = SampleRate,
            Format = SDL.AudioFormat.AudioF32LE,
            Channels = 2,
        };

        nint stream = SDL.OpenAudioDeviceStream(SDL.AudioDeviceDefaultPlayback, in spec, null, nint.Zero);
        if (stream == nint.Zero)
        {
            Trace.Write(0x65a3c81d, $"[AudioOutput] Couldn't open the playback device ({SDL.GetError()}); sounds are silent until it opens.");
        }

        return stream;
    }

    // How many of the mixer's frames the device reads from the stream at once, which is the least
    // the stream can hold without the device ever finding it short
    private static int QueryDeviceFrames(
        nint stream)
    {
        uint device = SDL.GetAudioStreamDevice(stream);
        if ((device != 0)
            && SDL.GetAudioDeviceFormat(device, out var spec, out int sampleFrames)
            && (spec.Freq > 0)
            && (sampleFrames > 0))
        {
            return (int)(((long)sampleFrames * SampleRate) + spec.Freq - 1) / spec.Freq;
        }

        return DefaultDeviceFrames;
    }
}
