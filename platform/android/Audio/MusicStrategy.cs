// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;

namespace DotGame.Framework.Android.Audio;

/// <summary>
/// Android music track. In the
/// <see cref="Framework.Platform.Audio.MusicState.Loaded"/> state this holds
/// only the decoder and metadata; the direct PCM buffer, the decoder's copy
/// of the file, the <see cref="global::Android.Media.AudioTrack"/>, and the refill
/// thread are created by <see cref="OnAcquireAsync"/> and torn down by
/// <see cref="OnReleaseAsync"/>.
/// </summary>
/// <remarks>
/// Both of those run on the base class's background operation queue,
/// so neither <c>AudioTrack</c> construction nor the blocking thread
/// join ever runs on the game thread or an activity lifecycle
/// callback, where they would risk an ANR.
/// </remarks>
internal sealed class MusicStrategy : MusicStrategyBase
{
    // Decode buffer: 4800 samples per channel is 100 ms at 48 kHz
    private const int DecodeSamplesPerChannel = 4800;

    // AudioTrack streaming buffer = decode chunk * this multiplier.
    // Four chunks (400 ms) gives the refill thread enough
    // headroom to survive GC pauses (10-30ms typical on low-end devices)
    // and OS scheduling jitter without causing audible underruns.
    private const int AudioTrackBufferMultiplier = 4;

    private readonly OpusMusicReader _decoder;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly TimeSpan _duration;
    private readonly long _totalSamples;

    // Signalled while the refill thread should be pushing PCM. Lets the
    // thread be created during acquisition and parked until output
    // actually starts, so starting playback is a cheap flag flip.
    private readonly ManualResetEventSlim _renderGate = new(false);

    // Set while the refill thread is parked at the gate or gone, when it can't be using the
    // decoder or the track.
    private readonly ManualResetEventSlim _renderParked = new(false);

    // Resident only between OnAcquireAsync and OnReleaseAsync.
    private Java.Nio.ByteBuffer? _directBuffer;
    private nint _directBufferAddress;
    private global::Android.Media.AudioTrack? _audioTrack;
    private Thread? _playbackThread;

    private volatile bool _threadRunning;
    private bool _loop;
    private long _loopSamplePosition;
    private long _endSamplePosition;
    private Strategy<IVolumeControlStrategy>? _volumeControl;
    private float _lastSetVolume = -1f;   // sentinel: force set on first chunk

    internal MusicStrategy(
        OpusMusicReader decoder,
        int sampleRate,
        int channels,
        TimeSpan duration,
        IMusicPlaybackListener? listener)
        : base(listener)
    {
        this._decoder = decoder
            ?? throw new ArgumentNullException(nameof(decoder));
        this._sampleRate = sampleRate;
        this._channels = channels;
        this._duration = duration;

        // The decoder's frame count (the manifest's until the file is read), else the duration's
        long totalSamples = decoder.TotalFrames;
        if (totalSamples <= 0)
        {
            totalSamples = (long)(duration.TotalSeconds * sampleRate);
        }

        this._totalSamples = totalSamples;
        this._endSamplePosition = totalSamples;
    }

    public override TimeSpan Duration => this._duration;

    public override Strategy<IVolumeControlStrategy>? VolumeControl
    {
        set => this._volumeControl = value;
    }

    internal Strategy<IVolumeControlStrategy>? GetVolumeControl()
        => this._volumeControl;

    #region Resource acquisition

