// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;

namespace DotGame.Framework.Desktop.Audio;

internal sealed class AudioServiceStrategy : IAudioServiceStrategy, IMusicPlaybackListener
{
    // Every music strategy with playback state owned by this service.
    // Tracks are added on Play and removed on Stop. Used by the
    // window hidden/shown suspend/resume sweep so every active track is
    // paused and resumed correctly, regardless of whether the game
    // considers a particular track to be background or a one-shot
    // overlay — the framework intentionally does not distinguish.
    private readonly HashSet<MusicStrategyBase> _activeMusic = new();

    // Subset of _activeMusic that this service paused in response to
    // the window being hidden. Only tracks in this set are
    // auto-resumed when it's shown; tracks the game paused
    // itself stay paused.
    private readonly HashSet<MusicStrategyBase> _autoPausedMusic = new();

    // Every sound voice is mixed here and played through the one output stream, as on Android
    private readonly AudioMixer _mixer = new();
    private readonly AudioOutput _output;

    internal AudioServiceStrategy()
    {
        this._output = new AudioOutput(this._mixer);
    }

    /// <summary>
    /// Ends the sounds' audio thread and closes its stream. Call before SDL quits.
    /// </summary>
    internal void Shutdown()
    {
        this._output.Shutdown();
    }

    public Task OnInitialize()
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool ShouldPlayMusic() => true;

    /// <summary>
    /// Called by the host engine when the game window is hidden. Pauses
    /// every actively-playing music track so the game is silent while
    /// it can't be seen.
    /// </summary>
    internal void OnSystemSuspend()
    {
        // Evict tracks the game disposed (e.g. via content unload) so
        // the sweep never touches a released device stream.
        this.PruneDisposedMusic();

        foreach (var music in this._activeMusic)
        {
            if (music.IsPlaying)
            {
                music.Pause();
                this._autoPausedMusic.Add(music);
            }
        }
    }

    private void PruneDisposedMusic()
    {
        this._activeMusic.RemoveWhere(static music => music.IsDisposed);
        this._autoPausedMusic.RemoveWhere(static music => music.IsDisposed);
    }

    /// <summary>
    /// Called by the host engine when the game window regains focus.
    /// Resumes only the music tracks we paused ourselves.
    /// </summary>
    internal void OnSystemResume()
    {
        this.PruneDisposedMusic();

        if (this._autoPausedMusic.Count == 0)
        {
            return;
        }

        // Play with no parameters resumes a paused track in place and
        // returns without blocking.
        foreach (var music in this._autoPausedMusic)
        {
            music.Play(null, null, null, false);
        }

        this._autoPausedMusic.Clear();
    }

    public Strategy<IVolumeControlStrategy> MasterVolumeControl { get; } = new Strategy<IVolumeControlStrategy>(PolledVolumeControlStrategy.Master);

    public Strategy<IVolumeControlStrategy> CreateVolumeControl()
    {
        return new Strategy<IVolumeControlStrategy>(new PolledVolumeControlStrategy());
    }

    public Strategy<IVoiceStrategy> CreateVoice(
        Strategy<ISoundStrategy> sound,
        TimeSpan offset,
        TimeSpan duration)
    {
        var voice = new MixerVoiceStrategy(
            this._mixer,
            sound.ToConcrete<MixerSoundStrategy>(),
            AudioOutput.SampleRate,
            offset,
            duration);
        return new Strategy<IVoiceStrategy>(voice);
    }

    bool IMusicPlaybackListener.CanPlay(
        MusicStrategyBase music)
    {
        return true;
    }

    void IMusicPlaybackListener.OnPlay(
        MusicStrategyBase music)
    {
        this._activeMusic.Add(music);
        this._autoPausedMusic.Remove(music);
    }

    void IMusicPlaybackListener.OnStop(
        MusicStrategyBase music)
    {
        this._activeMusic.Remove(music);
        this._autoPausedMusic.Remove(music);
    }

    void IMusicPlaybackListener.OnPause(
        MusicStrategyBase music)
    {
        // A game-initiated pause must not be auto-resumed on focus
        // change. Keep the track in _activeMusic so a later system
        // event still sees it, but remove it from the auto-paused
        // set so OnSystemResume leaves it alone.
        this._autoPausedMusic.Remove(music);
    }
}
