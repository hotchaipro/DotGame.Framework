// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using AVFoundation;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.iOS.Audio;

/// <summary>
/// iOS representation of a decoded sound. Holds the raw 16-bit
/// interleaved PCM bytes shared by every voice created for this
/// sound, plus the shared <see cref="AVAudioFormat"/> describing the
/// AVAudioEngine-compatible float32 deinterleaved layout that voices
/// build their <see cref="AVAudioPcmBuffer"/>s in.
/// </summary>
/// <remarks>
/// Each (offset, duration) segment of this sound is converted from
/// 16-bit to float32 once, when its first voice is created, into an
/// <see cref="AVAudioPcmBuffer"/> that every voice of the segment
/// schedules, so a pool's extra voices cost a node each, not a copy of
/// the audio. <c>Play</c> calls schedule that buffer with no managed
/// allocation.
/// </remarks>
internal sealed class SoundStrategy : ISoundStrategy
{
    private readonly byte[] _sourcePcm;
    private readonly int _sampleRate;
    private readonly int _channels;
    private AVAudioFormat? _format;

    // One buffer per segment, shared by every voice that plays it: a
    // pool's voices schedule the same buffer, on their own nodes
    private readonly Dictionary<(int StartFrame, int FrameCount), AVAudioPcmBuffer> _segmentBuffers = new();
    private AVAudioPcmBuffer? _emptyBuffer;

    internal SoundStrategy(
        byte[] sourcePcm,
        int sampleRate,
        int channels,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(sourcePcm);

        this._sourcePcm = sourcePcm;
        this._sampleRate = sampleRate;
        this._channels = channels;
        this.Duration = duration;

        this._format = new AVAudioFormat(
            AVAudioCommonFormat.PCMFloat32,
            (double)sampleRate,
            (uint)channels,
            interleaved: false);
    }

    public TimeSpan Duration { get; }

    internal byte[] SourcePcm => this._sourcePcm;

    internal int SampleRate => this._sampleRate;

    internal int Channels => this._channels;

    internal AVAudioFormat Format =>
        this._format ?? throw new ObjectDisposedException(nameof(SoundStrategy));

    /// <summary>
    /// The segment's buffer, converted once and shared by the voices
    /// that play it. Game thread (voices are created there).
    /// </summary>
    internal AVAudioPcmBuffer GetSegmentBuffer(
        int startFrame,
        int frameCount)
    {
        var key = (startFrame, frameCount);
        if (!this._segmentBuffers.TryGetValue(key, out var buffer))
        {
            buffer = VoiceStrategy.BuildPcmBuffer(this._sourcePcm, startFrame, frameCount, this._channels, this.Format);
            this._segmentBuffers.Add(key, buffer);
        }

        return buffer;
    }

    /// <summary>
    /// A buffer with no frames, in this sound's format: scheduled with
    /// <see cref="AVAudioPlayerNodeBufferOptions.Interrupts"/>, it stops
    /// what a running node plays without stopping the node.
    /// </summary>
    internal AVAudioPcmBuffer EmptyBuffer =>
        this._emptyBuffer ??= VoiceStrategy.BuildPcmBuffer(this._sourcePcm, 0, 0, this._channels, this.Format);

    public void Dispose()
    {
        foreach (var buffer in this._segmentBuffers.Values)
        {
            buffer.Dispose();
        }

        this._segmentBuffers.Clear();
        this._emptyBuffer?.Dispose();
        this._emptyBuffer = null;

        this._format?.Dispose();
        this._format = null;
    }
}