    /// <inheritdoc />
    protected override Task OnAcquireAsync(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop,
        CancellationToken cancellationToken)
    {
        // Validate the decoded audio format before touching the native
        // AudioTrack. A non-positive sample rate or channel count means the
        // decoder never produced a usable format (e.g. a failed/corrupt
        // decode), which AudioTrack would reject. The state machine handles
        // this as any failed acquire, rather than this track reporting
        // Prepared and then playing nothing.
        if (this._sampleRate <= 0 || this._channels <= 0)
        {
            throw new InvalidOperationException(
                $"[MusicStrategy] Invalid audio format ({this._sampleRate}Hz, {this._channels}ch).");
        }

        // NOTE: A stop re-primes a prepared track while the refill thread may still be decoding,
        // and the decoder isn't thread-safe
        this.ParkRendering();

        this.ConfigureRange(start, end, loopPoint, loop);

        if (this._directBuffer is null)
        {
            int s16BufferSize = DecodeSamplesPerChannel * this._channels * 2;
            var directBuffer = Java.Nio.ByteBuffer.AllocateDirect(s16BufferSize)!;

            this._directBuffer = directBuffer;
            this._directBufferAddress = directBuffer.GetDirectBufferAddress();
        }

        // SeekTo reads the file the first time and positions playback, so
        // starting output skips that on the critical path: it belongs on
        // the background queue.
        long startSample = start.HasValue
            ? (long)(start.Value.TotalSeconds * this._sampleRate)
            : 0L;

        this._decoder.SeekTo(startSample);

        // Force volume to be applied on the first chunk of playback.
        this._lastSetVolume = -1f;

        if (this._audioTrack is null)
        {
            if (!this.CreateAudioTrack())
            {
                return Task.CompletedTask;
            }
        }

        // A decoder fault ends the thread, so that a later Play can try again with a new one
        if ((this._playbackThread is null) || !this._playbackThread.IsAlive)
        {
            this._threadRunning = true;
            this._renderGate.Reset();
            this._renderParked.Reset();

            this._playbackThread = new Thread(this.PlaybackLoop)
            {
                IsBackground = true,
                Name = "MusicPlayback",
            };

            this._playbackThread.Start();
        }

        return Task.CompletedTask;
    }

    private const int PlayOutPollMilliseconds = 5;
    private const int PlayOutGraceMilliseconds = 250;

    // Frames written since the track's position was last zero, for PlayOut. Refill thread only,
    // except when it's parked (a flush zeroes both).
    private int _framesWritten;

    private void ParkRendering()
    {
        if (this._playbackThread is null)
        {
            return;
        }

        this._renderGate.Reset();

        // Pausing also ends a blocking write, and flushing drops what the thread wrote last
        var audioTrack = this._audioTrack;
        audioTrack?.Pause();

        this._renderParked.Wait();

        audioTrack?.Flush();
        this._framesWritten = 0;
    }

    private void ConfigureRange(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop)
    {
        if (end.HasValue)
        {
            long endSamples = (long)(end.Value.TotalSeconds * this._sampleRate);
            this._endSamplePosition = Math.Min(endSamples, this._totalSamples);
        }
        else
        {
            this._endSamplePosition = this._totalSamples;
        }

        this._loopSamplePosition = loopPoint.HasValue
            ? (long)(loopPoint.Value.TotalSeconds * this._sampleRate)
            : 0L;

        this._loop = loop;

        _ = start;
    }

    private bool CreateAudioTrack()
    {
        var channelMask = this._channels == 2
            ? global::Android.Media.ChannelOut.Stereo
            : global::Android.Media.ChannelOut.Mono;

        int minBufferSize = global::Android.Media.AudioTrack.GetMinBufferSize(
            this._sampleRate,
            channelMask,
            global::Android.Media.Encoding.Pcm16bit);

        // GetMinBufferSize returns a negative error code (Error /
        // ErrorBadValue) when the device cannot support this format.
        // Building an AudioTrack with such a format throws "Cannot create
        // AudioTrack", so skip instead of attempting a doomed allocation.
        if (minBufferSize <= 0)
        {
            Trace.Write(0x7d2c9a14,
                $"[MusicStrategy] Skipping playback: unsupported audio format " +
                $"({this._sampleRate}Hz, {this._channels}ch, " +
                $"minBuffer={minBufferSize}).");
            return false;
        }

        int bufferSize = Math.Max(minBufferSize,
            DecodeSamplesPerChannel * this._channels * 2 * AudioTrackBufferMultiplier);

        try
        {
            this._audioTrack = new global::Android.Media.AudioTrack.Builder()
                .SetAudioAttributes(
                    new global::Android.Media.AudioAttributes.Builder()
                    .SetUsage(global::Android.Media.AudioUsageKind.Game)!
                    .SetContentType(
                        global::Android.Media.AudioContentType.Music)!
                    .Build()!)
                .SetAudioFormat(
                    new global::Android.Media.AudioFormat.Builder()
                    .SetEncoding(global::Android.Media.Encoding.Pcm16bit)!
                    .SetSampleRate(this._sampleRate)!
                    .SetChannelMask(channelMask)!
                    .Build()!)
                .SetBufferSizeInBytes(bufferSize)
                .SetTransferMode(global::Android.Media.AudioTrackMode.Stream)
                .Build();

            return true;
        }
        catch (Exception ex)
        {
            // AudioTrack allocation can fail transiently (device
            // audio-resource exhaustion, audio-focus/routing transitions,
            // low-end hardware). A one-shot ident or background track
            // failing to start must not crash the game - log and degrade
            // to silence.
            Trace.Write(0x7d2c9a15, "[MusicStrategy] Failed to create AudioTrack.", ex);

            this.ReleaseAudioTrack();

            return false;
        }
    }

