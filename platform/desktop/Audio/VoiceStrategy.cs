// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;

namespace DotGame.Framework.Desktop.Audio;

/// <summary>
/// One pre-allocated SDL3 voice. Owns a long-lived
/// <c>SDL_AudioStream</c> connected to the default playback device;
/// playing the voice queues the sound's already-pinned PCM bytes
/// into the stream via <c>SDL_PutAudioStreamDataNoCopy</c> and
/// resumes it — zero per-Play copies, zero per-Play managed
/// allocation.
/// </summary>
/// <remarks>
/// The audio service updates the gain of each playing voice every frame, so a volume change or a
/// fade reaches a voice that's already playing. There's no get callback: a managed callback on
/// SDL's audio thread would run every device buffer for every voice that has ever played, waiting
/// out any blocking collection while holding the device's lock, and a voice dropped without being
/// disposed would leave SDL calling a collected delegate.
/// </remarks>
internal sealed class VoiceStrategy : IVoiceStrategy
{
    private readonly nint _segmentAddress;
    private readonly int _segmentLengthBytes;
    private readonly SoundStrategy _sound;
    private readonly AudioServiceStrategy _audioService;
    private nint _stream;

    private PolledVolumeControlStrategy? _volumeControl;
    private bool _isPaused;
    private bool _isInActiveList;
    private bool _isLooping;
    private float _playbackRate = 1f;

    // Playing and looping, which holds even if a long frame lets the queue run dry
    private bool _isLoopPlaying;
    private readonly int _loopAheadBytes;

    internal VoiceStrategy(
        SoundStrategy sound,
        AudioServiceStrategy audioService,
        TimeSpan offset,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(audioService);

        this._sound = sound;
        this._audioService = audioService;

        // Bake the (offset, duration) segment into a byte range
        // within the sound's already-pinned PCM buffer. Subsequent
        // Play calls hand SDL the same precomputed pointer + length
        // with no per-Play arithmetic and no per-Play allocation.
        int bytesPerFrame = sound.Channels * 2; // 16-bit
        int bytesPerSecond = sound.SampleRate * bytesPerFrame;
        int totalBytes = sound.LengthBytes;

        int startBytes = (int)(offset.TotalSeconds * bytesPerSecond);
        startBytes -= startBytes % bytesPerFrame;
        if (startBytes < 0)
        {
            startBytes = 0;
        }
        else if (startBytes > totalBytes)
        {
            startBytes = totalBytes;
        }

        int lengthBytes = (int)(duration.TotalSeconds * bytesPerSecond);
        lengthBytes -= lengthBytes % bytesPerFrame;
        if (lengthBytes < 0)
        {
            lengthBytes = 0;
        }
        else if (lengthBytes > totalBytes - startBytes)
        {
            lengthBytes = totalBytes - startBytes;
        }

        // NOTE: A quarter of a second ahead, so only a longer frame than that makes a gap
        this._loopAheadBytes = bytesPerSecond / 4;
        this._segmentAddress = sound.PinnedBase + startBytes;
        this._segmentLengthBytes = lengthBytes;

        var spec = new SDL.AudioSpec
        {
            Freq = sound.SampleRate,
            Format = SDL.AudioFormat.AudioS16LE,
            Channels = sound.Channels,
        };

        this._stream = SDL.OpenAudioDeviceStream(
            SDL.AudioDeviceDefaultPlayback, in spec, null, nint.Zero);

    }

    public bool IsPlaying
    {
        get
        {
            if (this._stream == nint.Zero || this._isPaused)
            {
                return false;
            }

            return this._isLoopPlaying
                || SDL.GetAudioStreamAvailable(this._stream) > 0
                || SDL.GetAudioStreamQueued(this._stream) > 0;
        }
    }

    public void SetVolumeControl(
        Strategy<IVolumeControlStrategy>? volumeControl)
    {
        this._volumeControl = volumeControl?.ToConcrete<PolledVolumeControlStrategy>();
        this.ApplyGain();
    }

    public void SetLooping(
        bool isLooping)
    {
        bool wasPlaying = this.IsPlaying;
        this._isLooping = isLooping;
        this._isLoopPlaying = isLooping && wasPlaying;
        if (this._isLoopPlaying)
        {
            this.QueueLoopAhead();
        }
        else if (wasPlaying)
        {
            this.FlushCue();
        }
    }

