// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Opengl;
using Android.Views;
using DotGame.Framework.Platform.Common;
using DotGame.Framework.Threading;
using Handler = Android.OS.Handler;
using JavaSystem = Java.Lang.JavaSystem;
using Looper = Android.OS.Looper;
using Runnable = Java.Lang.Runnable;

namespace DotGame.Framework.Android.Graphics;

/// <summary>
/// A single, long-lived, self-managed EGL/OpenGL ES render thread that the
/// engine fully controls. It replaces the framework-owned GL thread of
/// <c>GLSurfaceView</c> so the Android UI thread never makes an unbounded
/// blocking cross-thread call during pause, resume, surface teardown, or
/// activity destroy — the structural cause of the pause/teardown ANRs.
/// </summary>
/// <remarks>
/// <para>
/// This thread owns the entire EGL lifecycle (display, config, context, window
/// surface) via managed <see cref="EGL14"/> and issues all GL — including the
/// <c>libGLESv2.so</c> P/Invoke hot path in <see cref="GLBindings"/> —
/// against the context that is current on this one thread. It is created
/// exactly once and idles across pause/resume (never recreated), which is
/// required because <see cref="GameSynchronizationContext.Initialize"/> fails
/// fast if called twice and the game-thread marker is
/// <see cref="ThreadStaticAttribute">thread-static</see>.
/// </para>
/// <para>
/// The thread runs a <see cref="Looper"/>. Frames come from
/// <see cref="Choreographer"/> callbacks, one per display refresh while the
/// window can present, and each frame's time is the display's: the expected
/// presentation time on API 33 and later, otherwise the frame's vsync time.
/// This is the pacing Android recommends, and what its frame pacing library
/// (Swappy, in the Android Game Development Kit) is built on. While the game is
/// starting and the window can't present (a cold start in the background),
/// startup advances on short delayed messages instead.
/// </para>
/// <para>
/// The Android UI thread and the engine communicate with this thread only by
/// enqueuing non-blocking commands, which a message on the looper processes.
/// The one bounded wait is the <see cref="CommandType.SurfaceDestroyed"/>
/// handshake (driven from
/// <see cref="GameSurfaceView.SurfaceDestroyed(ISurfaceHolder)"/>), which lets
/// the render thread release its EGL window surface before the framework frees
/// the native window.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The render thread's looper handler and Java callbacks live as long as the engine.")]
internal sealed class RenderThread
{
    private enum CommandType
    {
        SurfaceAvailable,
        SurfaceResized,
        SurfaceDestroyed,
        Pause,
        Resume,
        WindowFocus,
        Stop,
        Run,
    }