    /// <inheritdoc />
    protected override Task OnReleaseAsync()
    {
        // Runs on the background queue, so the thread join and the
        // native track teardown below cannot stall the game
        // thread or an activity lifecycle callback.
        this._threadRunning = false;
        this._renderGate.Set();

        var playbackThread = this._playbackThread;
        this._playbackThread = null;
        playbackThread?.Join();

        this._renderGate.Reset();

        this.ReleaseAudioTrack();

        // The decoder's copy of the file goes while the track is idle: the next acquisition reads
        // it again
        this._decoder.Release();

        var directBuffer = this._directBuffer;
        this._directBuffer = null;
        this._directBufferAddress = nint.Zero;
        directBuffer?.Dispose();

        if (this.IsDisposed)
        {
            // Final teardown: the asset itself is going away.
            this._renderGate.Dispose();
            this._renderParked.Dispose();
        }

        return Task.CompletedTask;
    }

    private void ReleaseAudioTrack()
    {
        // Use Interlocked.Exchange so concurrent teardown paths cannot
        // double-release.
        var track = Interlocked.Exchange(ref this._audioTrack, null);
        if (track is null)
        {
            return;
        }

        try
        {
            track.Stop();
        }
        catch
        {
            // Ignored: track may already be stopped.
        }

        track.Release();
        track.Dispose();
    }

    #endregion

    #region Output primitives

    /// <inheritdoc />
    protected override void OnStartOutput()
    {
        var audioTrack = this._audioTrack;
        if (audioTrack is null)
        {
            return;
        }

        audioTrack.Play();
        this._renderGate.Set();
    }

    /// <inheritdoc />
    protected override void OnResumeOutput()
    {
        var audioTrack = this._audioTrack;
        if (audioTrack is null)
        {
            return;
        }

        audioTrack.Play();
        this._renderGate.Set();
    }

    /// <inheritdoc />
    protected override void OnPauseOutput()
    {
        // Park the refill thread first so it is not blocked inside
        // AudioTrack.Write when the track pauses.
        this._renderGate.Reset();

        this._audioTrack?.Pause();
    }

    /// <inheritdoc />
    protected override void OnStopOutput()
    {
        this._renderGate.Reset();

        var audioTrack = this._audioTrack;
        if (audioTrack is null)
        {
            return;
        }

        try
        {
            audioTrack.Pause();
            audioTrack.Flush();
        }
        catch
        {
            // Ignored: the track may already be stopped or released.
        }
    }

    #endregion

    private void PlaybackLoop()
    {
        // Raise the OS thread priority so the scheduler pre-empts normal
        // work (UI, GC) rather than letting them starve the AudioTrack
        // refill loop. ThreadPriority.Audio maps to THREAD_PRIORITY_AUDIO
        // (-16), which is the same priority used by the Android audio HAL.
        global::Android.OS.Process.SetThreadPriority(
            global::Android.OS.ThreadPriority.Audio);

        // NOTE: An exception escaping this thread would end the process, for a decoder or codec
        // fault that should only end this track
        try
        {
            this.RenderUntilDone();
        }
        catch (Exception exception) when (this._threadRunning)
        {
            GameHost.ReportAudioFailure(0x58c2e7a4, exception);
            this.OnNaturalEnd();
        }
        catch (Exception exception)
        {
            // Released under the loop's feet, which is what stopping it asked for
            Trace.Write(0x1d94b6f0, exception: exception);
        }
        finally
        {
            this._renderParked.Set();
        }
    }