    // SDL resamples the stream by the ratio as it reads it, so a change is heard at once, and
    // what's queued (which IsPlaying reads) runs out at the rate it's played at
    public void SetPlaybackRate(
        float rate)
    {
        this._playbackRate = rate;
        if (this._stream != nint.Zero)
        {
            SDL.SetAudioStreamFrequencyRatio(this._stream, rate);
            if (this._isLoopPlaying && !this._isPaused)
            {
                this.QueueLoopAhead();
            }
        }
    }

    public void Play()
    {
        if ((this._stream == nint.Zero) || this._sound.IsDisposed)
        {
            return;
        }

        this.TrackAsActive();

        if (this._isPaused)
        {
            this._isPaused = false;
            this.ApplyGain();
            SDL.ResumeAudioStreamDevice(this._stream);
            return;
        }

        // Restart from the beginning: clear any queued data, then
        // hand SDL a direct pointer into the sound's pinned PCM
        // buffer. No managed copy, no SDL-side copy.
        SDL.ClearAudioStream(this._stream);
        this.ApplyGain();

        int lengthBytes = this._segmentLengthBytes;
        if (lengthBytes <= 0)
        {
            return;
        }

        SDL.PutAudioStreamDataNoCopy(
            this._stream,
            this._segmentAddress,
            lengthBytes,
            callback: null,
            userdata: nint.Zero);

        this._isLoopPlaying = this._isLooping;
        if (this._isLoopPlaying)
        {
            this.QueueLoopAhead();
        }
        else
        {
            this.FlushCue();
        }

        SDL.ResumeAudioStreamDevice(this._stream);
    }

    public void Stop()
    {
        if (this._stream == nint.Zero)
        {
            return;
        }

        SDL.PauseAudioStreamDevice(this._stream);
        SDL.ClearAudioStream(this._stream);
        this._isPaused = false;
        this._isLoopPlaying = false;
    }

    public void Pause()
    {
        if (this._stream == nint.Zero || this._isPaused)
        {
            return;
        }

        SDL.PauseAudioStreamDevice(this._stream);
        this._isPaused = true;
    }

    public void Dispose()
    {
        if (this._stream != nint.Zero)
        {
            SDL.DestroyAudioStream(this._stream);
            this._stream = nint.Zero;
        }
    }

    private void TrackAsActive()
    {
        if (!this._isInActiveList)
        {
            this._isInActiveList = true;
            this._audioService.AddActiveVoice(this);
        }
    }

    /// <summary>
    /// Brings the stream's gain up to date, each frame. Returns whether this voice stays in the
    /// service's list of active voices.
    /// </summary>
    internal bool Update()
    {
        if ((this._stream != nint.Zero) && (this._isPaused || this.IsPlaying))
        {
            if (this._isLoopPlaying && !this._isPaused)
            {
                this.QueueLoopAhead();
            }

            this.ApplyGain();
            return true;
        }

        this._isInActiveList = false;
        return false;
    }

    // The same pinned bytes again, with no copy, until there's enough to last past the next frame
    private void QueueLoopAhead()
    {
        if (this._segmentLengthBytes <= 0)
        {
            this._isLoopPlaying = false;
            return;
        }

        // (What's queued is in the sound's own bytes, which a faster rate uses up sooner)
        int ahead = (int)MathF.Ceiling(this._loopAheadBytes * this._playbackRate);
        while (SDL.GetAudioStreamQueued(this._stream) < ahead)
        {
            SDL.PutAudioStreamDataNoCopy(
                this._stream,
                this._segmentAddress,
                this._segmentLengthBytes,
                callback: null,
                userdata: nint.Zero);
        }
    }

    // Tells SDL that what's queued is all of the cue. A stream that's resampling (at a rate
    // other than as recorded) holds its last frames back for the frames it expects to follow,
    // so without this the queue never empties and the voice never finishes. More can still be
    // queued after it, as a loop turned on again does.
    private void FlushCue()
    {
        SDL.FlushAudioStream(this._stream);
    }

    private void ApplyGain()
    {
        if (this._stream == nint.Zero)
        {
            return;
        }

        SDL.SetAudioStreamGain(this._stream, this.ReadVolume());
    }

    private float ReadVolume()
    {
        return PolledVolumeControlStrategy.ReadEffectiveVolume(this._volumeControl);
    }
}
