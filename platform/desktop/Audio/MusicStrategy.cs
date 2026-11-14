// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;

namespace DotGame.Framework.Desktop.Audio;

/// <summary>
/// SDL3 music track. In the <see cref="Framework.Platform.Audio.MusicState.Loaded"/>
/// state this holds only the source path and the metadata needed to
/// describe the track; the Opus decoder (which reads the file), the decode
/// buffer, and the SDL device stream are created by
/// <see cref="OnAcquireAsync"/> and torn down by
/// <see cref="OnReleaseAsync"/>, both of which run on the base class's
/// background operation queue.
/// </summary>
/// <remarks>
/// A thread of its own decodes while the track plays, keeping a quarter of a second queued in the
/// stream. Decoding in the stream's get callback would run it on SDL's audio thread, holding the
/// device's lock, so a slow decode or a cold disk read would hold up all audio and any game thread
/// playing a sound, and an exception would end the process.
/// </remarks>
internal sealed class MusicStrategy : MusicStrategyBase
{
    // Decode buffer: 4800 samples per channel is 100 ms at 48 kHz
    private const int DecodeSamplesPerChannel = 4800;

    // How much to keep queued in the stream, and how often to check it (and the volume)
    private const int QueuedMilliseconds = 250;
    private const int PollMilliseconds = 10;

    // How long the end of a track may take to play out beyond what's queued
    private const int PlayOutGraceMilliseconds = 250;

    // Every track that hasn't been collected, so shutdown can tear them all down before SDL
    // quits. Weak, so it doesn't keep unloaded tracks alive.
    private static readonly ConditionalWeakTable<MusicStrategy, object?> _tracks = new();

    private readonly string _path;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly TimeSpan _duration;

    // Signalled while the decode thread should fill the stream
    private readonly ManualResetEventSlim _renderGate = new(false);

    // Set while the decode thread is parked at the gate or gone, when it can't be using the reader
    private readonly ManualResetEventSlim _renderParked = new(false);

    // Resident only between OnAcquireAsync and OnReleaseAsync.
    private OpusMusicReader? _decoder;
    private short[]? _decodeBuffer;
    private nint _stream;
    private Thread? _decodeThread;
    private volatile bool _threadRunning;

    private bool _loop;
    private long _loopSamplePosition;
    private long _endSamplePosition;

    // NOTE: The concrete control, so a control disposed while the track plays can't throw on the decode
    // thread. Set by the game thread.
    private volatile PolledVolumeControlStrategy? _volumeControl;

    internal MusicStrategy(
        string path,
        int sampleRate,
        int channels,
        TimeSpan duration,
        IMusicPlaybackListener? listener)
        : base(listener)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        this._path = path;
        this._sampleRate = sampleRate;
        this._channels = channels;
        this._duration = duration;

