// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using AVFoundation;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;

namespace DotGame.Framework.iOS.Audio;

/// <summary>
/// One iOS voice. Owns a slot in the audio service's player-node pool
/// and schedules its segment's <see cref="AVAudioPcmBuffer"/>, shared
/// with the other voices of the segment (<see cref="SoundStrategy.GetSegmentBuffer"/>),
/// on the slot's <see cref="AVAudioPlayerNode"/> each time
/// <see cref="Play"/> is called.
/// </summary>
/// <remarks>
/// <para>
/// The node keeps running between plays: the audio thread starts it
/// once, at the voice's first play (and again after an engine restart
/// or a reset), because <see cref="AVAudioPlayerNode.Play()"/> waits for
/// the output's next IO cycle. A play then schedules the buffer with
/// <see cref="AVAudioPlayerNodeBufferOptions.Interrupts"/>, which
/// replaces whatever the node plays and starts within a render cycle,
/// with no wait; a stop interrupts with an empty buffer. So rapid
/// replays start at once, on any thread.
/// </para>
/// <para>
/// Player nodes are never detached — AVAudioEngine crashes when
/// nodes are detached while the engine is running — so a voice
/// retains its slot for its entire lifetime and releases it on
/// <see cref="Dispose"/>. While the voice plays, the audio service's
/// volume thread applies its volume to the node, so volume
/// changes and fades take effect while it plays.
/// </para>
/// </remarks>
internal sealed class VoiceStrategy : IVoiceStrategy, IPlayerNodeOwner
{
    private readonly AudioServiceStrategy _service;
    private readonly AVAudioPcmBuffer _segmentBuffer;
    private readonly AVAudioPcmBuffer _emptyBuffer;
    private readonly int _slot;
    // Cached ScheduleBuffer completion delegate. Allocated once at
    // construction so each Play reuses the same managed instance and
    // avoids per-Play closure allocation on the audio hot path.
    private readonly Action _onPlaybackComplete;
    private readonly Func<bool> _nodeWantedCheck;
    private bool _isDisposed;

    // The segment buffers scheduled and not yet completed (finished or
    // interrupted: each completes exactly once). The voice has finished
    // when this drops to zero, so an interrupted play's late completion
    // doesn't end the play that replaced it.
    private int _scheduledBuffers;

    // Whether the voice wants its node running: set by its first play,
    // cleared by a pause (which stops the node) and on dispose
    private volatile bool _isNodeWanted;

    // Set by the game thread, read by the volume thread; the
    // playback completion callback also clears _isPlaying.
    private volatile bool _isPlaying;
    private volatile PolledVolumeControlStrategy? _volumeControl;

    // A play made before this voice's new node was wired, for the wiring to perform
    private volatile bool _playWhenWired;

    private volatile bool _isLooping;

    // Paused with its buffer part played: the next play carries on from there
    private bool _isPaused;

    // What the node's volume was last set to, as a set costs a message 66 times a second
    private float _appliedVolume = -1f;

    internal VoiceStrategy(
        AudioServiceStrategy service,
        SoundStrategy sound,
        TimeSpan offset,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(sound);

        this._service = service;

        int channels = sound.Channels;
        int sampleRate = sound.SampleRate;
        int totalFrames = sound.SourcePcm.Length / (channels * 2);

        int startFrame = (int)(offset.TotalSeconds * sampleRate);
        if (startFrame < 0)
        {
            startFrame = 0;
        }
        else if (startFrame > totalFrames)
        {
            startFrame = totalFrames;
        }

        int frameCount = (int)(duration.TotalSeconds * sampleRate);
        if (frameCount < 0)
        {
            frameCount = 0;
        }
        else if (frameCount > totalFrames - startFrame)
        {
            frameCount = totalFrames - startFrame;
        }

        this._segmentBuffer = sound.GetSegmentBuffer(startFrame, frameCount);
        this._emptyBuffer = sound.EmptyBuffer;

        this._slot = service.AcquireSlot(sound.Format, this);
        this._onPlaybackComplete = this.HandlePlaybackComplete;
        this._nodeWantedCheck = () => this._isNodeWanted && !this._isDisposed;
    }

