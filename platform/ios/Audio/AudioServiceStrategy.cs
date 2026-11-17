// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AudioToolbox;
using AVFoundation;
using CoreFoundation;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;
using Foundation;
using UIKit;

namespace DotGame.Framework.iOS.Audio;

/// <summary>
/// iOS audio backend. One background thread owns the audio system:
/// it activates the session, proves that audio IO is running, and
/// only then builds the sound engine, so a game runs silently instead
/// of aborting when the audio server is unavailable or stalled.
/// </summary>
/// <remarks>
/// <para>
/// <c>AURemoteIO</c> (the output unit behind every
/// <see cref="AVAudioEngine"/>) aborts the process when an RPC to a
/// stalled audio server times out, on whichever thread made the call.
/// Moving engine calls off the game thread is therefore not enough:
/// the engine must not be touched until a probe that waits rather
/// than aborts (a silent <see cref="OutputAudioQueue"/>) has played a
/// buffer. See <c>docs/architecture/audio.md</c>.
/// </para>
/// <para>
/// Until the output is open, voices are silent no-ops. Music doesn't
/// wait for it: preparing and playing complete as usual, and a track
/// is heard once the audio thread has wired its node to the engine.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The audio service lives for the lifetime of the app.")]
internal sealed class AudioServiceStrategy : IAudioServiceStrategy, IMusicPlaybackListener
{
    // How long the audio thread waits before probing again while the
    // output is down and nothing else has woken it.
    private static readonly TimeSpan ProbeRetryInterval = TimeSpan.FromSeconds(5);

    private const int ProbeBufferBytes = 4096;

    // How often the volume thread applies each playing voice's and
    // track's volume to its node: about once a frame, so fades sound
    // smooth.
    private static readonly TimeSpan VolumeInterval = TimeSpan.FromMilliseconds(15);

    // Every music strategy with playback state owned by this service.
    // Tracks are added on Play and removed on Stop. Used by the
    // app-lifecycle and audio-session interruption sweeps so every
    // active track is paused and resumed correctly, regardless of
    // whether the game considers a particular track to be background
    // or a one-shot overlay — the framework intentionally does not
    // distinguish. Main thread only.
    private readonly HashSet<MusicStrategyBase> _activeMusic = new();

    // Subset of _activeMusic that this service paused in response to
    // a system event (background, AVAudioSession interruption). Only
    // tracks in this set are auto-resumed; tracks the game paused
    // itself stay paused. Main thread only.
    private readonly HashSet<MusicStrategyBase> _autoPausedMusic = new();

    private NSObject? _didEnterBackgroundObserver;
    private NSObject? _willEnterForegroundObserver;
    private NSObject? _didBecomeActiveObserver;
    private NSObject? _interruptionObserver;
    private NSObject? _routeChangeObserver;
    private NSObject? _mediaServicesResetObserver;
    private NSObject? _silenceHintObserver;
    private NSObject? _configurationChangeObserver;

    // Requests from the main thread to the audio thread, guarded by
    // _requestLock. The audio thread takes them each time it wakes.
    private readonly Lock _requestLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private bool _suspended;
    private bool _restartRequested;
    private bool _resetRequested;
    private bool _resumeMusicRequested;

    // Between an interruption's Began and Ended, when activating the session can't work
    private bool _interrupted;

    // Slots given up, whose nodes the audio thread stops before they're free again:
    // AVAudioPlayerNode.Stop can wait on the audio server
    private readonly List<int> _releasedSlots = new();

    // Player nodes to start, from the game thread: AVAudioPlayerNode.Play
    // waits for the output's next IO cycle, which a stalled audio server
    // never brings (it waits about 2 s, then throws "player did not see
    // an IO cycle"), so only the audio thread starts nodes. Each start
    // asks whether it's still wanted, as the voice or track may have
    // stopped since.
    private readonly List<(int Slot, Func<bool> IsWanted)> _pendingStarts = new();

    // An interruption deactivated the session behind the audio
    // thread's back.
    private bool _sessionLost;

    private volatile bool _outputAvailable;
    private volatile bool _silenceSecondaryAudio;

    // Audio thread only.
    private AVAudioEngine? _engine;
    private bool _sessionActive;

