// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using AVFoundation;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;
using Foundation;

namespace DotGame.Framework.iOS.Audio;

/// <summary>
/// iOS music track. In the
/// <see cref="Framework.Platform.Audio.MusicState.Loaded"/> state this holds
/// only the source URL and metadata; the <see cref="AVAudioFile"/> and
/// a player node from the audio service's pool, on the shared engine,
/// are taken by <see cref="OnAcquireAsync"/> and given back by
/// <see cref="OnReleaseAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Opening the audio file and scheduling the initial segments read
/// ahead and decode PCM; the base class runs both on its background
/// operation queue so neither touches the game or render thread.
/// </para>
/// <para>
/// The track never touches the engine itself: the audio service's
/// audio thread attaches its node, restarts the engine after
/// interruptions, and applies the track's volume from its volume
/// thread. Acquiring never waits for the audio output, so preparing
/// and playing complete as usual while it's unavailable (games await
/// <c>PrepareAsync</c> during start-up); the audio is scheduled when
/// the node is wired, and a track that is playing by then starts.
/// </para>
/// </remarks>
internal sealed class MusicStrategy : MusicStrategyBase, IPlayerNodeOwner
{
    private readonly NSUrl _fileUrl;
    private readonly AudioServiceStrategy _audioService;
    private readonly int _sampleRate;
    private readonly TimeSpan _duration;

    // Resident only between OnAcquireAsync and OnReleaseAsync.
    private AVAudioFile? _audioFile;
    private int _slot = -1;

    private volatile bool _rendering;
    private int _lastSchedule;
    private bool _loop;
    private long _loopFramePosition;
    private long _endFramePosition;
    private volatile PolledVolumeControlStrategy? _volumeControl;

    // Where the current acquire starts, and the node its audio is
    // scheduled on. The acquire (on the track's queue) and the node
    // being wired (on the audio thread) both schedule under this lock.
    private readonly Lock _scheduleLock = new();
    private long _startFrame;
    private AVAudioPlayerNode? _scheduledNode;

    internal MusicStrategy(
        NSUrl fileUrl,
        int sampleRate,
        TimeSpan duration,
        AudioServiceStrategy audioService)
        : base(audioService)
    {
        this._fileUrl = fileUrl
            ?? throw new ArgumentNullException(nameof(fileUrl));
        this._audioService = audioService
            ?? throw new ArgumentNullException(nameof(audioService));
        this._sampleRate = sampleRate;
        this._duration = duration;
    }

    ~MusicStrategy()
    {
        this.ReleaseResources();
    }

    public override TimeSpan Duration => this._duration;

    public override Strategy<IVolumeControlStrategy>? VolumeControl
    {
        set
        {
            this._volumeControl = value?.ToConcrete<PolledVolumeControlStrategy>();
            this.ApplyVolume();
        }
    }

    // The track's node while it is wired to the running output.
    private AVAudioPlayerNode? Node => this._audioService.GetSlotNode(Volatile.Read(ref this._slot));

    #region Resource acquisition

    /// <inheritdoc />
    protected override Task OnAcquireAsync(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop,
        CancellationToken cancellationToken)
    {
        // Runs on the background queue. Opening the file and scheduling
        // segments reads ahead and decodes PCM, so it must never happen
        // inline on a caller's thread.
        var audioFile = this._audioFile;
        if (audioFile is null)
        {
            audioFile = new AVAudioFile(this._fileUrl, out var openError);

            if (openError is not null)
            {
                // The state machine handles this as any failed acquire, rather than this track
                // reporting Prepared and then playing nothing
                throw new InvalidOperationException(
                    $"[MusicStrategy] Failed to open music file: {openError.LocalizedDescription}");
            }

            this._audioFile = audioFile;
        }

        long startFrame = this.ComputeFrames(audioFile, start, end, loopPoint, loop);

        if (this._slot < 0)
        {
            Volatile.Write(
                ref this._slot,
                this._audioService.AcquireSlot(audioFile.ProcessingFormat, this));
        }

        lock (this._scheduleLock)
        {
            this._startFrame = startFrame;

            // Re-priming an already-acquired node (e.g. Stop settling
            // back to Prepared) rewinds the queued audio. Without a node
            // (the output is unavailable) the audio is scheduled when
            // the node is wired.
            var playerNode = this.Node;
            playerNode?.Stop();
            this.ScheduleFromStart(playerNode);
        }

        return Task.CompletedTask;
    }

    // Queues audio from the acquire's start position without starting
    // the node, so it reads ahead and decodes now. No audio is
    // produced until the node is played. Caller holds _scheduleLock.
    private void ScheduleFromStart(
        AVAudioPlayerNode? playerNode)
    {
        this._scheduledNode = playerNode;
        if (playerNode is null)
        {
            return;
        }

        this.ScheduleSegment(this._startFrame);

        if (this._loop)
        {
            // Pre-schedule the first loop iteration so the node has
            // audio queued for a gapless transition.
            this.ScheduleSegment(this._loopFramePosition);
        }
    }

    /// <inheritdoc />
    protected override Task OnReleaseAsync()
    {
        this.ReleaseResources();

        if (this.IsDisposed)
        {
            this._fileUrl.Dispose();
        }

        return Task.CompletedTask;
    }

