// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using DotGame.Framework.Content;
using DotGame.Framework.Platform.Audio;

namespace DotGame.Framework.Audio;

/// <summary>A long piece of audio streamed as it plays, for background music. Load it from content; the content service owns it, so release it with <c>Content.UnloadAsync</c>.</summary>
public sealed class Music : IContent
{
    [PlatformContract]
    internal Music(
        Strategy<IMusicStrategy> musicStrategy)
    {
        ArgumentNullException.ThrowIfNull(musicStrategy);

        this.Strategy = musicStrategy;
    }

    [PlatformContract]
    internal readonly Strategy<IMusicStrategy> Strategy;

    /// <summary>Whether it has been released.</summary>
    public bool IsDisposed => this.Strategy.IsDisposed;

    // Where the track really is, for the platform's music and for tests: how much memory it holds,
    // and whether it has got as far as making sound. Games ask IsPlaying and IsPaused.
    internal MusicState State => (this.Strategy.IsDisposed ?
        MusicState.Unloaded : this.Strategy.Interface.State);

    /// <summary>
    /// Whether the music is playing, from the moment <see cref="Play"/> or <see cref="Resume"/> is
    /// called. Becomes <c>false</c> when playback reaches the end, on <see cref="Stop"/> and
    /// <see cref="Pause"/>, and when the music can't be loaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It says what was asked for, not that sound is coming out yet: music that's still loading, or
    /// waiting for the player's first tap in a browser, is playing already, and starts by itself. To
    /// know it's ready, await <see cref="PrepareAsync"/>.
    /// </para>
    /// <para>
    /// The system can refuse to play, as Android does while another app holds the audio, and then it
    /// stays <c>false</c>. It's cheap on every platform, so it's safe to read each frame.
    /// </para>
    /// </remarks>
    public bool IsPlaying => (!this.Strategy.IsDisposed) && this.Strategy.Interface.IsPlaying;

    /// <summary>
    /// Whether the music is paused, from the moment <see cref="Pause"/> is called on music that's
    /// playing, even if it was still loading. <see cref="Resume"/> carries on, and
    /// <see cref="Play"/> or <see cref="Stop"/> ends it.
    /// </summary>
    /// <remarks>
    /// Music paused while still loading finishes loading, and doesn't start until
    /// <see cref="Resume"/>. If it can't be loaded, this becomes <c>false</c>.
    /// </remarks>
    public bool IsPaused => (!this.Strategy.IsDisposed) && this.Strategy.Interface.IsPaused;

    /// <summary>
    /// Gets the total duration of this music track. The value is
    /// precomputed by the content pipeline when available; otherwise
    /// it is determined by the platform codec at load time.
    /// </summary>
    public TimeSpan Duration => this.Strategy.Interface.Duration;

    /// <summary>
    /// Starts this music playing.
    /// </summary>
    /// <param name="start">The time offset within the music to start playback, or if not specified, the start of the music.</param>
    /// <param name="end">The time offset within the music to end playback, or if not specified, the end of the music.</param>
    /// <param name="loopPoint">The time offset within the music to loop back to, or if not specified, the start of the music.</param>
    /// <param name="loop"><c>true</c> if playback should loop back to
    /// <paramref name="loopPoint"/> when <paramref name="end"/> is
    /// reached, or <c>false</c> if playback should stop there.</param>
    /// <remarks>
    /// <para>
    /// Returns immediately and never blocks the calling thread. The fetch, decode, and buffer
    /// allocation it requires, unless <see cref="PrepareAsync"/> did them, are performed on a background
    /// thread and playback begins as soon as they complete. <see cref="IsPlaying"/> is <c>true</c>
    /// from this call.
    /// </para>
    /// <para>
    /// Starts from the beginning of the range, whatever the music was doing, as a voice does:
    /// <see cref="Resume"/> carries on from a pause. If it's already loading to play, that carries on,
    /// and it starts once.
    /// </para>
    /// <para>
    /// Playing retains the music's buffers until <see cref="Stop"/> is
    /// called or playback reaches its natural end. On music that has been
    /// disposed, it does nothing.
    /// </para>
    /// </remarks>
    public void Play(
        TimeSpan? start = default,
        TimeSpan? end = default,
        TimeSpan? loopPoint = null,
        bool loop = false)
    {
        AudioService.ValidateRange(start, end, loopPoint);

        // NOTE: A platform's music carries on when playing or paused
        if ((this.State == MusicState.Playing) || this.IsPaused)
        {
            this.Strategy.Interface.Stop();
        }

        this.Strategy.Interface.Play(start, end, loopPoint, loop);
    }