    private readonly struct Command
    {
        public CommandType Type { get; init; }

        public Surface? Surface { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public Action? Action { get; init; }

        public bool HasFocus { get; init; }

        public ManualResetEventSlim? ReleaseLatch { get; init; }
    }

    // Safety cap on the render-thread stop pump. Teardown does NOT block the UI
    // thread, so this only bounds how long this background thread pumps the
    // game synchronization context awaiting game.Stop()/Dispose() before it
    // abandons the wait and tears down EGL anyway.
    private const long StopPumpCapMs = 5000;

    // Interval of the startup pump, which advances startup while the window
    // can't present (and so gets no frame callbacks): short enough that loading
    // keeps moving, without a busy-spin and without any GL call.
    private const long StartupPumpIntervalMs = 8;

    // Threshold above which an eglSwapBuffers call is reported as slow. A swap
    // that blocks this long means the compositor/GPU driver stalled it (the
    // exact condition that lets the system RenderThread — and the main thread —
    // block behind us); the presentation gate should keep this from happening,
    // so any occurrence is worth a breadcrumb for diagnosis.
    private const long SlowSwapWarnThresholdMs = 500;

    private readonly AndroidGameEngine _engine;
    private readonly Game _game;
    private readonly GameHost _host;
    private readonly Thread _thread;
    private readonly Lock _sync = new();
    private readonly Queue<Command> _commandQueue = new();

    // The looper and its handler, set on the render thread once it's running;
    // guarded by _sync, since commands are enqueued from other threads.
    private Looper? _looper;
    private Handler? _handler;
    private bool _isProcessScheduled;

    // Frame scheduling — render-thread only.
    private Choreographer? _choreographer;
    private FrameCallback? _frameCallback;
    private Java.Lang.Object? _vsyncCallback;
    private Runnable? _processCommandsRunnable;
    private Runnable? _startupPumpRunnable;
    private bool _isFrameScheduled;
    private bool _isStartupPumpScheduled;

    // EGL objects — created and used only on the render thread.
    private EGLDisplay? _eglDisplay;
    private EGLConfig? _eglConfig;
    private EGLContext? _eglContext;
    private EGLSurface? _eglSurface;

    // Surface/render state — render-thread only unless noted.
    private Surface? _nativeSurface;
    private bool _surfacePendingCreate;

    // Set false on the UI thread (surfaceDestroyed) so the render thread stops
    // presenting to a dying surface immediately; set true on the render thread
    // when a new surface arrives. Read on the render thread each frame.
    private volatile bool _surfaceValid;

    // Set true on the UI thread (window focus loss, visibility change, detach,
    // or pause) and false when the window regains focus/visibility. Read on the
    // render thread each frame — INDEPENDENTLY of the command queue — so the
    // render thread stops issuing eglSwapBuffers (and any other GL call) the
    // instant the window begins a background/stop transition. This is the hard
    // guarantee that our render thread never holds the GPU driver lock while the
    // OS performs its synchronous HardwareRenderer.nSetStopped handoff to the
    // system RenderThread, which is what the main thread waits on during a
    // window stop. A queued command would be too late (it could sit behind a
    // long-running command), so this is a plain volatile flag, not a command.
    private volatile bool _presentationSuspended;

    // Whether the window has focus, which comes and goes on its own for a window on screen
    private bool _windowHasFocus = true;

    private EGLSurface? _eglOffscreenSurface;

    // A drain for work posted while no frames run: render thread, set in ThreadMain
    private Action? _requestIdleDrain;
    private Runnable? _idleDrainRunnable;
    private int _isIdleDrainScheduled;

    private bool _paused;
    private bool _stopRequested;

    // Whether the game's startup has begun, on the first surface
    private bool _startupBegun;

    // Whether a fully drawn frame has been presented, so the host can be told once
    private bool _firstFramePresented;

    private bool _hasPendingSize;
    private int _pendingWidth;
    private int _pendingHeight;

    public RenderThread(
        AndroidGameEngine engine,
        GameHost host)
    {
        this._engine = engine;
        this._host = host;
        this._game = host.Game;

        this._thread = new Thread(this.ThreadMain)
        {
            Name = "DotGame GL Render Thread",
            IsBackground = true,
        };
    }

    /// <summary>
    /// Starts the render thread. Safe to call once, from the UI thread.
    /// </summary>
    public void Start()
    {
        this._thread.Start();
    }

    // ------------------------------------------------------------------
    // Non-blocking command API (called from the UI thread / engine)
    // ------------------------------------------------------------------

    /// <summary>
    /// Notifies the render thread that a native <see cref="Surface"/> is
    /// available so it can create its EGL window surface. Non-blocking.
    /// </summary>
    public void NotifySurfaceCreated(
        Surface surface)
    {
        this.Enqueue(new Command
        {
            Type = CommandType.SurfaceAvailable,
            Surface = surface,
        });
    }

    /// <summary>
    /// Notifies the render thread of a new surface size. Non-blocking.
    /// </summary>
    public void NotifySurfaceChanged(
        int width,
        int height)
    {
        this.Enqueue(new Command
        {
            Type = CommandType.SurfaceResized,
            Width = width,
            Height = height,
        });
    }

    /// <summary>
    /// Notifies the render thread that the surface is being destroyed and waits
    /// up to <paramref name="timeout"/> for it to release the EGL window
    /// surface. Marks the surface invalid immediately so presentation stops at
    /// once, then returns regardless of whether the release acknowledged in
    /// time (bounded so a wedged render thread degrades to a skipped release
    /// instead of a system-fatal ANR).
    /// </summary>
    public void NotifySurfaceDestroyed(
        TimeSpan timeout)
    {
        // Stop presenting to the dying surface as soon as possible.
        this._surfaceValid = false;

        using var latch = new ManualResetEventSlim(false);

        this.Enqueue(new Command
        {
            Type = CommandType.SurfaceDestroyed,
            ReleaseLatch = latch,
        });

        latch.Wait(timeout);
    }

    /// <summary>
    /// Enqueues a change of window focus: the game is inactive while its window lacks focus, and
    /// keeps drawing, unlike a pause.
    /// </summary>
    public void NotifyWindowFocus(
        bool hasFocus)
    {
        this.Enqueue(new Command { Type = CommandType.WindowFocus, HasFocus = hasFocus });
    }

    /// <summary>
    /// Enqueues a pause: the render thread marshals <see cref="GameHost.PauseAsync"/>
    /// onto the game thread, then parks (stops rendering). Non-blocking.
    /// </summary>
    public void NotifyPause()
    {
        this.Enqueue(new Command { Type = CommandType.Pause });
    }

    /// <summary>
    /// Enqueues a resume: the render thread marshals <see cref="GameHost.ResumeAsync"/>
    /// onto the game thread and un-parks (resumes rendering once a surface is
    /// present). Non-blocking.
    /// </summary>
    public void NotifyResume()
    {
        this.Enqueue(new Command { Type = CommandType.Resume });
    }

    /// <summary>
    /// Enqueues teardown: the render thread runs <see cref="GameHost.StopAsync"/> and
    /// <c>Dispose</c> on the game thread (pumping the game synchronization
    /// context to completion), then tears down EGL and exits. Non-blocking on
    /// the caller.
    /// </summary>
    public void NotifyStop()
    {
        this.Enqueue(new Command { Type = CommandType.Stop });
    }

    /// <summary>
    /// Marshals an arbitrary <paramref name="action"/> onto the game thread —
    /// the equivalent of <c>GLSurfaceView.QueueEvent</c>. Non-blocking.
    /// </summary>
    public void Run(
        Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        this.Enqueue(new Command
        {
            Type = CommandType.Run,
            Action = action,
        });
    }

    /// <summary>
    /// Suspends presentation immediately: the render thread stops issuing
    /// <c>eglSwapBuffers</c> (and any other GL call) on its next iteration.
    /// Called on the UI thread from the earliest available window-going-away
    /// signal (visibility change, detach, pause). This is a plain
    /// volatile flag set — NOT a queued command — so it takes effect even if a
    /// long-running command is in flight. It guarantees our render thread is not
    /// inside a GL/EGL call while the OS performs its synchronous
    /// <c>HardwareRenderer.nSetStopped</c> handoff to the system RenderThread,
    /// which is what the main thread waits on during a window stop. Non-blocking.
    /// </summary>
    public void SuspendPresentation(
        string reason)
    {
        this._presentationSuspended = true;

        Trace.Write(0x2b7c9a21, FormattableString.Invariant(
            $"[RenderThread] presentation suspended ({reason})"));
    }

    /// <summary>
    /// Resumes presentation: the render thread starts issuing
    /// <c>eglSwapBuffers</c> again once a surface is present. Called on the UI
    /// thread when the window regains visibility (or on resume). Wakes the
    /// render thread so frames resume immediately rather than waiting for the
    /// next command. Non-blocking.
    /// </summary>
    public void ResumePresentation(
        string reason)
    {
        this._presentationSuspended = false;

        // Wake the render thread so it re-evaluates CanPresent and resumes
        // frames without waiting for the next command to arrive.
        lock (this._sync)
        {
            this.ScheduleProcessLocked();
        }

        Trace.Write(0x2b7c9a22, FormattableString.Invariant(
            $"[RenderThread] presentation resumed ({reason})"));
    }

    private void Enqueue(
        in Command command)
    {
        lock (this._sync)
        {
            this._commandQueue.Enqueue(command);
            this.ScheduleProcessLocked();
        }
    }

    // Posts one message to process the queued commands, unless one is already
    // pending. Before the looper runs, commands wait in the queue; the thread
    // processes them once it starts.
    private void ScheduleProcessLocked()
    {
        if ((this._handler is null) || this._isProcessScheduled)
        {
            return;
        }

        this._isProcessScheduled = true;
        this._handler.Post(this._processCommandsRunnable!);
    }

    // ------------------------------------------------------------------
    // Render thread
    // ------------------------------------------------------------------

    private void ThreadMain()
    {
        // Establish the game-thread identity and synchronization context on
        // this thread ONCE, synchronously, before any async work is kicked off.
        // Doing this outside an async method matters: AsyncTaskMethodBuilder
        // saves/restores SynchronizationContext.Current around an async method
        // body, which would revert a SetSynchronizationContext made inside one.
        GameHost.InitializeGameThread();

        // The priority the system's own display work runs at, so short bursts of game work aren't
        // left on small cores
        global::Android.OS.Process.SetThreadPriority(global::Android.OS.ThreadPriority.Display);

        if (!this.InitializeEgl())
        {
            // No game can run without this, and a thread that just exited would leave the app
            // on its splash screen with no report
            GameHost.Terminate(0x2b7c9a10, new InvalidOperationException(FormattableString.Invariant(
                $"[RenderThread] EGL initialization failed: {this._lastEglFailure}")));
        }

        try
        {
            // The looper: commands, frame callbacks, and the startup pump all
            // run as its messages, on this thread (the game thread), which owns
            // the EGL context. It runs until a Stop command quits it.
            Looper.Prepare();
            this._choreographer = Choreographer.Instance;
            this._frameCallback = new FrameCallback(this);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                this._vsyncCallback = new VsyncCallback(this);
            }

            this._processCommandsRunnable = new Runnable(this.OnProcessCommands);
            this._startupPumpRunnable = new Runnable(this.OnStartupPump);
            this._idleDrainRunnable = new Runnable(this.OnIdleDrain);
            this._requestIdleDrain = this.RequestIdleDrain;

            lock (this._sync)
            {
                this._looper = Looper.MyLooper();
                this._handler = new Handler(this._looper!);

                // Commands enqueued before the looper existed
                this.ScheduleProcessLocked();
            }

            Looper.Loop();
        }
        finally
        {
            lock (this._sync)
            {
                this._handler = null;
            }

            // Teardown: run game teardown on this thread (pumping the game
            // synchronization context to completion), then release all EGL
            // resources and let the thread exit. This runs on every exit path,
            // including a Stop that arrives mid-startup.
            GameHost.WorkPosted = null;
            this.RunStopSequence();
            GameHost.WorkPosted = null;
            this.ReleaseWindowSurface();
            this.DestroyContextAndDisplay();

            // Reset the one-shot game-thread state as the final act on this
            // thread. Android can keep the process alive after this render thread
            // exits and then relaunch (a new activity starts a new render
            // thread); without this reset the next thread's
            // GameSynchronizationContext.Initialize would fail fast on the
            // still-set static guard and abort the process (SIGABRT). This runs
            // last, after every continuation has been pumped.
            GameHost.ShutdownGameThread();

            Trace.Write(0x2b7c9a11, "[RenderThread] render thread exited");

            // The game has had its OnStop, and this is what a game that wants a fresh process for
            // each launch asked for
            this._engine.ExitProcessIfRequested();
        }
    }