        _tracks.Add(this, null);
    }

    /// <summary>
    /// Disposes every track and waits, up to <paramref name="timeout"/> each, for its queued
    /// acquire and release to finish. Call before SDL quits: quitting destroys the tracks'
    /// device streams, so an acquire still running on the background queue would otherwise
    /// start output on a freed stream and crash the process (as when the game loop ends
    /// with an exception while a track is being prepared).
    /// </summary>
    internal static void DisposeAndDrainAll(
        TimeSpan timeout)
    {
        foreach (var (track, _) in _tracks)
        {
            track.DisposeAndDrain(timeout);
        }
    }

    ~MusicStrategy()
    {
        // Only the native SDL stream is reclaimed here: the decoder is managed memory alone
        if (this._stream != nint.Zero)
        {
            SDL.DestroyAudioStream(this._stream);
            this._stream = nint.Zero;
        }
    }

    public override TimeSpan Duration => this._duration;

    public override Strategy<IVolumeControlStrategy>? VolumeControl
    {
        set => this._volumeControl = value?.ToConcrete<PolledVolumeControlStrategy>();
    }

    #region Resource acquisition

    /// <inheritdoc />
    protected override Task OnAcquireAsync(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop,
        CancellationToken cancellationToken)
    {
        // Runs on the background queue: opening the file, allocating
        // the decode buffers, and opening the device stream all happen
        // off the game thread.
        this.ParkRendering();

        var path = this._path;
        var decoder = this._decoder ??= new OpusMusicReader(
            () => System.IO.File.OpenRead(path), this._channels, (long)(this._duration.TotalSeconds * this._sampleRate));

        this._decodeBuffer ??= new short[DecodeSamplesPerChannel * this._channels];

        if (this._stream == nint.Zero)
        {
            var spec = new SDL.AudioSpec
            {
                Freq = this._sampleRate,
                Format = SDL.AudioFormat.AudioS16LE,
                Channels = this._channels,
            };

            this._stream = SDL.OpenAudioDeviceStream(
                SDL.AudioDeviceDefaultPlayback,
                in spec,
                null,
                nint.Zero);

            if (this._stream == nint.Zero)
            {
                throw new InvalidOperationException($"Failed to open an audio stream: {SDL.GetError()}");
            }
        }
        else
        {
            SDL.PauseAudioStreamDevice(this._stream);
            SDL.ClearAudioStream(this._stream);
        }

        // Seek to the start position, which reads the file the first time, so starting output is a
        // cheap, non-blocking operation
        decoder.SeekTo(start.HasValue ? (long)(start.Value.TotalSeconds * this._sampleRate) : 0L);

        long totalSamples = decoder.TotalFrames;
        this._endSamplePosition = end.HasValue
            ? Math.Min((long)(end.Value.TotalSeconds * this._sampleRate), totalSamples)
            : totalSamples;

        this._loopSamplePosition = loopPoint.HasValue
            ? (long)(loopPoint.Value.TotalSeconds * this._sampleRate)
            : 0L;

        this._loop = loop;

        // A decoder fault ends the thread, so that a later Play can try again with a new one
        if ((this._decodeThread is null) || !this._decodeThread.IsAlive)
        {
            this._threadRunning = true;
            this._renderGate.Reset();
            this._renderParked.Reset();

            this._decodeThread = new Thread(this.DecodeLoop)
            {
                IsBackground = true,
                Name = "MusicDecode",
            };
            this._decodeThread.Start();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task OnReleaseAsync()
    {
        this.ReleaseResources();

        return Task.CompletedTask;
    }

    // Stops the decode thread at its gate, so the reader and the stream's contents are this thread's
    private void ParkRendering()
    {
        if (this._decodeThread is null)
        {
            return;
        }

        this._renderGate.Reset();
        this._renderParked.Wait();
    }

    private void ReleaseResources()
    {
        this._threadRunning = false;
        this._renderGate.Set();

        var decodeThread = this._decodeThread;
        this._decodeThread = null;
        decodeThread?.Join();

        this._renderGate.Reset();

        if (this._stream != nint.Zero)
        {
            SDL.DestroyAudioStream(this._stream);
            this._stream = nint.Zero;
        }

        this._decoder = null;
        this._decodeBuffer = null;
    }

    #endregion

    #region Output primitives

    /// <inheritdoc />
    protected override void OnStartOutput()
    {
        if (this._stream == nint.Zero)
        {
            return;
        }

        this.ApplyVolume();
        SDL.ResumeAudioStreamDevice(this._stream);
        this._renderGate.Set();
    }

    /// <inheritdoc />
    protected override void OnResumeOutput()
    {
        if (this._stream == nint.Zero)
        {
            return;
        }

        this.ApplyVolume();
        SDL.ResumeAudioStreamDevice(this._stream);
        this._renderGate.Set();
    }

    /// <inheritdoc />
    protected override void OnPauseOutput()
    {
        // Keeps what's queued, which plays on resume
        this._renderGate.Reset();

        if (this._stream != nint.Zero)
        {
            SDL.PauseAudioStreamDevice(this._stream);
        }
    }

    /// <inheritdoc />
    protected override void OnStopOutput()
    {
        this._renderGate.Reset();

        if (this._stream != nint.Zero)
        {
            SDL.PauseAudioStreamDevice(this._stream);
            SDL.ClearAudioStream(this._stream);
        }
    }

    #endregion

    private void ApplyVolume()
    {
        if (this._stream != nint.Zero)
        {
            SDL.SetAudioStreamGain(this._stream, PolledVolumeControlStrategy.ReadEffectiveVolume(this._volumeControl));
        }
    }

    private void DecodeLoop()
    {
        try
        {
            this.RenderUntilReleased();
        }
        catch (Exception exception) when (this._threadRunning)
        {
            // A decoder fault ends this track, not the game
            GameHost.ReportAudioFailure(0x4a1d7c92, exception);
            this.NotifyPlaybackCompleted();
        }
        catch (Exception exception)
        {
            // Released under the loop's feet, which is what stopping it asked for
            Trace.Write(0x4a1d7c93, exception: exception);
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

    private void RenderUntilReleased()
    {
        int bytesPerFrame = this._channels * sizeof(short);
        int queuedLimit = (this._sampleRate * QueuedMilliseconds / 1000) * bytesPerFrame;

        while (this._threadRunning)
        {
            this.WaitAtRenderGate();

            if (!this._threadRunning)
            {
                break;
            }

            var decoder = this._decoder;
            var decodeBuffer = this._decodeBuffer;
            nint stream = this._stream;
            if ((decoder is null) || (decodeBuffer is null) || (stream == nint.Zero))
            {
                break;
            }

            this.ApplyVolume();

            if (SDL.GetAudioStreamQueued(stream) >= queuedLimit)
            {
                Thread.Sleep(PollMilliseconds);
                continue;
            }

            int samplesToRead = (int)Math.Min(DecodeSamplesPerChannel, this._endSamplePosition - decoder.FramePosition);

            int framesRead = (samplesToRead > 0)
                ? decoder.Read(decodeBuffer.AsSpan(0, samplesToRead * this._channels))
                : 0;

            if (framesRead <= 0)
            {
                if (this._loop)
                {
                    decoder.SeekTo(this._loopSamplePosition);
                }
                else if (this.PlayOut(stream))
                {
                    this.NotifyPlaybackCompleted();

                    // Parked until a prepared track plays again, or it's released
                    this._renderGate.Reset();
                }

                continue;
            }

            unsafe
            {
                fixed (short* samples = decodeBuffer)
                {
                    if (!SDL.PutAudioStreamData(stream, (nint)samples, framesRead * bytesPerFrame))
                    {
                        throw new InvalidOperationException($"Failed to queue music: {SDL.GetError()}");
                    }
                }
            }
        }
    }

    // Waits until what's queued has played, so the end of the track is heard. A pause or stop
    // interrupts this: returns whether the end was reached.
    private bool PlayOut(
        nint stream)
    {
        int queued = SDL.GetAudioStreamQueued(stream);
        long deadline = Environment.TickCount64 + PlayOutGraceMilliseconds
            + ((Math.Max(queued, 0) * 1000L) / (this._sampleRate * this._channels * sizeof(short)));

        while ((SDL.GetAudioStreamQueued(stream) > 0) || (SDL.GetAudioStreamAvailable(stream) > 0))
        {
            if (!this._threadRunning || !this._renderGate.IsSet)
            {
                return false;
            }

            if (Environment.TickCount64 > deadline)
            {
                break;
            }

            Thread.Sleep(PollMilliseconds);
        }

        return this._renderGate.IsSet;
    }
}
