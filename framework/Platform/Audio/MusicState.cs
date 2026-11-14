// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Platform.Audio;

/// <summary>
/// The lifecycle state of a <see cref="DotGame.Framework.Audio.Music"/> track. The state
/// determines how much memory the track currently holds, from
/// nothing at all (<see cref="Unloaded"/>) through metadata-only
/// (<see cref="Loaded"/>) to fully buffered and rendering
/// (<see cref="Playing"/>).
/// </summary>
/// <remarks>
/// <para>
/// Music is optimized for low memory overhead: by default a track
/// holds only its metadata and allocates decode/playback buffers
/// when <see cref="DotGame.Framework.Audio.Music.Play"/> is called, freeing them
/// again when <see cref="DotGame.Framework.Audio.Music.Stop"/> is called.
/// </para>
/// <para>
/// <see cref="DotGame.Framework.Audio.Music.PrepareAsync"/> and
/// <see cref="DotGame.Framework.Audio.Music.Unprepare"/> let a game trade memory for
/// latency by holding those buffers across stop/play cycles. Buffers
/// are retained while <em>either</em> a prepare or an active playback
/// holds them, and are released only when both have been released.
/// </para>
/// </remarks>
internal enum MusicState
{
    /// <summary>
    /// Nothing is resident. The track has either not been loaded yet
    /// or has been disposed/unloaded by the content service. All
    /// playback operations are safe no-ops in this state.
    /// </summary>
    Unloaded = 0,

    /// <summary>
    /// Metadata only: duration, sample rate, channel count, and the
    /// location of the compressed source. No file handles are open,
    /// no decoder exists, and no decode or device buffers are
    /// allocated. This is the state a track occupies immediately
    /// after content load, and the state it returns to once neither a
    /// prepare nor an active playback retains its buffers.
    /// </summary>
    Loaded = 1,

    /// <summary>
    /// Buffers are being acquired on a background thread: fetching,
    /// decoding, and seeking the source and creating the platform
    /// playback objects. No audio is produced in this state.
    /// </summary>
    /// <remarks>
    /// A <see cref="DotGame.Framework.Audio.Music.Play"/> issued while the track is
    /// preparing is recorded and starts automatically as soon as
    /// preparation completes.
    /// </remarks>
    Preparing = 2,

    /// <summary>
    /// Buffers are allocated, primed, and seeked to the requested
    /// start position, but no audio is being produced.
    /// <see cref="DotGame.Framework.Audio.Music.Play"/> starts with minimal latency
    /// from this state.
    /// </summary>
    Prepared = 3,

    /// <summary>
    /// The track is actively producing audio.
    /// </summary>
    Playing = 4,

    /// <summary>
    /// Playback was suspended mid-track. All buffers and the playback
    /// position are retained so <see cref="DotGame.Framework.Audio.Music.Play"/>
    /// resumes exactly where it left off.
    /// </summary>
    Paused = 5,
}