    // Engines and nodes left behind by a media-services reset. They
    // belong to the dead audio server, so they're never called again,
    // and they're never released either: a release could reach the
    // server from the finalizer. Resets are rare enough for this to
    // stay small.
    private readonly List<NSObject> _abandoned = new();

    // Pool of player nodes, shared by sound voices and music tracks,
    // that grows on demand. Nodes are never removed — detaching nodes
    // while the audio render thread is active causes native crashes
    // in AUScheduledSoundPlayer — and each is connected once with its
    // slot's format, so a free slot is only reused for the same
    // format. The slot pool only has to model node lifetime; any
    // per-cue instance limiting lives in higher layers. The array is
    // replaced (never resized in place) under _requestLock, so the
    // audio thread can wire a snapshot while the pool grows.
    private sealed class PlayerSlot(AVAudioPlayerNode node, AVAudioFormat format, AVAudioUnitVarispeed? rateUnit)
    {
        public volatile AVAudioPlayerNode Node = node;
        public readonly AVAudioFormat Format = format;

        // Between the node and the mixer in a slot whose voice plays at a rate other than as
        // recorded, and null in every other slot. Connected once, with the node.
        public volatile AVAudioUnitVarispeed? RateUnit = rateUnit;

        // Set by the audio thread once the node is attached to the
        // running engine; owners treat an unwired node as absent.
        public volatile bool Wired;

        // The voice or track that holds the slot, for the volume
        // thread and engine restarts. Guarded by _requestLock with
        // InUse.
        public volatile IPlayerNodeOwner? Owner;
        public bool InUse;
    }

    // Set when a voice or track starts playing, to wake the volume
    // thread.
    private readonly AutoResetEvent _playbackStarted = new(false);

    private PlayerSlot[] _slots = [];

    private readonly NSString _category;

    internal AudioServiceStrategy(
        AudioCategory category)
    {
        this._category = category switch
        {
            AudioCategory.SoloAmbient => AVAudioSessionCategory.SoloAmbient.GetConstant()!,
            AudioCategory.Playback => AVAudioSessionCategory.Playback.GetConstant()!,
            _ => AVAudioSessionCategory.Ambient.GetConstant()!,
        };
    }

    public Task OnInitialize()
    {
        var center = NSNotificationCenter.DefaultCenter;

        this._didEnterBackgroundObserver = center.AddObserver(
            UIApplication.DidEnterBackgroundNotification,
            _ => this.OnSystemSuspend());

        this._willEnterForegroundObserver = center.AddObserver(
            UIApplication.WillEnterForegroundNotification,
            _ => this.OnSystemResume());

        this._didBecomeActiveObserver = center.AddObserver(
            UIApplication.DidBecomeActiveNotification,
            _ => this.OnBecameActive());

        this._interruptionObserver = AVAudioSession.Notifications.ObserveInterruption(
            (_, args) => this.OnAudioSessionInterruption(args));

        // A new route, a reset, or a changed hint can each mean the
        // output is usable again; the audio thread decides.
        this._routeChangeObserver = AVAudioSession.Notifications.ObserveRouteChange(
            (_, _) => this._wake.Set());

        this._mediaServicesResetObserver = AVAudioSession.Notifications.ObserveMediaServicesWereReset(
            (_, _) => this.RequestReset());

        this._silenceHintObserver = AVAudioSession.Notifications.ObserveSilenceSecondaryAudioHint(
            (_, args) => this._silenceSecondaryAudio =
                args.Hint == AVAudioSessionSilenceSecondaryAudioHintType.Begin);

        // Session activation, the IO probe and the engine all run on
        // the audio thread; initialization never waits for them.
        var thread = new Thread(this.RunAudioThread)
        {
            IsBackground = true,
            Name = "DotGame Audio",
        };

        thread.Start();

        var volumeThread = new Thread(this.RunVolumeThread)
        {
            IsBackground = true,
            Name = "DotGame Audio Volume",
        };

        volumeThread.Start();

        return Task.CompletedTask;
    }

    #region Audio thread

