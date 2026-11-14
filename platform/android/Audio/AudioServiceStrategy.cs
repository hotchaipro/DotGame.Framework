// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.App;
using Android.OS;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;
using AndroidApp = global::Android.App.Application;

namespace DotGame.Framework.Android.Audio;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The audio service lives for the lifetime of the app.")]
internal sealed class AudioServiceStrategy : IAudioServiceStrategy, IMusicPlaybackListener
{
    // Every music strategy with playback state owned by this service.
    // Tracks are added on Play and removed on Stop. Used by the
    // activity-lifecycle suspend/resume sweep so every active track
    // is paused and resumed correctly, regardless of whether the game
    // considers a particular track to be background or a one-shot
    // overlay — the framework intentionally does not distinguish.
    private readonly HashSet<MusicStrategyBase> _activeMusic = new();

    // Subset of _activeMusic that this service paused in response to
    // the activity moving to the background. Only tracks in this set
    // are auto-resumed when the activity returns to the foreground;
    // tracks the game paused itself stay paused.
    private readonly HashSet<MusicStrategyBase> _autoPausedMusic = new();

    // Tracks paused by losing audio focus, or left paused at resume because other audio was
    // playing, so they can be resumed once focus is available again.
    private readonly HashSet<MusicStrategyBase> _focusPausedMusic = new();

    // Guards the three sets: the game thread changes them as music plays and stops, and the
    // UI thread sweeps them on lifecycle and focus events. Not held while calling into music,
    // which reports back through IMusicPlaybackListener.
    private readonly System.Threading.Lock _musicLock = new();

    // Every sound effect plays through one mixed output stream; see
    // docs/architecture/audio.md
    private readonly AudioMixer _mixer = new();
    private readonly AudioOutput _output;

    private AudioLifecycleCallbacks? _lifecycleCallbacks;
    // Guards taking and releasing focus, which the game and UI threads both do
    private readonly System.Threading.Lock _focusLock = new();

    private AudioFocusChangeListener? _focusChangeListener;
    private OtherAudioWatcher? _otherAudioWatcher;
    private global::Android.Media.AudioManager? _audioManager;
    private global::Android.Media.AudioFocusRequestClass? _audioFocusRequest;
    private volatile FocusState _focusState;
    private volatile bool _isInBackground;
    private volatile bool _isOtherAudioPlaying;

    private enum FocusState
    {
        // The game holds no focus, and other apps' audio is theirs to keep
        None,
        Held,
        // Another app has it for now, and the system will hand it back
        LostForNow,
    }

    private readonly Activity _hostActivity;

    internal AudioServiceStrategy(
        Activity hostActivity)
    {
        this._hostActivity = hostActivity;
        this._output = new AudioOutput(this._mixer);
    }

    /// <summary>
    /// Ends the service, when the game stops: an activity's recreate starts a new one, and this
    /// one mustn't go on reacting to lifecycle events, holding focus or holding its stream.
    /// </summary>
    internal void Shutdown()
    {
        if (AndroidApp.Context.ApplicationContext is AndroidApp app && (this._lifecycleCallbacks is not null))
        {
            app.UnregisterActivityLifecycleCallbacks(this._lifecycleCallbacks);
            this._lifecycleCallbacks = null;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(26) && (this._otherAudioWatcher is not null))
        {
            this._audioManager?.UnregisterAudioPlaybackCallback(this._otherAudioWatcher);
            this._otherAudioWatcher = null;
        }

        this.ReleaseAudioFocus();
        this._output.Shutdown();
    }

    public Task OnInitialize()
    {
        var context = AndroidApp.Context;
        this._audioManager = context.GetSystemService(
            global::Android.Content.Context.AudioService)
            as global::Android.Media.AudioManager;

        // Register activity lifecycle callbacks so active music can be
        // paused when the user leaves the app and resumed when they
        // return.
        if (context.ApplicationContext is AndroidApp app)
        {
            this._lifecycleCallbacks = new AudioLifecycleCallbacks(this);
            app.RegisterActivityLifecycleCallbacks(this._lifecycleCallbacks);
        }

        // Focus is taken only when music starts: starting a game shouldn't stop what the player
        // is listening to, and sound effects mix with it. See docs/architecture/audio.md
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && (this._audioManager is { } manager))
        {
            var audioAttributes = new global::Android.Media.AudioAttributes.Builder()!
                .SetUsage(global::Android.Media.AudioUsageKind.Game)!
                .Build()!;

            this._focusChangeListener = new AudioFocusChangeListener(this);
            this._audioFocusRequest = new global::Android.Media.AudioFocusRequestClass.Builder(
                global::Android.Media.AudioFocus.Gain)!
                .SetAudioAttributes(audioAttributes)!
                .SetOnAudioFocusChangeListener(this._focusChangeListener)!
                .Build()!;

            this._otherAudioWatcher = new OtherAudioWatcher(this);
            manager.RegisterAudioPlaybackCallback(this._otherAudioWatcher, null);
            this._isOtherAudioPlaying = IsOtherAudioPlaying(manager.ActivePlaybackConfigurations);
        }