    private void ReleaseResources()
    {
        this._rendering = false;

        lock (this._scheduleLock)
        {
            this._scheduledNode = null;
        }

        // Releasing the slot stops the node first: this fires all
        // pending segment completion callbacks, which observe
        // _rendering and will not schedule more segments. Exchange so
        // concurrent teardown paths cannot release the slot twice.
        int slot = Interlocked.Exchange(ref this._slot, -1);
        if (slot >= 0)
        {
            this._audioService.ReleaseSlot(slot);
        }

        var audioFile = Interlocked.Exchange(ref this._audioFile, null);
        audioFile?.Dispose();
    }

    #endregion

    #region Output primitives

    /// <inheritdoc />
    protected override void OnStartOutput()
    {
        this.StartNode();
    }

    /// <inheritdoc />
    protected override void OnResumeOutput()
    {
        this.StartNode();
    }

    /// <inheritdoc />
    protected override void OnPauseOutput()
    {
        this._rendering = false;

        this.Node?.Pause();
    }

    /// <inheritdoc />
    protected override void OnStopOutput()
    {
        this._rendering = false;

        this.Node?.Pause();
    }

    // Without a node (the output is unavailable) the track is
    // silent, and OnNodeWired starts it when the node is wired.
    private void StartNode()
    {
        this._rendering = true;

        var playerNode = this.Node;
        if (playerNode is null)
        {
            return;
        }

        this.ApplyVolume();

        // Started on the audio thread, where waiting for the output can't
        // stall the game, unless the track has paused, stopped or given
        // its node back by then
        int slot = Volatile.Read(ref this._slot);
        this._audioService.StartSlotNode(slot, () => this._rendering && (Volatile.Read(ref this._slot) == slot));

        this._audioService.OnPlaybackStarted();
    }

    #endregion

    #region IPlayerNodeOwner

    bool IPlayerNodeOwner.IsRendering => this._rendering && (this.Node is not null);

    /// <inheritdoc />
    public void ApplyVolume()
    {
        var playerNode = this.Node;
        if (playerNode is not null)
        {
            playerNode.Volume = PolledVolumeControlStrategy.ReadEffectiveVolume(this._volumeControl);
        }
    }

    void IPlayerNodeOwner.OnEngineRestarted()
    {
        // The engine stopping stops its player nodes; carry on where
        // the track was.
        if (this._rendering)
        {
            // NOTE: As VoiceStrategy's: a node refused here shouldn't stop the other tracks' restart
            try
            {
                if (this.Node is { } node)
                {
                    AudioNodes.Play(node);
                }
            }
            catch (Exception exception) when (exception is ObjCRuntime.ObjCException or Foundation.NSErrorException)
            {
                GameHost.ReportAudioFailure(0x6e3b90d2, exception);
            }
        }
    }

    void IPlayerNodeOwner.OnNodeWired()
    {
        lock (this._scheduleLock)
        {
            // Schedule the audio the acquire couldn't, or schedule it
            // again from the start on the new node a media-services
            // reset left. A released track has no audio file and
            // schedules nothing.
            var playerNode = this.Node;
            if ((playerNode is null) || (playerNode == this._scheduledNode) || (this._audioFile is null))
            {
                return;
            }

            this.ScheduleFromStart(playerNode);
        }

        if (this._rendering)
        {
            this.StartNode();
        }
    }

    #endregion

    private long ComputeFrames(
        AVAudioFile audioFile,
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop)
    {
        long startFrame = start.HasValue
            ? (long)(start.Value.TotalSeconds * this._sampleRate)
            : 0;

        if (end.HasValue)
        {
            long endFrames = (long)(end.Value.TotalSeconds * this._sampleRate);
            this._endFramePosition = Math.Min(endFrames, audioFile.Length);
        }
        else
        {
            this._endFramePosition = audioFile.Length;
        }

        this._loopFramePosition = loopPoint.HasValue
            ? (long)(loopPoint.Value.TotalSeconds * this._sampleRate)
            : 0;

        this._loop = loop;

        return startFrame;
    }

    /// <summary>
    /// Schedules a segment of the audio file for playback. When the
    /// segment finishes, the completion handler queues the next loop
    /// iteration (if looping) or reports the natural end.
    /// </summary>
    private void ScheduleSegment(
        long fromFrame)
    {
        var playerNode = this.Node;
        var audioFile = this._audioFile;

        if (playerNode is null || audioFile is null)
        {
            return;
        }

        long frameCount = this._endFramePosition - fromFrame;
        if (frameCount <= 0)
        {
            return;
        }

        int schedule = Interlocked.Increment(ref this._lastSchedule);
        playerNode.ScheduleSegment(
            audioFile,
            fromFrame,
            (uint)frameCount,
            null,
            () =>
            {
                // A stop's completion can run after a quick play has started again
                if (schedule != Volatile.Read(ref this._lastSchedule))
                {
                    return;
                }

                if (this._loop && this._rendering)
                {
                    this.ScheduleSegment(this._loopFramePosition);
                }
                else if (this._rendering)
                {
                    // Non-loop playback has reached its natural end.
                    // NotifyPlaybackCompleted posts the transition off
                    // this AVAudio callback queue, so the callback is
                    // never blocked behind the state lock.
                    this._rendering = false;
                    this.NotifyPlaybackCompleted();
                }
            });
    }
}