    private void RunAudioThread()
    {
        while (true)
        {
            bool settled = false;

            // NOTE: AVAudioEngine reports misuse by raising exceptions, and one escaping this
            // thread would end the process, when the point of this thread is that a game runs
            // silently instead. The retry below tries again.
            nint pool = ObjCAutoreleasePool.Push();
            try
            {
                settled = this.RunAudioThreadPass();
            }
            catch (Exception exception)
            {
                GameHost.ReportAudioFailure(0x3fa81c65, exception);
            }
            finally
            {
                ObjCAutoreleasePool.Pop(pool);
            }

            // Activating around a foreground transition or just after a call can fail, with
            // nothing to wake this thread afterwards, so it keeps trying until audio runs
            this._wake.WaitOne(settled ? Timeout.InfiniteTimeSpan : ProbeRetryInterval);
        }
    }

    // One pass over what's been asked of the audio thread; returns whether there's nothing to try
    // again: audio is suspended or interrupted, or running
    private bool RunAudioThreadPass()
    {
        bool suspended;
        bool interrupted;
        bool restart;
        bool reset;

        lock (this._requestLock)
        {
            suspended = this._suspended;
            interrupted = this._interrupted;
            restart = this._restartRequested;
            reset = this._resetRequested;
            this._restartRequested = false;
            this._resetRequested = false;

            if (this._sessionLost)
            {
                this._sessionLost = false;
                this._sessionActive = false;
            }
        }

        if (reset)
        {
            this.AbandonOutput();
        }

        this.StopReleasedNodes();

        if (suspended)
        {
            this.DeactivateSession();
        }
        else if (interrupted)
        {
            // The system holds the session until the interruption ends
        }
        else if (this.ActivateSession())
        {
            if (!this._outputAvailable)
            {
                if (this.ProbeOutput())
                {
                    this.OpenOutput();
                }
            }
            else if (restart || !this._engine!.Running)
            {
                this.RestartEngine();
            }

            if (this._outputAvailable)
            {
                this.WireSlots();
                this.StartPendingNodes();
                this.PostMusicResume();
            }
        }

        return suspended || interrupted || (this._outputAvailable && this._sessionActive && this._engine!.Running);
    }

    // Activates the session for game audio, in the game's category.
    // The default, Ambient, mixes with other apps (so the user's music
    // keeps playing) and respects the ringer/silent switch; it's also
    // what makes SecondaryAudioShouldBeSilencedHint meaningful.
    private bool ActivateSession()
    {
        if (this._sessionActive)
        {
            return true;
        }

        var session = AVAudioSession.SharedInstance();
        session.SetCategory(this._category);

        var error = session.SetActive(true);
        if (error is not null)
        {
            Trace.Write(0x3c8e51a2,
                $"[AudioServiceStrategy] Session activation failed: {error.LocalizedDescription}");
            return false;
        }

        this._sessionActive = true;
        this._silenceSecondaryAudio = session.SecondaryAudioShouldBeSilencedHint;
        return true;
    }

    private void DeactivateSession()
    {
        if (!this._sessionActive)
        {
            return;
        }

        this._engine?.Pause();

        // A failure leaves the session active, as the state says; the OS cleans up when the app
        // is fully suspended.
        var error = AVAudioSession.SharedInstance().SetActive(
            false,
            AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation);
        if (error is not null)
        {
            Trace.Write(0x2d7ae4c9,
                $"[AudioServiceStrategy] Session deactivation failed: {error.LocalizedDescription}");
            return;
        }

        this._sessionActive = false;
    }