    /// <summary>
    /// Carries on from where <see cref="Pause"/> stopped, with the range and looping it was playing
    /// with, or starts it if it was paused while still loading. Does nothing unless
    /// <see cref="IsPaused"/>.
    /// </summary>
    public void Resume()
    {
        if (this.IsPaused)
        {
            this.Strategy.Interface.Play(null, null, null, false);
        }
    }

    /// <summary>
    /// Primes this music for playback: performs the fetch, buffer, decode,
    /// and seek work that <see cref="Play"/> would otherwise incur on
    /// demand, without producing any audio. A subsequent
    /// <see cref="Play"/> with the same parameters then starts with
    /// minimal latency; with others, it prepares again for those.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Trades memory for latency: the music holds its decode and
    /// playback buffers from the moment this is called until
    /// <see cref="Unprepare"/> releases them, instead of only for the
    /// duration of playback. The default (never calling this) keeps
    /// music optimized for low memory overhead.
    /// </para>
    /// <para>
    /// All of the work happens on a background thread. This method
    /// does not block the calling thread, and awaiting the returned
    /// task is optional: <see cref="Play"/> may be called at any point
    /// before, during, or after preparation and behaves correctly in
    /// every case.
    /// </para>
    /// <para>
    /// Safe to call repeatedly and in any order relative to
    /// <see cref="Play"/>, <see cref="Pause"/>, and <see cref="Stop"/>.
    /// Preparing music that is already prepared for the same range, playing, or paused
    /// simply takes the retain and completes. On music that has been
    /// disposed, it does nothing.
    /// </para>
    /// </remarks>
    /// <returns>
    /// A task that completes when the music is ready to start at once
    /// (immediately when its buffers are already resident).
    /// </returns>
    public Task PrepareAsync(
        TimeSpan? start = default,
        TimeSpan? end = default,
        TimeSpan? loopPoint = null,
        bool loop = false)
    {
        AudioService.ValidateRange(start, end, loopPoint);

        return this.Strategy.Interface.Prepare(start, end, loopPoint, loop);
    }

    /// <summary>
    /// Releases the retain taken by <see cref="PrepareAsync"/>,
    /// returning the music to its default low-memory behavior.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Does not stop or interrupt playback. If the music is currently
    /// playing or paused, its buffers stay resident and
    /// are freed by the eventual <see cref="Stop"/> (or by playback
    /// reaching its natural end). Otherwise they are freed on a
    /// background thread.
    /// </para>
    /// <para>
    /// Returns immediately, never blocks the calling thread, and is
    /// safe to call repeatedly, in any order, and on music that was
    /// never prepared or has been disposed.
    /// </para>
    /// </remarks>
    public void Unprepare()
    {
        this.Strategy.Interface.Unprepare();
    }

    /// <summary>
    /// Stops playback of this music and releases the retain taken by
    /// <see cref="Play"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If <see cref="PrepareAsync"/> still holds a retain, the buffers
    /// stay resident and the music stays prepared, so the next
    /// <see cref="Play"/> is still latency-free. Otherwise the buffers
    /// are released on a background thread.
    /// </para>
    /// <para>
    /// Returns immediately, never blocks the calling thread, and is
    /// safe to call on music that is not playing or has been disposed.
    /// </para>
    /// </remarks>
    public void Stop()
    {
        this.Strategy.Interface.Stop();
    }

    /// <summary>
    /// Pauses playback of this music, retaining all of its buffers and its
    /// playback position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A subsequent <see cref="Resume"/> carries on exactly where playback
    /// left off. Pausing never releases memory; use <see cref="Stop"/>
    /// for that.
    /// </para>
    /// <para>
    /// Returns immediately, never blocks the calling thread, and is
    /// safe to call on music that is not playing or has been disposed.
    /// Pausing music that is still loading to play keeps it from starting
    /// until <see cref="Resume"/>, and <see cref="IsPaused"/> is <c>true</c> at once.
    /// </para>
    /// </remarks>
    public void Pause()
    {
        this.Strategy.Interface.Pause();
    }

    private VolumeControl? _volumeControl;

    /// <summary>
    /// Gets or sets the volume control for this music.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The control has been disposed.</exception>
    public VolumeControl? VolumeControl
    {
        get => this._volumeControl;

        set
        {
            value?.AddUser();

            var previous = this._volumeControl;
            this._volumeControl = value;
            this.Strategy.Interface.VolumeControl = value?.Strategy;

            previous?.RemoveUser();
        }
    }

    void IDisposable.Dispose()
    {
        GC.SuppressFinalize(this);
        this.Strategy.Dispose();

        this._volumeControl?.RemoveUser();
        this._volumeControl = null;
    }
}
