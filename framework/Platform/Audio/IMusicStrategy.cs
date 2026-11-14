// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;

namespace DotGame.Framework.Platform.Audio;

/// <summary>
/// Platform-specific state for a single music track.
/// </summary>
/// <remarks>
/// <para>
/// Implementations should derive from <c>MusicStrategyBase</c> (in the platforms' shared code),
/// which supplies the shared <see cref="MusicState"/> machine, the
/// buffer-retention accounting, and the background operation queue
/// that keeps acquisition and teardown off the game and render
/// threads.
/// </para>
/// <para>
/// <b>Retention.</b> A track's buffers are retained while either a
/// prepare retain (taken by prepare, released by unprepare) or a
/// playback retain (taken by play, released by stop) is outstanding,
/// and are freed only once both are released. This makes prepare,
/// play, stop, and unprepare safe to issue in any order.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="State"/>, <see cref="IsPlaying"/>, <see cref="IsPaused"/>, and
/// <see cref="Duration"/> are cheap, non-blocking reads that are safe
/// from any thread, including a per-frame poll on the game thread.
/// </para>
/// <para>
/// <b>Playback.</b> The playback operations (<see cref="Play"/>, <see cref="Prepare"/>,
/// <see cref="Unprepare"/>, <see cref="Stop"/>, <see cref="Pause"/>)
/// implement the <see cref="MusicState"/> machine described on
/// <c>MusicStrategyBase</c> (in the platforms' shared code). They may be issued in any order,
/// any number of times, from any thread, and none of them may block
/// the calling thread on file I/O, decoding, or native teardown —
/// they are routinely called from the game thread and from
/// ANR-sensitive platform lifecycle callbacks.
/// </para>
/// </remarks>
internal interface IMusicStrategy
{
    /// <summary>
    /// The current lifecycle state of the track, which determines how
    /// much memory it currently holds.
    /// </summary>
    MusicState State { get; }

    /// <summary>
    /// <c>true</c> from the <see cref="Play"/> that was accepted, so also while
    /// <see cref="State"/> is <see cref="MusicState.Preparing"/> and while a browser waits
    /// for a gesture to allow sound. Transitions to <c>false</c> when playback reaches the end
    /// of the track, when the track is stopped or paused, and when preparing it fails.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// <c>true</c> from a <see cref="Pause"/> of a track for which <see cref="IsPlaying"/> was
    /// <c>true</c>, so also when it hadn't started yet, until the next <see cref="Play"/> or
    /// <see cref="Stop"/>. A track paused before it started doesn't start until
    /// <see cref="Play"/>, which then starts it at its prepared position.
    /// </summary>
    bool IsPaused { get; }

    /// <summary>
    /// Sets the volume control for this music.
    /// </summary>
    Strategy<IVolumeControlStrategy>? VolumeControl { set; }

    /// <summary>
    /// Total duration of the music track. Available from the
    /// <see cref="MusicState.Loaded"/> state onward, since it comes
    /// from metadata rather than from decoded audio.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// Begins or resumes playback.
    /// </summary>
    /// <param name="start">The time offset within the music to start playback, or if not specified, the start of the music.</param>
    /// <param name="end">The time offset within the music to end playback, or if not specified, the end of the music.</param>
    /// <param name="loopPoint">The time offset within the music to loop back to, or if not specified, the start of the music.</param>
    /// <param name="loop"><c>True</c> if playback should loop back to the <paramref name="loopPoint"/> when <paramref name="end"/> is reached, or
    /// <c>false</c> if the audio should stop when <paramref name="end"/> is reached.</param>
    /// <remarks>
    /// <para>
    /// Takes the playback retain, which is released by
    /// <see cref="Stop"/> (never by <see cref="Pause"/>).
    /// </para>
    /// <para>
    /// Behavior by state: <see cref="MusicState.Unloaded"/> is a
    /// no-op; <see cref="MusicState.Playing"/> is a no-op that
    /// preserves the existing start/end/loop configuration;
    /// <see cref="MusicState.Paused"/> resumes from the paused
    /// position and ignores every parameter;
    /// <see cref="MusicState.Prepared"/> starts immediately, or prepares
    /// again first if prepared for another range;
    /// <see cref="MusicState.Preparing"/> records the request and
    /// starts automatically when preparation completes, preparing again
    /// if for another range; a track paused before it started ignores every
    /// parameter, as a resume does; and
    /// <see cref="MusicState.Loaded"/> implicitly prepares and then
    /// plays.
    /// </para>
    /// <para>
    /// Must not block the calling thread. Any fetch, decode, or
    /// native-object creation required by the
    /// <see cref="MusicState.Loaded"/> path runs on a background
    /// queue.
    /// </para>
    /// </remarks>
    void Play(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop);