    /// <summary>
    /// Plays a silent buffer through an audio queue and waits for it
    /// to finish, which proves that IO cycles are running. Blocks the
    /// audio thread for as long as IO is stalled; returns
    /// <c>false</c> when the queue can't start at all.
    /// </summary>
    private bool ProbeOutput()
    {
        using var played = new ManualResetEventSlim(false);

        var format = AudioStreamBasicDescription.CreateLinearPCM(44100, 2, 16, false);
        using var queue = new OutputAudioQueue(format);
        queue.BufferCompleted += (_, _) => played.Set();

        for (int i = 0; i < 2; i++)
        {
            IntPtr buffer;
            unsafe
            {
                if (queue.AllocateBuffer(ProbeBufferBytes, out AudioQueueBuffer* data) != AudioQueueStatus.Ok)
                {
                    return false;
                }

                buffer = (IntPtr)data;
                new Span<byte>((void*)data->AudioData, ProbeBufferBytes).Clear();
            }

            queue.EnqueueBuffer(buffer, ProbeBufferBytes, []);
        }

        // Start and the first completion are where a stalled audio
        // server makes the probe wait. Neither aborts, unlike the
        // engine's output unit.
        var status = queue.Start();
        if (status != AudioQueueStatus.Ok)
        {
            Trace.Write(0x5a07c3e4,
                $"[AudioServiceStrategy] Audio output unavailable ({status}); running silently.");
            return false;
        }

        if (!played.Wait(ProbeRetryInterval))
        {
            Trace.Write(0x6e2b9d10,
                "[AudioServiceStrategy] Audio output stalled; running silently until it recovers.");

            // A reset can leave the queue never completing, so this looks up now and then
            while (!played.Wait(ProbeRetryInterval))
            {
                lock (this._requestLock)
                {
                    if (this._suspended || this._resetRequested)
                    {
                        return false;
                    }
                }
            }
        }

        queue.Stop(true);
        return true;
    }

    private void OpenOutput()
    {
        var engine = new AVAudioEngine();

        // Builds the output node and the mixer graph, the first touch
        // of AURemoteIO.
        _ = engine.MainMixerNode;

        engine.StartAndReturnError(out var error);
        if (error is not null)
        {
            Trace.Write(0x1f4d6a83,
                $"[AudioServiceStrategy] Failed to start AVAudioEngine: {error.LocalizedDescription}");
            engine.Dispose();
            return;
        }

        this._configurationChangeObserver = AVAudioEngine.Notifications.ObserveConfigurationChange(
            engine,
            (_, _) => this.RequestRestart());

        this._engine = engine;
        this._outputAvailable = true;

        Trace.Write(0x7b90e215, "[AudioServiceStrategy] Audio output open.");
    }

    // Restarts the engine after an interruption or a configuration
    // change stopped it, then lets each owner resume its node.
    private void RestartEngine()
    {
        this._engine!.StartAndReturnError(out var error);
        if (error is not null)
        {
            Trace.Write(0x48d1f0b6,
                $"[AudioServiceStrategy] Failed to restart AVAudioEngine: {error.LocalizedDescription}");
            return;
        }

        PlayerSlot[] slots;
        lock (this._requestLock)
        {
            slots = this._slots;
        }

        foreach (var slot in slots)
        {
            if (slot.Wired)
            {
                slot.Owner?.OnEngineRestarted();
            }
        }
    }

    // Attaches and connects every slot's node that isn't wired yet and
    // tells its owner, which may have audio waiting for the node.
    private void WireSlots()
    {
        var engine = this._engine!;

        PlayerSlot[] slots;
        lock (this._requestLock)
        {
            slots = this._slots;
        }

        foreach (var slot in slots)
        {
            if (slot.Wired)
            {
                continue;
            }

            var node = slot.Node;
            engine.AttachNode(node);

            // Connect once. Reconnecting while the engine is running
            // throws kAudioUnitErr_FormatNotSupported.
            if (slot.RateUnit is { } rateUnit)
            {
                engine.AttachNode(rateUnit);
                AudioNodes.Connect(engine, node, rateUnit, slot.Format);
                AudioNodes.Connect(engine, rateUnit, engine.MainMixerNode, slot.Format);
            }
            else
            {
                AudioNodes.Connect(engine, node, engine.MainMixerNode, slot.Format);
            }

            slot.Wired = true;

            slot.Owner?.OnNodeWired();
        }
    }

    // After a media-services reset the engine and its nodes belong to
    // the dead server. Abandon them, give every slot a fresh node and
    // go back to probing.
    private void AbandonOutput()
    {
        this._outputAvailable = false;
        this._sessionActive = false;

        this._configurationChangeObserver?.Dispose();
        this._configurationChangeObserver = null;

        if (this._engine is { } engine)
        {
            this._abandoned.Add(engine);
            this._engine = null;
        }

        PlayerSlot[] slots;
        lock (this._requestLock)
        {
            slots = this._slots;
        }

        foreach (var slot in slots)
        {
            this._abandoned.Add(slot.Node);
            slot.Wired = false;
            slot.Node = new AVAudioPlayerNode();
            if (slot.RateUnit is { } rateUnit)
            {
                this._abandoned.Add(rateUnit);
                slot.RateUnit = new AVAudioUnitVarispeed();
            }
        }

        // Each music track schedules its audio again, from its start
        // position, when its new node is wired.
    }

