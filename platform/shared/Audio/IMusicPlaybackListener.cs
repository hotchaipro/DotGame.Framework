// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Platform.Common.Audio;

/// <summary>
/// Observes the playback a game asks of its music tracks, for a platform audio service that
/// pauses and resumes them around system events (the app going to the background, another app
/// taking audio focus). A track created with a listener reports each game request to it;
/// the service's own pauses and resumes call the track directly and aren't reported.
/// </summary>
internal interface IMusicPlaybackListener
{
    /// <summary>
    /// Whether the game may start the track now. A track the listener refuses doesn't play,
    /// and isn't reported to <see cref="OnPlay"/>.
    /// </summary>
    bool CanPlay(
        MusicStrategyBase music);

    /// <summary>
    /// The game started or resumed the track.
    /// </summary>
    void OnPlay(
        MusicStrategyBase music);

    /// <summary>
    /// The game paused the track, which the service must then not resume on its own.
    /// </summary>
    void OnPause(
        MusicStrategyBase music);

    /// <summary>
    /// The track stopped: the game stopped it, or it reached its end.
    /// </summary>
    void OnStop(
        MusicStrategyBase music);
}