    /// <summary>
    /// Primes music for playback: performs every fetch, buffer, decode,
    /// and seek that <see cref="Play"/> would otherwise perform on
    /// demand, without producing any audio, and retains the resulting
    /// buffers until <see cref="Unprepare"/> is called. A subsequent
    /// <see cref="Play"/> then starts with minimal latency.
    /// </summary>
    /// <param name="start">The time offset within the music to start playback, or if not specified, the start of the music.</param>
    /// <param name="end">The time offset within the music to end playback, or if not specified, the end of the music.</param>
    /// <param name="loopPoint">The offset of the loop in the music relative to <paramref name="start"/>, or if not specified, the start of the music.</param>
    /// <param name="loop"><c>True</c> if playback should loop back to the <paramref name="loopPoint"/> when <paramref name="end"/> is reached.</param>
    /// <returns>
    /// A task that completes once the track is prepared. Callers are
    /// never required to await it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Takes the prepare retain. Because the prepare retain and the
    /// playback retain are independent, a <see cref="Stop"/> issued
    /// while a prepare retain is outstanding keeps the buffers
    /// resident (returning to <see cref="MusicState.Prepared"/>)
    /// rather than freeing them.
    /// </para>
    /// <para>
    /// Must not start, resume, or otherwise produce audio. Idempotent:
    /// concurrent or repeated calls share a single in-flight
    /// operation, and calling it on a track already prepared for this
    /// range, playing, or paused simply takes the retain. A track prepared
    /// or preparing for another range, with no play on its way, prepares
    /// again for this one. A call on an
    /// <see cref="MusicState.Unloaded"/> track is a no-op.
    /// </para>
    /// <para>
    /// Must not block the calling thread: all acquisition work runs on
    /// a background queue and the returned task is handed back
    /// immediately.
    /// </para>
    /// </remarks>
    Task Prepare(
        TimeSpan? start,
        TimeSpan? end,
        TimeSpan? loopPoint,
        bool loop);

    /// <summary>
    /// Releases the prepare retain taken by <see cref="Prepare"/>,
    /// without stopping active playback or unloading the track.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Buffers are freed, returning the track to
    /// <see cref="MusicState.Loaded"/>, only when playback is not also
    /// retaining them. If the track is currently playing or paused,
    /// the memory is kept and released by the eventual
    /// <see cref="Stop"/>.
    /// </para>
    /// <para>
    /// Safe to call when nothing was prepared, and safe on an
    /// <see cref="MusicState.Unloaded"/> track (no-op). Must not block
    /// the calling thread: native teardown is queued, not performed
    /// inline.
    /// </para>
    /// </remarks>
    void Unprepare();

    /// <summary>
    /// Stops playback and releases the playback retain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Buffers are freed, returning the track to
    /// <see cref="MusicState.Loaded"/>, only when no prepare retain is
    /// outstanding. If <see cref="Prepare"/> was called and not yet
    /// unprepared, the track settles into
    /// <see cref="MusicState.Prepared"/> so the next
    /// <see cref="Play"/> remains latency-free.
    /// </para>
    /// <para>
    /// Also cancels any play request recorded while the track was
    /// preparing. Implementations must make
    /// <see cref="IsPlaying"/> report <c>false</c>
    /// before returning, then queue the actual native teardown so the
    /// calling thread is never blocked.
    /// </para>
    /// </remarks>
    void Stop();

    /// <summary>
    /// Pauses playback, retaining all buffers and the
    /// playback position so a subsequent <see cref="Play"/> resumes in
    /// place.
    /// </summary>
    /// <remarks>
    /// Does not release either retain, and therefore never frees
    /// memory. Must not block the calling thread; this is relied upon
    /// by the platform lifecycle and audio-focus sweeps, which run on
    /// ANR-sensitive threads.
    /// </remarks>
    void Pause();
}