    private void PostMusicResume()
    {
        lock (this._requestLock)
        {
            if (!this._resumeMusicRequested)
            {
                return;
            }

            this._resumeMusicRequested = false;
        }

        DispatchQueue.MainQueue.DispatchAsync(this.ResumeAutoPaused);
    }

    private void RequestRestart()
    {
        lock (this._requestLock)
        {
            this._restartRequested = true;
        }

        this._wake.Set();
    }

    private void RequestReset()
    {
        lock (this._requestLock)
        {
            this._resetRequested = true;
        }

        this._wake.Set();
    }

    #endregion

    #region System events (main thread)

    // Removes any music the game has disposed (e.g. via content
    // unload) from the tracking sets. The service holds direct
    // references to MusicStrategy instances rather than the higher-level
    // Music asset, so a disposed track would otherwise linger and be
    // resurrected by a suspend/resume or interruption sweep,
    // dereferencing its already released AVAudioEngine.
    private void PruneDisposedMusic()
    {
        this._activeMusic.RemoveWhere(static music => music.IsDisposed);
        this._autoPausedMusic.RemoveWhere(static music => music.IsDisposed);
    }

    private void PauseActiveMusic()
    {
        this.PruneDisposedMusic();

        // Pause() only stops the player node and retains every buffer;
        // the native teardown that could block is never triggered from
        // a lifecycle callback.
        foreach (var music in this._activeMusic)
        {
            if (music.IsPlaying)
            {
                music.Pause();
                this._autoPausedMusic.Add(music);
            }
        }
    }

    private void OnSystemSuspend()
    {
        this.PauseActiveMusic();

        lock (this._requestLock)
        {
            this._suspended = true;
            this._resumeMusicRequested = false;
        }

        this._wake.Set();
    }

    private void OnSystemResume()
    {
        // The audio thread re-activates the session, then posts the
        // music resume back here.
        lock (this._requestLock)
        {
            this._suspended = false;
            this._restartRequested = true;
            this._resumeMusicRequested = true;
        }

        this._wake.Set();
    }

    // Whatever an interruption was, the app being active again means trying again: not every
    // Began gets an Ended
    private void OnBecameActive()
    {
        lock (this._requestLock)
        {
            if (this._interrupted)
            {
                this._interrupted = false;
                this._restartRequested = true;
                this._resumeMusicRequested = true;
            }
        }

        this._wake.Set();
    }

    // A late Began for an interruption that came while the app was suspended: it's over, and no
    // Ended follows.
    private static bool WasSuspendedWhenInterrupted(
        AVAudioSessionInterruptionEventArgs args)
    {
        var info = args.Notification.UserInfo;
        if (info is null)
        {
            return false;
        }

        return ((info[WasSuspendedKey] as NSNumber)?.BoolValue == true)
            || ((info[InterruptionReasonKey] as NSNumber)?.NIntValue == InterruptionReasonAppWasSuspended);
    }

    private static readonly NSString WasSuspendedKey = new("AVAudioSessionInterruptionWasSuspendedKey");
    private static readonly NSString InterruptionReasonKey = new("AVAudioSessionInterruptionReasonKey");
    private const int InterruptionReasonAppWasSuspended = 1;

    private void OnAudioSessionInterruption(
        AVAudioSessionInterruptionEventArgs args)
    {
        if (args.InterruptionType == AVAudioSessionInterruptionType.Began)
        {
            if (WasSuspendedWhenInterrupted(args))
            {
                // A resume OnSystemResume just asked for stands
                lock (this._requestLock)
                {
                    this._sessionLost = true;
                    this._restartRequested = true;
                }

                this._wake.Set();
                return;
            }

            this.PauseActiveMusic();

            // The system deactivated the session.
            lock (this._requestLock)
            {
                this._sessionLost = true;
                this._interrupted = true;
                this._resumeMusicRequested = false;
            }

            this._wake.Set();
        }
        else if (args.InterruptionType == AVAudioSessionInterruptionType.Ended)
        {
            lock (this._requestLock)
            {
                this._sessionLost = true;
                this._interrupted = false;
                this._restartRequested = true;
                this._resumeMusicRequested =
                    (args.Option & AVAudioSessionInterruptionOptions.ShouldResume) != 0;
            }

            this._wake.Set();
        }
    }