    /// <summary>
    /// A looper message: processes the queued commands, then schedules what
    /// runs next. A Stop quits the looper.
    /// </summary>
    private void OnProcessCommands()
    {
        lock (this._sync)
        {
            this._isProcessScheduled = false;
        }

        if (this.ProcessCommands())
        {
            this._looper?.Quit();
            return;
        }

        this.AdoptPendingSurface();
        this.ScheduleNext();
    }

    /// <summary>
    /// Makes a newly arrived native surface's EGL window surface current, and
    /// on the first one, begins the game's startup. Every game GL call runs
    /// against a live context: with no window surface, the context is current
    /// on a small offscreen one.
    /// </summary>
    private void AdoptPendingSurface()
    {
        if (!this._surfacePendingCreate || this._nativeSurface is null || !this._surfaceValid)
        {
            return;
        }

        this._surfacePendingCreate = false;

        // NOTE: A context lost here has been recreated with a new surface, if that worked
        if ((!this.CreateWindowSurface() && (this._eglSurface is null)) || this._startupBegun)
        {
            return;
        }

        // The first surface: create the services, report the display so the
        // game's OnInitialize can read ClientBounds, then start the game. Startup
        // advances with each frame (or startup pump) from here.
        this._startupBegun = true;
        var services = this._engine.CreateServices();
        this.ApplyPendingSize();
        this._host.BeginStartup(services);
    }