    private void HandlePlaybackComplete()
    {
        if (Interlocked.Decrement(ref this._scheduledBuffers) <= 0)
        {
            Volatile.Write(ref this._scheduledBuffers, 0);
            this._isPlaying = false;
        }
    }

    public bool IsPlaying => this._isPlaying && !this._isDisposed;

    bool IPlayerNodeOwner.IsRendering => this.IsPlaying;

    void IPlayerNodeOwner.OnEngineRestarted()
    {
        // The engine stopping stopped the node, which keeps running
        // between plays. Audio thread. A sound cut short isn't resumed,
        // but a loop has no end to have been cut short of.
        bool isLoopCutShort = this._isLooping && this._isPlaying;
        this._isPlaying = false;
        this._isPaused = false;
        if (isLoopCutShort)
        {
            this.ScheduleSegment();
        }

        this.StartNodeOnAudioThread();
    }

    void IPlayerNodeOwner.OnNodeWired()
    {
        // A new node starts stopped. A play made while the output was
        // unavailable was silent and isn't replayed; one made just before
        // the node was wired, as when a pool grows for an overlap, is what
        // this voice is for. Audio thread.
        this._appliedVolume = -1f;
        if ((this._playWhenWired || (this._isLooping && this._isPlaying)) && !this._isDisposed)
        {
            this._playWhenWired = false;
            this.ScheduleSegment();
            this._isNodeWanted = true;
            this._service.OnPlaybackStarted();
        }

        this.StartNodeOnAudioThread();
    }

    private void StartNodeOnAudioThread()
    {
        if (!this._isNodeWanted || this._isDisposed)
        {
            return;
        }

        var node = this._service.GetSlotNode(this._slot);
        if (node is null)
        {
            return;
        }

        try
        {
            AudioNodes.Play(node);
        }
        catch (Exception e) when (e is ObjCRuntime.ObjCException or Foundation.NSErrorException)
        {
            Trace.Write(0x2b6f9e14, "[VoiceStrategy] The node didn't start.", e);
        }
    }

    public void SetVolumeControl(
        Strategy<IVolumeControlStrategy>? volumeControl)
    {
        this._volumeControl = volumeControl?.ToConcrete<PolledVolumeControlStrategy>();
        this.ApplyVolume();
    }

    public void SetLooping(
        bool isLooping)
    {
        if (isLooping == this._isLooping)
        {
            return;
        }

        this._isLooping = isLooping;

        var node = this._service.GetSlotNode(this._slot);
        if (this._isDisposed || (node is null) || (!this._isPlaying && !this._isPaused))
        {
            return;
        }

        if (isLooping)
        {
            // Loops from when what's playing reaches its end
            Interlocked.Increment(ref this._scheduledBuffers);
            node.ScheduleBuffer(this._segmentBuffer, null, AVAudioPlayerNodeBufferOptions.Loops, this._onPlaybackComplete);
        }
        else
        {
            // Lets the loop reach its end, and its completion ends the play
            node.ScheduleBuffer(this._emptyBuffer, null, AVAudioPlayerNodeBufferOptions.InterruptsAtLoop, null);
        }
    }

    public void Play()
    {
        if (this._isDisposed)
        {
            return;
        }

        // No node while the audio output is unavailable: the voice
        // stays silent rather than waiting for the audio system. With the
        // output there, the node is about to be wired.
        var node = this._service.GetSlotNode(this._slot);
        if (node is null)
        {
            if (this._service.IsOutputAvailable)
            {
                this._isPlaying = true;
                this._playWhenWired = true;
            }

            return;
        }

        if (this._isPaused)
        {
            // The node still holds the buffer, at the paused position
            this._isPaused = false;
            this._isPlaying = true;
            this._isNodeWanted = true;
            this.ApplyVolume();
            this._service.StartSlotNode(this._slot, this._nodeWantedCheck);
            this._service.OnPlaybackStarted();
            return;
        }

        this.ScheduleSegment();

        // The first play, or the first since a pause, has the audio
        // thread start the node, which plays what's scheduled from then on
        if (!this._isNodeWanted)
        {
            this._isNodeWanted = true;
            this._service.StartSlotNode(this._slot, this._nodeWantedCheck);
        }

        this._service.OnPlaybackStarted();
    }