    private void ResumeAutoPaused()
    {
        // Evict tracks the game disposed while backgrounded so the
        // resume sweep never calls Play on a released engine.
        this.PruneDisposedMusic();

        if (this._autoPausedMusic.Count == 0 || this._silenceSecondaryAudio)
        {
            return;
        }

        // Play with no parameters resumes a paused track in place and
        // returns immediately, so this sweep cannot stall the main
        // thread.
        foreach (var music in this._autoPausedMusic)
        {
            music.Play(null, null, null, false);
        }

        this._autoPausedMusic.Clear();
    }

    #endregion

    #region Volume thread

    /// <summary>
    /// Applies the volume of each playing voice and track to its node
    /// while any plays, so a volume change or a fade reaches audio
    /// that's already playing, as on the other platforms. A player
    /// node has no render callback to poll from, so this thread does
    /// it; it sleeps until the next <see cref="OnPlaybackStarted"/>
    /// once nothing is playing, so an idle game costs nothing.
    /// </summary>
    private void RunVolumeThread()
    {
        while (true)
        {
            bool anyPlaying = false;
            var slots = Volatile.Read(ref this._slots);
            nint pool = ObjCAutoreleasePool.Push();
            for (int i = 0; i < slots.Length; i++)
            {
                var owner = slots[i].Owner;
                if ((owner is not null) && owner.IsRendering)
                {
                    owner.ApplyVolume();
                    anyPlaying = true;
                }
            }

            ObjCAutoreleasePool.Pop(pool);

            if (anyPlaying)
            {
                Thread.Sleep(VolumeInterval);
            }
            else
            {
                this._playbackStarted.WaitOne();
            }
        }
    }

    /// <summary>
    /// Wakes the volume thread for a voice or track that started
    /// playing.
    /// </summary>
    internal void OnPlaybackStarted()
    {
        this._playbackStarted.Set();
    }