    /// <summary>
    /// Schedules what runs next: a frame at the next display refresh while the
    /// window can present; the startup pump while the game is starting and the
    /// window can't present; otherwise no frames, with continuations run as they
    /// arrive, so a game's asynchronous OnPause finishes.
    /// </summary>
    private void ScheduleNext()
    {
        if (this._stopRequested)
        {
            return;
        }

        if (this.CanPresent())
        {
            // NOTE: While starting, a hop from a worker shouldn't wait for the next vsync
            GameHost.WorkPosted = this._host.IsStarted ? null : this._requestIdleDrain;
            this.ScheduleFrame();
        }
        else if (this._startupBegun && !this._host.IsStarted && (this._host.StartupFault is null))
        {
            if (!this._isStartupPumpScheduled)
            {
                this._isStartupPumpScheduled = true;
                this._handler!.PostDelayed(this._startupPumpRunnable!, StartupPumpIntervalMs);
            }
        }
        else
        {
            GameHost.WorkPosted = this._requestIdleDrain;
            GameHost.DrainContinuations();
        }
    }

    // Posts a drain for work posted while no frames are running. Called on the posting thread;
    // once per drain, however much is posted.
    private void RequestIdleDrain()
    {
        if (Interlocked.Exchange(ref this._isIdleDrainScheduled, 1) == 0)
        {
            this._handler?.Post(this._idleDrainRunnable!);
        }
    }

    private void OnIdleDrain()
    {
        Volatile.Write(ref this._isIdleDrainScheduled, 0);

        if (!this._stopRequested)
        {
            this._engine.StampRenderHeartbeat();
            GameHost.DrainContinuations();
        }
    }

