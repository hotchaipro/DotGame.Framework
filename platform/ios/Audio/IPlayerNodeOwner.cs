// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.iOS.Audio;

/// <summary>
/// A sound voice or music track holding a player node from the audio
/// service's pool.
/// </summary>
internal interface IPlayerNodeOwner
{
    /// <summary>
    /// Whether the owner is producing audio, so the service's volume
    /// thread keeps applying its volume.
    /// </summary>
    bool IsRendering { get; }

    /// <summary>
    /// Sets the node's volume from the owner's volume control. Called
    /// on the game thread and on the service's volume thread.
    /// </summary>
    void ApplyVolume();

    /// <summary>
    /// The engine restarted after an interruption or a configuration
    /// change stopped it. Called on the audio thread.
    /// </summary>
    void OnEngineRestarted();

    /// <summary>
    /// The owner's node was just attached to the running output: when
    /// the output first opens, for a slot created since, or for the
    /// new node after a media-services reset. Called on the audio
    /// thread.
    /// </summary>
    void OnNodeWired();
}