    private void WaitAtRenderGate()
    {
        // The gate is checked again after clearing _renderParked: ParkRendering clears the gate
        // before it waits, so either it sees the thread running or the thread sees the gate shut.
        do
        {
            this._renderParked.Set();
            this._renderGate.Wait();
            this._renderParked.Reset();
        }
        while (!this._renderGate.IsSet);
    }

    private void RenderUntilDone()
    {
        while (this._threadRunning)
        {
            // Parked while prepared-but-not-playing, and while paused.
            this.WaitAtRenderGate();

            if (!this._threadRunning)
            {
                break;
            }

            var audioTrack = this._audioTrack;
            var directBuffer = this._directBuffer;

            if (audioTrack is null || directBuffer is null)
            {
                break;
            }

            // Apply volume from the volume control, but only when the value
            // has actually changed to avoid a JNI call every decode chunk.
            float volume = PolledVolumeControlStrategy.ReadEffectiveVolume(this._volumeControl?.Interface as PolledVolumeControlStrategy);
            if (volume != this._lastSetVolume)
            {
                audioTrack.SetVolume(volume);
                this._lastSetVolume = volume;
            }

            int samplesToRead = DecodeSamplesPerChannel;

            // Clamp to end position
            if (this._decoder.FramePosition + samplesToRead
                > this._endSamplePosition)
            {
                samplesToRead = (int)(this._endSamplePosition
                    - this._decoder.FramePosition);
            }

            if (samplesToRead <= 0)
            {
                if (this._loop)
                {
                    this._decoder.SeekTo(this._loopSamplePosition);
                    continue;
                }

                // The thread stays, parked, for a prepared track to play again
                if (this.PlayOut(audioTrack))
                {
                    this.OnNaturalEnd();
                }

                continue;
            }

            // The native decoder produces interleaved S16LE PCM directly,
            // so we read straight into the direct ByteBuffer's native
            // memory with no float conversion, managed allocation, or JNI
            // array marshaling.
            int bytesToRead = samplesToRead * this._channels * 2;

            int bytesRead;
            unsafe
            {
                bytesRead = this._decoder.ReadPcm16(
                    new Span<byte>(
                        (void*)this._directBufferAddress,
                        bytesToRead));
            }

            if (bytesRead <= 0)
            {
                if (this._loop)
                {
                    this._decoder.SeekTo(this._loopSamplePosition);
                    continue;
                }

                // The thread stays, parked, for a prepared track to play again
                if (this.PlayOut(audioTrack))
                {
                    this.OnNaturalEnd();
                }

                continue;
            }

            // Reset buffer position for AudioTrack.Write, which
            // reads from position and advances it after each write.
            directBuffer.Clear();

            int written = audioTrack.Write(
                directBuffer, bytesRead,
                global::Android.Media.WriteMode.Blocking);
            if (written > 0)
            {
                this._framesWritten += written / (this._channels * 2);
            }
        }
    }

    // Waits until the track has played what was written to it, as the decoder finishes up to
    // most of a second before the listener hears the end, and ending there would cut it off. A
    // pause or stop interrupts this: returns whether the end was reached.
    private bool PlayOut(
        global::Android.Media.AudioTrack audioTrack)
    {
        int remaining = this._framesWritten - audioTrack.PlaybackHeadPosition;
        long deadline = Environment.TickCount64 + PlayOutGraceMilliseconds
            + ((Math.Max(remaining, 0) * 1000L) / this._sampleRate);

        while (audioTrack.PlaybackHeadPosition < this._framesWritten)
        {
            if (!this._threadRunning || !this._renderGate.IsSet)
            {
                return false;
            }

            if (Environment.TickCount64 > deadline)
            {
                break;
            }

            Thread.Sleep(PlayOutPollMilliseconds);
        }

        return this._renderGate.IsSet;
    }

    // Playback exhausted the source without looping. Hand the
    // transition to the state machine, which applies the retention
    // rules and queues a release when nothing retains the buffers.
    private void OnNaturalEnd()
    {
        this._renderGate.Reset();

        this.NotifyPlaybackCompleted();
    }
}