    #endregion

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
            this,
            sound.ToConcrete<SoundStrategy>(),
            offset,
            duration);
        return new Strategy<IVoiceStrategy>(voice);
    }

    #region Slots

    internal bool IsOutputAvailable => this._outputAvailable;

    /// <summary>
    /// Reserves a player-node slot connected with
    /// <paramref name="format"/> for a voice or a music track,
    /// reusing a free one with the same format or growing the pool by
    /// one; the audio thread attaches a new node once the output is
    /// open. The returned slot is owned by the caller until
    /// <see cref="ReleaseSlot"/>. Called on the game thread by voices
    /// and on a track's background queue by music.
    /// </summary>
    /// <param name="hasRateUnit">Whether the slot has a varispeed unit between its node and the
    /// mixer, for a voice that plays at a rate other than as recorded
    /// (<see cref="GetSlotRateUnit"/>). A slot keeps the kind it was made as.</param>
    internal int AcquireSlot(
        AVAudioFormat format,
        IPlayerNodeOwner owner,
        bool hasRateUnit = false)
    {
        int index = -1;

        lock (this._requestLock)
        {
            var slots = this._slots;

            for (int i = 0; i < slots.Length; i++)
            {
                if (!slots[i].InUse && ((slots[i].RateUnit is not null) == hasRateUnit) && slots[i].Format.IsEqual(format))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                var grown = new PlayerSlot[slots.Length + 1];
                Array.Copy(slots, grown, slots.Length);
                // NOTE: Its own format: a slot outlives the sound that made it, which disposes its format
                var slotFormat = new AVAudioFormat(format.CommonFormat, format.SampleRate, format.ChannelCount, format.Interleaved);
                grown[slots.Length] = new PlayerSlot(new AVAudioPlayerNode(), slotFormat, hasRateUnit ? new AVAudioUnitVarispeed() : null);
                this._slots = grown;
                index = slots.Length;
            }

            this._slots[index].InUse = true;
            this._slots[index].Owner = owner;
        }

        // Ask the audio thread to wire a new node.
        this._wake.Set();

        return index;
    }

    internal void ReleaseSlot(
        int slot)
    {
        var slots = this._slots;
        if (slot < 0 || slot >= slots.Length)
        {
            return;
        }

        lock (this._requestLock)
        {
            slots[slot].Owner = null;

            // NOTE: Free only once it's stopped, so a new owner's audio isn't stopped with it.
            // An unwired node has never played.
            if (slots[slot].Wired)
            {
                this._releasedSlots.Add(slot);
            }
            else
            {
                slots[slot].InUse = false;
            }
        }

        this._wake.Set();
    }

    // Audio thread.
    private void StopReleasedNodes()
    {
        PlayerSlot[] slots;
        int[] released;
        lock (this._requestLock)
        {
            if (this._releasedSlots.Count == 0)
            {
                return;
            }

            slots = this._slots;
            released = [.. this._releasedSlots];
            this._releasedSlots.Clear();
        }

        foreach (int slot in released)
        {
            try
            {
                if (this._outputAvailable && slots[slot].Wired)
                {
                    slots[slot].Node.Stop();
                }
            }
            catch (ObjCRuntime.ObjCException exception)
            {
                GameHost.ReportAudioFailure(0x51c6ad07, exception);
            }

            lock (this._requestLock)
            {
                slots[slot].InUse = false;
            }
        }
    }

    /// <summary>
    /// The slot's node when it is attached to the running output, or
    /// <c>null</c> while the output is unavailable; voices and tracks
    /// are silent no-ops then.
    /// </summary>
    internal AVAudioPlayerNode? GetSlotNode(int slot)
    {
        var slots = Volatile.Read(ref this._slots);
        if (!this._outputAvailable || slot < 0 || slot >= slots.Length || !slots[slot].Wired)
        {
            return null;
        }

        return slots[slot].Node;
    }

    /// <summary>
    /// The slot's varispeed unit when the slot has one and it is attached to the running output,
    /// or <c>null</c>.
    /// </summary>
    internal AVAudioUnitVarispeed? GetSlotRateUnit(int slot)
    {
        var slots = Volatile.Read(ref this._slots);
        if (!this._outputAvailable || slot < 0 || slot >= slots.Length || !slots[slot].Wired)
        {
            return null;
        }

        return slots[slot].RateUnit;
    }

    /// <summary>
    /// Starts a slot's node on the audio thread, where waiting for the
    /// output can't stall the game, if <paramref name="isWanted"/> still
    /// says so when it runs (called on the audio thread).
    /// </summary>
    internal void StartSlotNode(
        int slot,
        Func<bool> isWanted)
    {
        lock (this._requestLock)
        {
            this._pendingStarts.Add((slot, isWanted));
        }

        this._wake.Set();
    }

    // Audio thread. A node that doesn't start (the server stopped running
    // IO cycles behind the engine's back) restarts the engine, which
    // starts the tracks it was playing; the sound that failed is skipped.
    private void StartPendingNodes()
    {
        (int Slot, Func<bool> IsWanted)[] starts;
        lock (this._requestLock)
        {
            if (this._pendingStarts.Count == 0)
            {
                return;
            }

            starts = this._pendingStarts.ToArray();
            this._pendingStarts.Clear();
        }

        foreach (var (slot, isWanted) in starts)
        {
            var node = this.GetSlotNode(slot);
            if ((node is null) || !isWanted())
            {
                continue;
            }

            try
            {
                AudioNodes.Play(node);
            }
            catch (Exception e) when (e is ObjCRuntime.ObjCException or Foundation.NSErrorException)
            {
                Trace.Write(0x4d8a2c61, "[AudioServiceStrategy] A player node didn't start; restarting the engine.", e);

                lock (this._requestLock)
                {
                    this._restartRequested = true;
                }

                this._wake.Set();
                return;
            }
        }
    }

    #endregion

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
        // A game-initiated pause must not be auto-resumed after a system event
        this._autoPausedMusic.Remove(music);
    }

    /// <inheritdoc />
    public bool ShouldPlayMusic()
        => !this._silenceSecondaryAudio;
}