        return Task.CompletedTask;
    }

    // Takes focus for music about to play, and says whether it may. Without the focus API
    // (before Android 8) music plays regardless.
    private bool TryTakeAudioFocus()
    {
        if (this._isInBackground)
        {
            return false;
        }

        var manager = this._audioManager;
        var request = this._audioFocusRequest;
        if ((manager is null) || (request is null) || !OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return true;
        }

        lock (this._focusLock)
        {
            switch (this._focusState)
            {
                case FocusState.Held:
                    return true;

                case FocusState.LostForNow:
                    return false;
            }

            if (this._isOtherAudioPlaying)
            {
                return false;
            }

            if (manager.RequestAudioFocus(request) != global::Android.Media.AudioFocusRequest.Granted)
            {
                return false;
            }

            this._focusState = FocusState.Held;
            return true;
        }
    }

    private void ReleaseAudioFocus()
    {
        var manager = this._audioManager;
        var request = this._audioFocusRequest;
        if ((manager is null) || (request is null) || !OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        lock (this._focusLock)
        {
            if (this._focusState != FocusState.None)
            {
                this._focusState = FocusState.None;
                manager.AbandonAudioFocusRequest(request);
            }
        }
    }

    // Resumes the tracks waiting for focus, if it's available
    private void ResumeFocusPausedMusic()
    {
        MusicStrategyBase[] pausedMusic;
        lock (this._musicLock)
        {
            // Evict tracks the game disposed so a stale reference is never
            // resurrected by the resume sweep below.
            this.PruneDisposedMusic();

            if (this._focusPausedMusic.Count == 0)
            {
                return;
            }

            pausedMusic = [.. this._focusPausedMusic];
        }

        if (!this.TryTakeAudioFocus())
        {
            return;
        }

        lock (this._musicLock)
        {
            this._focusPausedMusic.Clear();
        }

        // Play with no parameters resumes a paused track in place and returns immediately, so
        // this sweep can't stall a lifecycle callback.
        foreach (var music in pausedMusic)
        {
            if (!music.IsPlaying)
            {
                music.Play(null, null, null, false);
            }
        }
    }

    private void OnAudioFocusGain()
    {
        lock (this._focusLock)
        {
            // Focus was given up while this callback was on its way
            if (this._focusState != FocusState.LostForNow)
            {
                return;
            }

            this._focusState = FocusState.Held;
        }

        this.ResumeFocusPausedMusic();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private static bool IsOtherAudioPlaying(
        IList<global::Android.Media.AudioPlaybackConfiguration>? configurations)
    {
        if (configurations is null)
        {
            return false;
        }

        // The list has only active players, the game's own among them. Those use USAGE_GAME, so
        // they don't count here, and neither do another app's game sounds or notifications. An
        // ad's video, which the game shows, counts: music waits for it.
        foreach (var configuration in configurations)
        {
            var usage = configuration.AudioAttributes?.Usage;
            if ((usage == global::Android.Media.AudioUsageKind.Media)
                || (usage == global::Android.Media.AudioUsageKind.VoiceCommunication))
            {
                return true;
            }
        }

        return false;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private void OnPlaybackConfigurationsChanged(
        IList<global::Android.Media.AudioPlaybackConfiguration>? configurations)
    {
        bool wasPlaying = this._isOtherAudioPlaying;
        bool isPlaying = IsOtherAudioPlaying(configurations);
        this._isOtherAudioPlaying = isPlaying;

        // Only when other audio has actually ended. Losing focus to an app that hasn't begun to
        // play reports nothing playing too (the game's own pause changes the list), and taking
        // focus then would stop that app before it starts.
        if (wasPlaying && !isPlaying)
        {
            this.ResumeFocusPausedMusic();
        }
    }

    private void OnAudioFocusLoss(
        bool isPermanent)
    {
        lock (this._focusLock)
        {
            // Focus was given up while this callback was on its way
            if (this._focusState != FocusState.Held)
            {
                return;
            }

            this._focusState = FocusState.LostForNow;
        }

        // The system gives no focus back after a permanent loss, so nothing is held any more
        if (isPermanent)
        {
            this.ReleaseAudioFocus();
        }

        MusicStrategyBase[] activeMusic;
        lock (this._musicLock)
        {
            // Evict tracks the game disposed so the sweep below never touches
            // a released native decoder.
            this.PruneDisposedMusic();

            activeMusic = [.. this._activeMusic];
        }

        // Pause any actively playing music due to focus loss
        foreach (var music in activeMusic)
        {
            if (music.IsPlaying)
            {
                music.Pause();

                lock (this._musicLock)
                {
                    this._focusPausedMusic.Add(music);
                }
            }
        }
    }

    /// <inheritdoc />
    public bool ShouldPlayMusic()
    {
        // Holding focus means no other audio is in the way. Without it, music would stop whatever
        // the player is listening to.
        return this._focusState switch
        {
            FocusState.Held => true,
            FocusState.LostForNow => false,
            _ => (!this._isInBackground) && (!this._isOtherAudioPlaying),
        };
    }

    // Removes any music the game has disposed (e.g. via content unload)
    // from the tracking sets. The service holds direct references to
    // MusicStrategy instances rather than the higher-level Music asset, so
    // a disposed track would otherwise linger and be resurrected by a
    // suspend/resume or audio-focus sweep, dereferencing its already
    // released native MediaExtractor/MediaCodec. The caller holds _musicLock.
    private void PruneDisposedMusic()
    {
        this._activeMusic.RemoveWhere(static music => music.IsDisposed);
        this._autoPausedMusic.RemoveWhere(static music => music.IsDisposed);
        this._focusPausedMusic.RemoveWhere(static music => music.IsDisposed);
    }

    private void OnSystemSuspend()
    {
        this._isInBackground = true;
        this._output.Suspend();

        MusicStrategyBase[] activeMusic;
        lock (this._musicLock)
        {
            // Evict tracks the game disposed (e.g. via content unload) so a
            // stale reference is neither promoted below nor resurrected by a
            // later resume sweep.
            this.PruneDisposedMusic();

            // If audio focus was lost before the activity paused (e.g. the ad
            // SDK preempted focus before Android fired OnActivityPaused), the
            // affected tracks are in _focusPausedMusic but NOT in
            // _autoPausedMusic.  Promote them now so OnSystemResume has the
            // complete set to restore, regardless of which event fired first.
            foreach (var music in this._focusPausedMusic)
            {
                this._autoPausedMusic.Add(music);
            }

            this._focusPausedMusic.Clear();

            activeMusic = [.. this._activeMusic];
        }

        // Abandon audio focus when app goes to background
        this.ReleaseAudioFocus();

        // OnActivityPaused is ANR-sensitive. Pause() only flips the
        // render gate and calls AudioTrack.Pause(), retaining every
        // buffer; the native teardown that could block is never
        // triggered from here.
        foreach (var music in activeMusic)
        {
            if (music.IsPlaying)
            {
                music.Pause();

                lock (this._musicLock)
                {
                    this._autoPausedMusic.Add(music);
                }
            }
        }
    }

    private void OnSystemResume()
    {
        this._isInBackground = false;

        lock (this._musicLock)
        {
            // Tracks paused by this service come back, unless the player has started listening
            // to something else meanwhile: they then wait in the focus set until it ends.
            foreach (var music in this._autoPausedMusic)
            {
                this._focusPausedMusic.Add(music);
            }

            this._autoPausedMusic.Clear();
        }

        this._output.Resume();

        // With nothing to resume no focus is taken: music the game plays later takes its own
        this.ResumeFocusPausedMusic();
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
        var voice = new VoiceStrategy(
            this._mixer,
            sound.ToConcrete<SoundStrategy>(),
            AudioOutput.SampleRate,
            offset,
            duration);
        return new Strategy<IVoiceStrategy>(voice);
    }

    bool IMusicPlaybackListener.CanPlay(
        MusicStrategyBase music)
    {
        // Refused in the background, while another app holds focus and while other audio plays,
        // whether the game checked ShouldPlayMusic first or not. The track is dropped
        // rather than queued: the engine's music player plays again when that says it may, and
        // a missed one-shot ident is safe to skip.
        return this.TryTakeAudioFocus();
    }

    void IMusicPlaybackListener.OnPlay(
        MusicStrategyBase music)
    {
        lock (this._musicLock)
        {
            this._activeMusic.Add(music);
            this._autoPausedMusic.Remove(music);
            this._focusPausedMusic.Remove(music);
        }
    }

    void IMusicPlaybackListener.OnStop(
        MusicStrategyBase music)
    {
        bool isLast;
        lock (this._musicLock)
        {
            this._activeMusic.Remove(music);
            this._autoPausedMusic.Remove(music);
            this._focusPausedMusic.Remove(music);
            isLast = (this._activeMusic.Count == 0);
        }

        // No music is left to hold focus for
        if (isLast)
        {
            this.ReleaseAudioFocus();
        }
    }

    void IMusicPlaybackListener.OnPause(
        MusicStrategyBase music)
    {
        // A game-initiated pause must not be auto-resumed when the
        // activity returns to the foreground or when audio focus is
        // regained. Keep the track in _activeMusic so a later system
        // event still sees it, but remove it from both auto-paused sets.
        lock (this._musicLock)
        {
            this._autoPausedMusic.Remove(music);
            this._focusPausedMusic.Remove(music);
        }
    }

    /// <summary>
    /// Listens for audio focus changes from other apps. When another app
    /// requests audio focus (e.g., displaying an interstitial ad), this
    /// listener is notified immediately, allowing the app to pause music
    /// without delay.
    /// </summary>
    private sealed class AudioFocusChangeListener
        : Java.Lang.Object,
          global::Android.Media.AudioManager.IOnAudioFocusChangeListener
    {
        private readonly AudioServiceStrategy _service;

        internal AudioFocusChangeListener(
            AudioServiceStrategy service)
        {
            this._service = service;
        }

        public void OnAudioFocusChange(
            [global::Android.Runtime.GeneratedEnum] global::Android.Media.AudioFocus focusChange)
        {
            switch (focusChange)
            {
                case global::Android.Media.AudioFocus.Gain:
                case global::Android.Media.AudioFocus.GainTransient:
                case global::Android.Media.AudioFocus.GainTransientMayDuck:
                    // We have (regained) audio focus
                    this._service.OnAudioFocusGain();
                    break;

                case global::Android.Media.AudioFocus.Loss:
                    // Another app has taken over; pause music
                    this._service.OnAudioFocusLoss(isPermanent: true);
                    break;

                case global::Android.Media.AudioFocus.LossTransient:
                case global::Android.Media.AudioFocus.LossTransientCanDuck:
                    // We lost audio focus for now; pause music
                    this._service.OnAudioFocusLoss(isPermanent: false);
                    break;
            }
        }
    }

    /// <summary>
    /// Tells the service when audio starts or stops anywhere on the device, so that
    /// <see cref="ShouldPlayMusic"/> needs no call into the system each frame.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private sealed class OtherAudioWatcher
        : global::Android.Media.AudioManager.AudioPlaybackCallback
    {
        private readonly AudioServiceStrategy _service;

        internal OtherAudioWatcher(
            AudioServiceStrategy service)
        {
            this._service = service;
        }

        public override void OnPlaybackConfigChanged(
            IList<global::Android.Media.AudioPlaybackConfiguration>? configs)
        {
            this._service.OnPlaybackConfigurationsChanged(configs);
        }
    }

    /// <summary>
    /// Routes the host activity's pause/resume callbacks to the audio
    /// service so background music can be suspended without the game
    /// engine having to participate.
    /// </summary>
    private sealed class AudioLifecycleCallbacks
        : Java.Lang.Object,
          AndroidApp.IActivityLifecycleCallbacks
    {
        private readonly AudioServiceStrategy _service;

        internal AudioLifecycleCallbacks(
            AudioServiceStrategy service)
        {
            this._service = service;
        }

        public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) { }
        public void OnActivityStarted(Activity activity) { }

        // NOTE: Only the game's activity: an ad's or a billing screen's resume over it mustn't
        // bring the music back behind it
        public void OnActivityResumed(Activity activity)
        {
            if (activity == this._service._hostActivity)
            {
                this._service.OnSystemResume();
            }
        }

        public void OnActivityPaused(Activity activity)
        {
            if (activity == this._service._hostActivity)
            {
                this._service.OnSystemSuspend();
            }
        }

        public void OnActivityStopped(Activity activity) { }
        public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }
        public void OnActivityDestroyed(Activity activity) { }
    }
}