    // Replaces what the node plays (its completion still comes, and
    // is counted), and on the running node starts with no wait
    private void ScheduleSegment()
    {
        var node = this._service.GetSlotNode(this._slot);
        if (node is null)
        {
            this._isPlaying = false;
            return;
        }

        this.ApplyVolume();

        Interlocked.Increment(ref this._scheduledBuffers);
        this._isPlaying = true;
        var options = this._isLooping
            ? AVAudioPlayerNodeBufferOptions.Interrupts | AVAudioPlayerNodeBufferOptions.Loops
            : AVAudioPlayerNodeBufferOptions.Interrupts;
        node.ScheduleBuffer(this._segmentBuffer, null, options, this._onPlaybackComplete);
    }

    public void Stop()
    {
        this._playWhenWired = false;

        if (this._isDisposed || (!this._isPlaying && !this._isPaused))
        {
            return;
        }

        this._isPlaying = false;
        this._isPaused = false;

        // Silence the node without stopping it, so the next play starts
        // at once
        var node = this._service.GetSlotNode(this._slot);
        node?.ScheduleBuffer(this._emptyBuffer, null, AVAudioPlayerNodeBufferOptions.Interrupts, null);
    }

    public void Pause()
    {
        if (this._isDisposed)
        {
            return;
        }

        var node = this._service.GetSlotNode(this._slot);
        node?.Pause();
        this._isNodeWanted = false;
        this._isPaused = this._isPlaying;
        this._isPlaying = false;
        this._playWhenWired = false;
    }

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        this._isDisposed = true;
        this._isNodeWanted = false;

        // Which stops the node
        this._service.ReleaseSlot(this._slot);
        this._isPlaying = false;

        // The segment's buffer belongs to the sound, shared with its
        // other voices
    }

    /// <summary>
    /// Sets the node's volume from the volume control. Called on the
    /// game thread and on the audio service's volume thread.
    /// </summary>
    public void ApplyVolume()
    {
        if (this._isDisposed)
        {
            return;
        }

        var node = this._service.GetSlotNode(this._slot);
        if (node is null)
        {
            return;
        }

        float volume = PolledVolumeControlStrategy.ReadEffectiveVolume(this._volumeControl);
        if (volume != this._appliedVolume)
        {
            this._appliedVolume = volume;
            node.Volume = volume;
        }
    }

    internal static AVAudioPcmBuffer BuildPcmBuffer(
        byte[] sourcePcm,
        int startFrame,
        int frameCount,
        int channels,
        AVAudioFormat format)
    {
        // Even an empty segment needs a valid buffer; allocate with
        // at least one frame of capacity and a zero FrameLength so
        // ScheduleBuffer's completion callback still fires.
        uint capacity = (uint)Math.Max(frameCount, 1);
        var buffer = new AVAudioPcmBuffer(format, capacity);
        buffer.FrameLength = (uint)Math.Max(frameCount, 0);

        if (frameCount <= 0)
        {
            return buffer;
        }

        unsafe
        {
            float** channelData = (float**)buffer.FloatChannelData;

            fixed (byte* srcPtr = sourcePcm)
            {
                short* shorts = (short*)srcPtr;
                for (int frame = 0; frame < frameCount; frame++)
                {
                    int srcFrame = startFrame + frame;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        channelData[ch][frame] =
                            shorts[(srcFrame * channels) + ch] / 32768f;
                    }
                }
            }
        }

        return buffer;
    }
}