    private void ScheduleFrame()
    {
        if (this._isFrameScheduled)
        {
            return;
        }

        this._isFrameScheduled = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            this._choreographer!.PostVsyncCallback((Choreographer.IVsyncCallback)this._vsyncCallback!);
        }
        else
        {
            this._choreographer!.PostFrameCallback(this._frameCallback!);
        }
    }

    /// <summary>
    /// A frame callback, at a display refresh: runs a frame and presents it if
    /// the game drew, then schedules the next.
    /// </summary>
    /// <param name="frameTimeNanos">The frame's time on the monotonic clock
    /// (<c>System.nanoTime</c>): its expected presentation time where the
    /// platform reports one, otherwise its vsync time.</param>
    private void OnFrame(
        long frameTimeNanos)
    {
        this._isFrameScheduled = false;
        if (this._stopRequested)
        {
            return;
        }

        GLResources.RunPendingDeletes();

        // Stamp the liveness counter first thing so even a frame that only
        // drains async work proves the loop is alive to the stall watchdog.
        this._engine.StampRenderHeartbeat();

        // A new native surface may have arrived (e.g. after a background resume,
        // or a context-loss recreate); CreateWindowSurface kicks GL resource
        // restore if the context was lost.
        this.AdoptPendingSurface();

        if (this.CanPresent())
        {
            this.RenderFrame(TimeSpan.FromTicks(frameTimeNanos / 100));
        }

        this.ScheduleNext();
    }

    /// <summary>
    /// A startup pump message: advances startup while the window can't present.
    /// </summary>
    private void OnStartupPump()
    {
        this._isStartupPumpScheduled = false;
        if (this._stopRequested)
        {
            return;
        }

        // Stamp the liveness counter so the stall watchdog does not
        // false-positive while startup runs off the GPU.
        this._engine.StampRenderHeartbeat();

        this.AdoptPendingSurface();
        this.ApplyPendingSize();
        this._host.Tick(TimeSpan.FromTicks(JavaSystem.NanoTime() / 100), canDraw: false);

        this.ScheduleNext();
    }

    /// <summary>
    /// Dispatches a pending surface-size change to the engine (viewport, bounds,
    /// refresh rate, DPI), once the game's services exist. No-op when no resize
    /// is pending. OnSurfaceChanged completes synchronously, so the bounds are
    /// applied by the time this returns.
    /// </summary>
    private void ApplyPendingSize()
    {
        if (!this._hasPendingSize || !this._startupBegun)
        {
            return;
        }

        this._hasPendingSize = false;
        this._engine.OnSurfaceChanged(this._pendingWidth, this._pendingHeight);
    }

    /// <summary>
    /// Drains and processes all queued commands. Returns <see langword="true"/>
    /// if a <see cref="CommandType.Stop"/> was encountered (teardown).
    /// </summary>
    private bool ProcessCommands()
    {
        while (true)
        {
            Command command;
            lock (this._sync)
            {
                if (this._commandQueue.Count == 0)
                {
                    return false;
                }

                command = this._commandQueue.Dequeue();
            }

            switch (command.Type)
            {
                case CommandType.SurfaceAvailable:
                    Trace.Write(0x2b7c9a25, "[RenderThread] SurfaceAvailable");
                    this._nativeSurface = command.Surface;
                    this._appliedFrameRate = 0;
                    this._surfaceValid = true;
                    this._surfacePendingCreate = true;
                    break;

                case CommandType.SurfaceResized:
                    Trace.Write(0x2b7c9a26, FormattableString.Invariant(
                        $"[RenderThread] SurfaceResized {command.Width}x{command.Height}"));
                    this._pendingWidth = command.Width;
                    this._pendingHeight = command.Height;
                    this._hasPendingSize = true;
                    break;

                case CommandType.SurfaceDestroyed:
                    Trace.Write(0x2b7c9a27, "[RenderThread] SurfaceDestroyed");
                    this._nativeSurface = null;
                    this._surfacePendingCreate = false;
                    this.ReleaseWindowSurface();

                    // Acknowledge release so the UI thread's bounded wait
                    // returns. The latch may already be disposed if the UI
                    // thread timed out first; ignore that race.
                    try
                    {
                        command.ReleaseLatch?.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    break;

                case CommandType.Pause:
                    Trace.Write(0x2b7c9a28, "[RenderThread] Pause");
                    this._paused = true;
                    this.UpdateGameActive();
                    break;

                case CommandType.Resume:
                    Trace.Write(0x2b7c9a29, "[RenderThread] Resume");
                    this._paused = false;
                    this.UpdateGameActive();
                    break;

                case CommandType.WindowFocus:
                    Trace.Write(0x2b7c9a2d, FormattableString.Invariant($"[RenderThread] Window focus {command.HasFocus}"));
                    this._windowHasFocus = command.HasFocus;
                    this.UpdateGameActive();
                    break;

                case CommandType.Run:
                    Trace.Write(0x2b7c9a2a, "[RenderThread] Run");
                    try
                    {
                        command.Action?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Trace.Write(0x2b7c9a12, "[RenderThread] marshaled action faulted", ex);
                    }

                    break;

                case CommandType.Stop:
                    Trace.Write(0x2b7c9a2b, "[RenderThread] Stop");
                    this._stopRequested = true;
                    return true;
            }
        }
    }

    // The game is active while its activity is resumed and its window has focus. The host
    // ignores a repeat of the state it's in.
    private void UpdateGameActive()
    {
        if (this._paused || !this._windowHasFocus)
        {
            this.MarshalGameLifecycle(static host => host.PauseAsync(), "Pause");
        }
        else
        {
            this.MarshalGameLifecycle(static host => host.ResumeAsync(), "Resume");
        }
    }

    /// <summary>
    /// Runs a game lifecycle transition on the game thread. The returned
    /// <see cref="Task"/> is intentionally not awaited here — the synchronous
    /// portion runs inline and any continuation is pumped by the per-frame (or
    /// idle) drain of the synchronization context.
    /// </summary>
    private void MarshalGameLifecycle(
        Func<GameHost, Task> transition,
        string name)
    {
        try
        {
            // The synchronous portion runs inline; SafeAsync covers a
            // fault raised after the first await, which the catch below
            // cannot observe.
            SafeAsync.Invoke(0x7e41d0c6, transition(this._host));
        }
        catch (Exception ex)
        {
            Trace.Write(0x2b7c9a13, FormattableString.Invariant(
                $"[RenderThread] game.{name} faulted"), ex);
        }
    }

    private bool IsActiveRenderState()
    {
        return this._eglSurface is not null
            && this._surfaceValid
            && !this._paused
            && !this._stopRequested;
    }

    /// <summary>
    /// Whether presentation is currently suspended by a UI-thread signal
    /// (visibility change, detach, or pause). When <see langword="true"/>
    /// the render loop gets no frames and will not advance its heartbeat counter,
    /// so callers (e.g. the stall watchdog via
    /// <see cref="DotGame.Framework.Android.AndroidGameEngine.IsRenderLoopActive"/>)
    /// must treat the loop as legitimately idle to avoid false stall reports.
    /// </summary>
    internal bool IsPresentationSuspended => this._presentationSuspended;

    /// <summary>
    /// Whether the render thread may issue GL/EGL calls (notably
    /// <c>eglSwapBuffers</c>) this frame. This is the single gate every swap
    /// is checked against: it is <see cref="IsActiveRenderState"/> plus the
    /// UI-thread presentation-suspend flag, so presentation stops the instant
    /// the window begins a background/stop transition — before the OS performs
    /// its synchronous <c>HardwareRenderer.nSetStopped</c> handoff to the system
    /// RenderThread. Keeping our render thread out of GL during that window keeps
    /// the main thread from blocking behind us; a frame already under way can't
    /// be stopped by a flag, so this reduces the risk rather than removing it.
    /// </summary>
    internal bool CanPresent()
    {
        return this.IsActiveRenderState()
            && !this._presentationSuspended;
    }

    // The frame rate last asked of the surface, where 0 is the display's own
    private int _appliedFrameRate;

    /// <summary>
    /// Asks the surface for the frame rate the game would like, if that's changed. From Android 11;
    /// the system has the last word.
    /// </summary>
    private void ApplyPreferredFrameRate()
    {
        int frameRate = this._engine.GraphicsService?.PreferredFrameRate ?? 0;
        if ((frameRate != this._appliedFrameRate) && (this._nativeSurface is { } surface) && OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            this._appliedFrameRate = frameRate;
            surface.SetFrameRate(frameRate, (int)SurfaceFrameRateCompatibility.Default);
        }
    }

    /// <summary>
    /// A presentable frame: dispatches a pending size change, runs the game's
    /// frame (which, while starting, only advances startup), and presents it if
    /// the game drew. Nothing is presented until the game draws its first
    /// frame, so startup never flashes a blank frame over the splash; the host
    /// is told once the first drawn frame is on screen.
    /// </summary>
    private void RenderFrame(
        TimeSpan frameTimestamp)
    {
        // Dispatch any pending size change before drawing this frame.
        this.ApplyPendingSize();
        this.ApplyPreferredFrameRate();

        // NOTE: Nothing can be drawn while a lost context's resources are being recreated
        bool canDraw = !(this._engine.GraphicsService?.IsDeviceLost ?? true);
        if (!this._host.Tick(frameTimestamp, canDraw))
        {
            return;
        }

        // Flush any sprites still pending in the active batch so they reach the
        // GPU before eglSwapBuffers presents this frame.
        this._engine.GraphicsService!.FlushPendingSprites();

        // Present only if the window still allows it. Presentation may have been
        // suspended on the UI thread between the frame's gate check and now (the
        // flag is volatile and set from a window visibility/detach/pause
        // callback); re-checking here keeps our render thread out of
        // eglSwapBuffers during the OS window-stop handoff.
        if (this.CanPresent() && this.SwapBuffers() && !this._firstFramePresented)
        {
            this._firstFramePresented = true;
            Trace.Write(0x2b7c9a2c, "[RenderThread] first frame presented");
            this._host.NotifyFirstFramePresented();
        }
    }

    private async Task RunRestoreAsync()
    {
        try
        {
            await this._engine.OnSurfaceCreated();
        }
        catch (Exception ex)
        {
            // NOTE: Nothing draws until a restore finishes, so running on would be a black screen
            // forever, with no report
            GameHost.Terminate(0x2b7c9a15, ex);
        }
    }

    /// <summary>
    /// Receives Choreographer frame callbacks (before API 33) for the render thread.
    /// </summary>
    private sealed class FrameCallback : Java.Lang.Object, Choreographer.IFrameCallback
    {
        private readonly RenderThread _thread;

        public FrameCallback(
            RenderThread thread)
        {
            this._thread = thread;
        }

        public void DoFrame(
            long frameTimeNanos)
        {
            this._thread.OnFrame(frameTimeNanos);
        }
    }

    /// <summary>
    /// Receives Choreographer vsync callbacks (API 33 and later), which carry the
    /// frame's expected presentation time.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private sealed class VsyncCallback : Java.Lang.Object, Choreographer.IVsyncCallback
    {
        private readonly RenderThread _thread;

        public VsyncCallback(
            RenderThread thread)
        {
            this._thread = thread;
        }

        public void OnVsync(
            Choreographer.FrameData data)
        {
            this._thread.OnFrame(data.PreferredFrameTimeline.ExpectedPresentationTimeNanos);
        }
    }

    // ------------------------------------------------------------------
    // EGL lifecycle
    // ------------------------------------------------------------------

    private bool InitializeEgl()
    {
        // NOTE: A failure returns the EGL14.EglNo* object, not null
        this._eglDisplay = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
        if ((this._eglDisplay is null) || this._eglDisplay.Equals(EGL14.EglNoDisplay))
        {
            this._eglDisplay = null;
            this.LogEglError("eglGetDisplay");
            return false;
        }

        var version = new int[2];
        if (!EGL14.EglInitialize(this._eglDisplay, version, 0, version, 1))
        {
            this.LogEglError("eglInitialize");
            return false;
        }

        if (!this.ChooseConfig())
        {
            return false;
        }

        return this.CreateContext();
    }

    private bool ChooseConfig()
    {
        // Multisampling first if asked for, then with fewer samples, then the plain configs
        for (int samples = this._engine.MultisampleCount; samples >= 2; samples /= 2)
        {
            int[] multisampled =
            {
                EGL14.EglRenderableType, EGLExt.EglOpenglEs3BitKhr,
                EGL14.EglSurfaceType, EGL14.EglWindowBit | EGL14.EglPbufferBit,
                EGL14.EglRedSize, 8,
                EGL14.EglGreenSize, 8,
                EGL14.EglBlueSize, 8,
                EGL14.EglAlphaSize, 8,
                EGL14.EglDepthSize, 0,
                EGL14.EglSampleBuffers, 1,
                EGL14.EglSamples, samples,
                EGL14.EglNone,
            };

            if (this.TryChooseConfig(multisampled))
            {
                var value = new int[1];
                this._engine.MultisampleCount = EGL14.EglGetConfigAttrib(this._eglDisplay, this._eglConfig, EGL14.EglSamples, value, 0)
                    ? Math.Max(value[0], 1)
                    : samples;
                return true;
            }
        }

        this._engine.MultisampleCount = 1;

        // Prefer 32-bit color, and no depth buffer, as sprites don't use one (about 5 MB at 1080p);
        // fall back to 16-bit 565 color if the device cannot provide an 8888 window config.
        // OpenGL ES 3 configs only: the content pipeline compiles shaders to
        // GLSL ES 3.00, which an ES 2 context can't compile.
        int[] attribs8888 =
        {
            EGL14.EglRenderableType, EGLExt.EglOpenglEs3BitKhr,
            EGL14.EglSurfaceType, EGL14.EglWindowBit | EGL14.EglPbufferBit,
            EGL14.EglRedSize, 8,
            EGL14.EglGreenSize, 8,
            EGL14.EglBlueSize, 8,
            EGL14.EglAlphaSize, 8,
            EGL14.EglDepthSize, 0,
            EGL14.EglNone,
        };

        if (this.TryChooseConfig(attribs8888))
        {
            return true;
        }

        Trace.Write(0x2b7c9a16, "[RenderThread] RGBA8888 config unavailable; falling back to RGB565");

        int[] attribs565 =
        {
            EGL14.EglRenderableType, EGLExt.EglOpenglEs3BitKhr,
            EGL14.EglSurfaceType, EGL14.EglWindowBit | EGL14.EglPbufferBit,
            EGL14.EglRedSize, 5,
            EGL14.EglGreenSize, 6,
            EGL14.EglBlueSize, 5,
            EGL14.EglDepthSize, 0,
            EGL14.EglNone,
        };

        if (this.TryChooseConfig(attribs565))
        {
            return true;
        }

        this.LogEglError("eglChooseConfig");
        return false;
    }

    private bool TryChooseConfig(
        int[] attribs)
    {
        var configs = new EGLConfig[1];
        var numConfig = new int[1];

        bool chosen = EGL14.EglChooseConfig(
            this._eglDisplay,
            attribs,
            0,
            configs,
            0,
            1,
            numConfig,
            0);

        if (chosen && numConfig[0] > 0 && configs[0] is not null)
        {
            this._eglConfig = configs[0];
            return true;
        }

        return false;
    }

    private bool CreateContext()
    {
        // OpenGL ES 3, for the GLSL ES 3.00 shaders (see ChooseConfig)
        int[] contextAttribs =
        {
            EGL14.EglContextClientVersion, 3,
            EGL14.EglNone,
        };

        this._eglContext = EGL14.EglCreateContext(
            this._eglDisplay,
            this._eglConfig,
            EGL14.EglNoContext,
            contextAttribs,
            0);

        if ((this._eglContext is null) || this._eglContext.Equals(EGL14.EglNoContext))
        {
            this._eglContext = null;
            this.LogEglError("eglCreateContext");
            return false;
        }

        GLResources.OnNewContext();

        // The context's surface while there's no window, so game code and uploads that run then
        // (OnResume comes before the surface, an interrupted load's uploads) have a live context
        int[] offscreenAttribs = { EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone };
        this._eglOffscreenSurface = EGL14.EglCreatePbufferSurface(this._eglDisplay, this._eglConfig, offscreenAttribs, 0);
        if ((this._eglOffscreenSurface is null) || this._eglOffscreenSurface.Equals(EGL14.EglNoSurface))
        {
            this._eglOffscreenSurface = null;
            this.LogEglError("eglCreatePbufferSurface");
            GameHost.ReportNonFatal(0x71c8a3e4, new InvalidOperationException(this._lastEglFailure));
        }

        return true;
    }

    private bool CreateWindowSurface()
    {
        if (this._eglDisplay is null || this._eglConfig is null || this._eglContext is null)
        {
            return false;
        }

        int[] surfaceAttribs = { EGL14.EglNone };

        this._eglSurface = EGL14.EglCreateWindowSurface(
            this._eglDisplay,
            this._eglConfig,
            this._nativeSurface,
            surfaceAttribs,
            0);

        if ((this._eglSurface is null) || this._eglSurface.Equals(EGL14.EglNoSurface))
        {
            // Null keeps the game from drawing to nowhere; the next surface event tries again
            this._eglSurface = null;
            this.LogEglError("eglCreateWindowSurface");
            GameHost.ReportNonFatal(0x6c1f3ea2, new InvalidOperationException(this._lastEglFailure));
            return false;
        }

        if (!EGL14.EglMakeCurrent(this._eglDisplay, this._eglSurface, this._eglSurface, this._eglContext))
        {
            int error = EGL14.EglGetError();
            if (error == EGL14.EglContextLost)
            {
                this.HandleContextLoss();
                return false;
            }

            // Nothing can draw to this surface, so it's let go; the next surface event tries again
            this.ReleaseWindowSurface();
            GameHost.ReportNonFatal(0x2b7c9a17, new InvalidOperationException(FormattableString.Invariant(
                $"[RenderThread] eglMakeCurrent failed: 0x{error:x4}")));
            return false;
        }

        // If the EGL context was lost and recreated (device marked lost),
        // reload GL resources. Fire-and-forget; its continuations are pumped by
        // subsequent frames (or the startup pump). The null guard covers the
        // first-ever surface, created before the graphics service exists.
        if (this._engine.GraphicsService is not null && this._engine.GraphicsService.IsDeviceLost)
        {
            _ = this.RunRestoreAsync();
        }

        return true;
    }

    private void ReleaseWindowSurface()
    {
        if (this._eglDisplay is null || this._eglSurface is null)
        {
            this._eglSurface = null;
            return;
        }

        // Detach the surface from the context before destroying it. The context
        // itself is preserved across pause/resume so GL resources survive, and it
        // stays current on the offscreen surface.
        if (this._eglOffscreenSurface is not null)
        {
            EGL14.EglMakeCurrent(this._eglDisplay, this._eglOffscreenSurface, this._eglOffscreenSurface, this._eglContext);
        }
        else
        {
            EGL14.EglMakeCurrent(this._eglDisplay, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
        }

        EGL14.EglDestroySurface(this._eglDisplay, this._eglSurface);
        this._eglSurface = null;
    }

    private void DestroyContext()
    {
        if (this._eglDisplay is not null)
        {
            EGL14.EglMakeCurrent(this._eglDisplay, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);

            if (this._eglOffscreenSurface is not null)
            {
                EGL14.EglDestroySurface(this._eglDisplay, this._eglOffscreenSurface);
            }

            if (this._eglContext is not null)
            {
                EGL14.EglDestroyContext(this._eglDisplay, this._eglContext);
            }
        }

        this._eglOffscreenSurface = null;
        this._eglContext = null;
    }

    private void DestroyContextAndDisplay()
    {
        this.DestroyContext();

        if (this._eglDisplay is not null)
        {
            EGL14.EglTerminate(this._eglDisplay);
            this._eglDisplay = null;
        }
    }

    /// <summary>
    /// Handles an <c>EGL_CONTEXT_LOST</c> from make-current or swap: notifies
    /// the graphics service, recreates the EGL context and (if a surface is
    /// still available) the window surface. The subsequent make-current runs
    /// the resource-restore path because the device is now marked lost.
    /// </summary>
    private void HandleContextLoss()
    {
        Trace.Write(0x2b7c9a18, "[RenderThread] EGL context lost; recreating");

        // NOTE: A context that's lost as soon as it's made would otherwise recurse here forever
        if (++this._contextLossDepth > MaxContextLossDepth)
        {
            GameHost.Terminate(0x6e0a44d2, new InvalidOperationException("The EGL context keeps being lost as soon as it's created."));
        }

        try
        {
            this.RecreateContext();
        }
        finally
        {
            this._contextLossDepth--;
        }
    }

    private const int MaxContextLossDepth = 3;
    private int _contextLossDepth;

    private void RecreateContext()
    {
        try
        {
            this._engine.GraphicsService.HandleContextLost();
        }
        catch (Exception ex)
        {
            Trace.Write(0x2b7c9a19, "[RenderThread] HandleContextLost faulted", ex);
        }

        this.ReleaseWindowSurface();
        this.DestroyContext();

        if (!this.CreateContext())
        {
            Trace.Write(0x2b7c9a1a, "[RenderThread] failed to recreate EGL context after loss");
            return;
        }

        if (this._nativeSurface is not null && this._surfaceValid)
        {
            this.CreateWindowSurface();
        }
    }

    /// <summary>
    /// Presents the current back buffer. Returns <see langword="true"/> only if
    /// <c>eglSwapBuffers</c> actually presented a frame; returns
    /// <see langword="false"/> when there is no surface to present to, or the
    /// swap failed (including an <c>EGL_CONTEXT_LOST</c> that triggers a context
    /// recreate rather than a present). The first-frame phase relies on this to
    /// signal the host only after a real present.
    /// </summary>
    private bool SwapBuffers()
    {
        if (this._eglDisplay is null || this._eglSurface is null)
        {
            return false;
        }

        // Time the swap so a swap that blocks despite the presentation gate is
        // diagnosable. A gated, non-presenting frame never reaches here, so any
        // slow swap recorded below happened while the window was genuinely
        // presentable — evidence of a compositor/driver stall rather than a
        // window-stop race.
        long swapStart = Environment.TickCount64;
        bool swapped = EGL14.EglSwapBuffers(this._eglDisplay, this._eglSurface);
        long swapMs = Environment.TickCount64 - swapStart;

        if (swapMs >= SlowSwapWarnThresholdMs)
        {
            Trace.Write(0x2b7c9a23, FormattableString.Invariant(
                $"[RenderThread] eglSwapBuffers took {swapMs}ms"));
        }

        if (swapped)
        {
            return true;
        }

        int error = EGL14.EglGetError();
        if (error == EGL14.EglContextLost)
        {
            this.HandleContextLoss();
        }
        else
        {
            Trace.Write(0x2b7c9a1b, FormattableString.Invariant(
                $"[RenderThread] eglSwapBuffers failed: 0x{error:x4}"));
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Teardown
    // ------------------------------------------------------------------

    private void RunStopSequence()
    {
        Trace.Write(0x2b7c9a1c, "[RenderThread] Stop: begin game teardown");

        // Keep the context current (if a surface remains) so any GL cleanup in
        // game.Stop has a chance to run; destroying the context afterward frees
        // all GL resources regardless.
        if (this._eglDisplay is not null && this._eglSurface is not null && this._eglContext is not null)
        {
            EGL14.EglMakeCurrent(this._eglDisplay, this._eglSurface, this._eglSurface, this._eglContext);
        }

        Task stopTask = this.StopGameAsync();

        // Pump the game synchronization context on THIS thread until teardown
        // completes. This is deadlock-free because we are the game thread and
        // drain our own queue; it does not block the UI thread.
        long start = Environment.TickCount64;
        while (!stopTask.IsCompleted)
        {
            GameHost.DrainContinuations();

            if (stopTask.IsCompleted)
            {
                break;
            }

            if (Environment.TickCount64 - start > StopPumpCapMs)
            {
                Trace.Write(0x2b7c9a1d, "[RenderThread] Stop: pump exceeded cap; abandoning wait");
                return;
            }

            Thread.Sleep(1);
        }

        if (stopTask.Exception is not null)
        {
            Trace.Write(0x2b7c9a1e, "[RenderThread] Stop: game teardown faulted", stopTask.Exception);
        }
        else
        {
            Trace.Write(0x2b7c9a1f, "[RenderThread] Stop: game teardown complete");
        }
    }

    private async Task StopGameAsync()
    {
        // GameHost.StopAsync waits for a startup step still in progress, so a
        // Stop that arrives mid-startup never runs alongside it
        await this._host.StopAsync();
        this._game.Dispose();
        this._engine.ShutdownAudio();
        this._engine.ShutdownGamepads();
    }

    private void LogEglError(
        string operation)
    {
        int error = EGL14.EglGetError();
        this._lastEglFailure = FormattableString.Invariant($"{operation} failed: 0x{error:x4}");
        Trace.Write(0x2b7c9a20, FormattableString.Invariant($"[RenderThread] {this._lastEglFailure}"));
    }

    // The last EGL call that failed, for a report
    private string _lastEglFailure = string.Empty;
}
