// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;
using DotGame.Framework.Threading;

namespace DotGame.Framework.Platform;

/// <summary>
/// The framework's side of the platform contract: everything a platform host calls to run a
/// <see cref="Framework.Game"/>, the counterpart of the strategy interfaces the platform
/// implements. A platform drives the framework only through this, the strategies,
/// <see cref="Strategy{T}"/>, and members marked <see cref="PlatformContractAttribute"/>; the
/// analyzers enforce it.
/// </summary>
/// <remarks>
/// <para>
/// The host owns the sequencing, the same on every platform: a platform says its graphics device
/// is ready (<see cref="BeginStartup"/>), that a frame is due (<see cref="Tick"/>), that the
/// display changed, that input arrived, and that the app is paused, resumed or stopping. The
/// host decides what runs: the startup steps (initialize, then start), updates and draws, and
/// when the frame clock resets.
/// </para>
/// <para>
/// One <see cref="GameHost"/> runs one game. Call its members from the game thread, except
/// <see cref="QueuePointerEvent"/> and <see cref="QueueKeyEvent"/>, which any thread may call.
/// </para>
/// <para>
/// Its static members are the framework-wide parts of the contract, which code deep in a
/// platform (a content loader, a sprite batch) calls without a host to hand: the game thread,
/// which is one per process, fatal errors, frame statistics, and the startup timeline.
/// </para>
/// </remarks>
internal sealed class GameHost
{
    private enum Phase
    {
        Created,
        Starting,
        Started,
        Faulted,
        Stopped,
    }

    private readonly Game _game;
    private Phase _phase;
    private Task? _startupTask;

    // A platform pause that arrived while starting, applied once the game has started
    private bool _isPausePending;

    // A display change that arrived while starting, reported once the game has started
    private bool _isDisplayChangePending;

    // A stop that arrived while starting, which waits for startup to finish
    private bool _isStopRequested;

    // Whether a frame has been drawn since starting; the first is drawn even while paused
    private bool _hasDrawnFirstFrame;

    // The platform's launch screen, given before startup and dismissed once
    private ILaunchScreenStrategy? _launchScreen;
    private int _isFirstFramePresented;

    public GameHost(
        Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        this._game = game;
    }

    /// <summary>
    /// The game this host runs.
    /// </summary>
    public Game Game => this._game;

    // ── Startup and lifecycle ──────────────────────────────────────────

    /// <summary>
    /// Whether the game has started: its <c>OnInitialize</c> and <c>OnStart</c> have completed,
    /// and each <see cref="Tick"/> now runs a frame.
    /// </summary>
    public bool IsStarted => this._phase == Phase.Started;

    /// <summary>
    /// The exception that ended startup, or null. A startup fault is also reported as a fatal
    /// error, which ends the process.
    /// </summary>
    public Exception? StartupFault { get; private set; }

    /// <summary>
    /// The color the last frame drawn first cleared the window to, or null before a frame has
    /// cleared it. A platform whose window doesn't reach the whole screen shows it around the
    /// window (the web page's background), as the others show it in their safe areas.
    /// </summary>
    public Color? WindowBackground => this._game.Graphics.WindowBackground;

    /// <summary>
    /// Starts the game once the platform's graphics device is ready: creates and initializes the
    /// game's services over <paramref name="services"/>, then runs the game's <c>OnInitialize</c>
    /// and <c>OnStart</c>. Returns at the first await; each <see cref="Tick"/> after this
    /// advances startup until the game has started. Call once, on the game thread, after
    /// <see cref="InitializeGameThread"/>.
    /// </summary>
    public void BeginStartup(
        PlatformServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        GameThread.VerifyCurrent(0xa1b2c3d4);

        if (this._phase != Phase.Created)
        {
            throw new InvalidOperationException("The game has already been started.");
        }

        this._phase = Phase.Starting;
        StartupTimeline.Mark("engine-start");

        // NOTE: Runs synchronously up to its first await, so the game's services exist when this
        // returns
        this._startupTask = this.RunStartup(services);
    }

    private async Task RunStartup(
        PlatformServices services)
    {
        try
        {
            this._game.CreateServices(services);
            await this._game.InitializeServices();
            await this._game.InvokeOnInitialize();

            // Game time starts once the game is initialized
            this._game.Time.Reset();
            StartupTimeline.Mark("engine-initialized");

            Trace.Write(0x78b00b4b, "Starting game...");
            await this._game.InvokeOnStart();

            this._phase = Phase.Started;
            this._game.IsActiveState = true;
            this._game.Time.ResetFrameTime();

            // From now on a load mustn't hold up frames. The web has only the one thread.
            this._game.Content.LoadsOnWorkerThreads = !OperatingSystem.IsBrowser();
            StartupTimeline.Mark("game-started");

            if (this._isStopRequested)
            {
                // StopAsync, waiting on startup, stops the game next
                return;
            }

            // A display change that arrived while starting is reported now: the game laid out
            // its first scenes from the display it read then (a web page in an iOS home-screen
            // app learns its safe area insets a moment after it loads)
            if (this._isDisplayChangePending)
            {
                this._isDisplayChangePending = false;

                this._game.InvokeOnClientBoundsChanged();
            }

            // A pause that arrived while starting takes effect now, as if it had arrived just
            // after: the game pauses (OnPause) and stays inactive until the platform resumes it,
            // so work deferred while inactive (such as postload) waits for that resume.
            if (this._isPausePending)
            {
                this._isPausePending = false;

                await this.PauseAsync();
            }
        }
        catch (Exception e)
        {
            this.StartupFault = e;
            this._phase = Phase.Faulted;
            FatalError.Terminate(0xcaeda43c, e);
        }
    }

    /// <summary>
    /// Makes the game inactive, when its window loses focus, something covers it, or the app goes
    /// to the background. A pause while starting takes effect once the game has started.
    /// </summary>
    public Task PauseAsync()
    {
        GameThread.VerifyCurrent(0xd2f3a603);

        // NOTE: A platform pause can arrive before the game has started (the activity is
        // backgrounded during a cold start, or the browser window loses focus while the page
        // loads). There is nothing to pause yet, so it's remembered and applied when it starts.
        if (this._phase != Phase.Started)
        {
            if (this._phase == Phase.Starting)
            {
                this._isPausePending = true;
            }

            return Task.CompletedTask;
        }

        // A press in progress ends here: the platform may never report its release (the
        // window lost focus mid-press), which would leave a control or a drag held
        this._game.Input.CancelPressedPointers();

        // NOTE: Platforms can report the same change more than once (the web reports a page
        // losing focus and being hidden separately), and a game sees each change once.
        if (!this._game.IsActiveState)
        {
            return Task.CompletedTask;
        }

        this._game.IsActiveState = false;

        // NOTE: A hook that isn't async throws here, and the platform's handling of the returned
        // task is what reports a fault, so it faults the task as an async hook would
        try
        {
            return this._game.InvokeOnPause();
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
    }

    /// <summary>
    /// Makes the game active again, when it has focus and is in front. A resume while starting
    /// cancels a pause that arrived while starting.
    /// </summary>
    public Task ResumeAsync()
    {
        GameThread.VerifyCurrent(0xe5c6b704);

        if (this._phase != Phase.Started)
        {
            this._isPausePending = false;
            return Task.CompletedTask;
        }

        if (this._game.IsActiveState)
        {
            return Task.CompletedTask;
        }

        this._game.IsActiveState = true;

        // The time spent in the background isn't game time
        this._game.Time.ResetFrameClock();

        // NOTE: As in PauseAsync
        try
        {
            return this._game.InvokeOnResume();
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
    }

    /// <summary>
    /// Stops the game, before the platform disposes it. Safe at any point: during startup, it
    /// lets startup finish first (frames keep advancing it), so a game whose <c>OnStart</c> ran
    /// always gets its <c>OnStop</c>.
    /// </summary>
    public async Task StopAsync()
    {
        GameThread.VerifyCurrent(0xc7d8e902);

        this._isPausePending = false;

        if (this._phase == Phase.Starting)
        {
            this._isStopRequested = true;

            // NOTE: RunStartup catches its own faults, so this doesn't throw
            await this._startupTask!;
        }

        if (this._phase != Phase.Started)
        {
            if (this._phase != Phase.Faulted)
            {
                this._phase = Phase.Stopped;
            }

            return;
        }

        this._phase = Phase.Stopped;
        this._game.IsActiveState = false;
        this._hasDrawnFirstFrame = false;
        this._game.ResetFirstFrameDrawn();

        await this._game.InvokeOnStop();
    }

    // ── Frames and the display ─────────────────────────────────────────

    /// <summary>
    /// Runs one frame, from the platform's frame callback. While the game is starting, it only
    /// runs the continuations that advance startup. Once started, it updates the game unless the
    /// game is inactive (and not set to run then), and draws it if <paramref name="canDraw"/>, so
    /// an inactive game's picture stays correct. A platform passes false while the game is hidden.
    /// </summary>
    /// <param name="frameTimestamp">A monotonic time from the platform's frame source, ideally
    /// when this frame will be shown. Only differences between timestamps are used.</param>
    /// <param name="canDraw">Whether the platform can present a frame now: false while a window
    /// is being resized or minimized, or a graphics context is lost.</param>
    /// <returns>Whether the game drew, so the platform has a frame to present.</returns>
    public bool Tick(
        TimeSpan frameTimestamp,
        bool canDraw)
    {
        try
        {
            switch (this._phase)
            {
                case Phase.Starting:
                    this.RunStartingFrame(frameTimestamp);
                    return false;

                case Phase.Started:
                    return this.RunFrame(frameTimestamp, canDraw);

                default:
                    return false;
            }
        }
        catch (Exception e) when (!System.Diagnostics.Debugger.IsAttached)
        {
            // Every platform reports a fault in the game's update or draw the same way, as the
            // game thread's pump does for a continuation's. With a debugger attached it stays
            // unhandled, so the debugger stops where it was thrown.
            FatalError.Terminate(0x4c7a18b1, e);
            throw;
        }
    }

    // A frame while the game is starting: advances startup by running the continuations queued
    // on the game thread, and discards input, which is for a game that isn't showing yet. A
    // press in progress is canceled, so the rest of it isn't delivered without its beginning.
    // Game time runs, so a delay awaited in OnInitialize or OnStart finishes.
    private void RunStartingFrame(
        TimeSpan frameTimestamp)
    {
        this._game.Input?.CancelPressedPointers();
        this._game.Input?.DequeuePointerEvents();
        this._game.Input?.DequeueKeyEvents();
        this._game.Input?.DequeueGamepadEvents();

        this._game.Time?.Update(frameTimestamp);

        // NOTE: The whole queue: there's no rendered frame to protect yet
        GameSynchronizationContext.GameThread.DrainPendingContinuations();
    }

    private bool RunFrame(
        TimeSpan frameTimestamp,
        bool canDraw)
    {
        var game = this._game;

        // NOTE: The first frame always runs, so a game paused while starting has something to
        // show
        bool isRunning = game.IsActiveState || game.RunWhenInactive || !this._hasDrawnFirstFrame;

        StatsCollector.BeginUpdate();

        if (isRunning)
        {
            game.Time.Update(frameTimestamp);
        }

        // NOTE: As spans, not IEnumerable: enumerating a list of structs through
        // IEnumerable<T> uses generic instances that WebAssembly AOT doesn't compile,
        // so every frame with input ran them in the interpreter
        game.InvokeHandlePointerEvents(CollectionsMarshal.AsSpan(game.Input.DequeuePointerEvents()));
        game.InvokeHandleKeyEvents(CollectionsMarshal.AsSpan(game.Input.DequeueKeyEvents()));
        game.InvokeHandleGamepadEvents(CollectionsMarshal.AsSpan(game.Input.DequeueGamepadEvents()));

        // Dispatch text-input session events (raised on the game thread)
        game.Input.DispatchTextInputEvents();

        // Run the continuations queued on the game synchronization context. This is the single
        // steady-state pump: it runs once per frame with an adaptively tuned budget, so bursts
        // of continuations (a cold start's shader compiles) are spread across frames without
        // dropping one.
        //
        // NOTE: Before OnUpdate, so state changed by continuations (a scene invalidating its
        // layout when async loading completes) is applied by this frame's update and layout
        // rather than drawn stale.
        GameSynchronizationContext.GameThread.ExecutePendingContinuations();

        if (isRunning)
        {
            if (game.FixedUpdateRate is not null)
            {
                while (game.Time.TryTakeFixedStep())
                {
                    game.InvokeOnFixedUpdate();
                }
            }

            game.InvokeOnUpdate();
        }

        StatsCollector.EndUpdate();

        if (!canDraw || (this._phase != Phase.Started) || game.IsDisposed)
        {
            return false;
        }

        StatsCollector.BeginDraw();
        game.Graphics.BeginFrame();
        game.InvokeOnDraw();
        StatsCollector.EndDraw();

        // The overlay draws after EndDraw, so its own sprite batch traffic isn't counted in the
        // statistics it shows
        game.Diagnostics.DrawOverlay(game.SafeAreaBounds);

        if (!this._hasDrawnFirstFrame)
        {
            this._hasDrawnFirstFrame = true;
            StartupTimeline.Mark("first-frame");
            game.CompleteFirstFrameDrawn();
        }

        return true;
    }

    /// <summary>
    /// Forgets the last frame's time, after a stall the host can't see (a lost graphics
    /// context's recovery), so the next frame doesn't count it as game time. The host resets the
    /// clock itself after starting and resuming.
    /// </summary>
    public void ResetFrameClock()
    {
        this._game.Time?.ResetFrameClock();
    }

    /// <summary>
    /// Tells the game about its display: when the platform first knows it, and whenever any of
    /// it changes. A change while starting is reported once the game has started.
    /// </summary>
    public void SetDisplay(
        in DisplayInfo display)
    {
        GameThread.VerifyCurrent(0xf4a7d805);

        bool isChanged = (display.ClientBounds != this._game.ClientBounds)
            || (display.SafeAreaBounds != this._game.SafeAreaBounds)
            || ((display.Dpi > 0f) && (display.Dpi != this._game.DisplayDpi));

        GameSynchronizationContext.GameThread.SetTargetFrameRate(display.RefreshRateHz);
        this._game.SetDisplay(display.ClientBounds, display.SafeAreaBounds, display.Dpi);

        if (!isChanged)
        {
            return;
        }

        if (this._phase == Phase.Started)
        {
            try
            {
                this._game.InvokeOnClientBoundsChanged();
            }
            catch (Exception e) when (!System.Diagnostics.Debugger.IsAttached)
            {
                // NOTE: As in Tick: the platform's display callback isn't inside a frame
                FatalError.Terminate(0x5d1e8c37, e);
                throw;
            }
        }
        else if (this._phase == Phase.Starting)
        {
            this._isDisplayChangePending = true;
        }
    }

    // ── Input ──────────────────────────────────────────────────────────

    /// <summary>
    /// The graphics device came back after being lost, and the platform has restored what it
    /// keeps. Game thread.
    /// </summary>
    public void NotifyDeviceReset()
    {
        this._game.Graphics?.RaiseDeviceReset();
    }

    /// <summary>
    /// The system is short of memory. Any thread: the game hears of it on the game thread.
    /// </summary>
    public void NotifyLowMemory()
    {
        GameSynchronizationContext.GameThread?.Post(static state => ((Game)state!).InvokeOnLowMemory(), this._game);
    }

    /// <summary>
    /// Queues a pointer event (mouse, touch, pen) for the next frame. Events before the game's
    /// services exist are dropped. Any thread may call this.
    /// </summary>
    public void QueuePointerEvent(
        in PointerEventData e)
    {
        this._game.Input?.QueuePointerEvent(
            e.State,
            e.PointerId,
            e.PointerType,
            e.Position.X,
            e.Position.Y,
            e.Button,
            e.HeldButtons,
            e.ScrollWheelDelta.X,
            e.ScrollWheelDelta.Y,
            e.AgeMs);
    }

    /// <summary>
    /// Queues a gamepad event for the next frame. Events before the game's services exist are
    /// dropped. Any thread may call this.
    /// </summary>
    public void QueueGamepadEvent(
        in DotGame.Framework.Input.GamepadEvent e)
    {
        this._game.Input?.QueueGamepadEvent(e);
    }

    /// <summary>
    /// Queues a key event for the next frame. Events before the game's services exist are
    /// dropped. Any thread may call this.
    /// </summary>
    public void QueueKeyEvent(
        in KeyEventData e)
    {
        this._game.Input?.QueueKeyEvent(e.State, e.Key, e.Modifiers, e.IsRepeat, e.VirtualKey);
    }

    // ── The game thread (one per process) ──────────────────────────────

    /// <summary>
    /// Makes the calling thread the game thread, with the game's synchronization context.
    /// </summary>
    public static void InitializeGameThread()
    {
        GameSynchronizationContext.GameThread.Initialize();
    }

    /// <summary>
    /// Ends the game thread, when the thread the platform ran it on exits.
    /// </summary>
    public static void ShutdownGameThread()
    {
        GameSynchronizationContext.GameThread.Shutdown();
    }

    /// <summary>
    /// Re-establishes the game's synchronization context on the game thread, for a platform
    /// whose callbacks arrive on it without it (the browser's).
    /// </summary>
    public static void EnsureGameThreadContext()
    {
        GameSynchronizationContext.GameThread.EnsureContext();
    }

    /// <summary>
    /// Runs every continuation queued on the game thread, for pumping outside frames (a paused
    /// app, teardown).
    /// </summary>
    public static void DrainContinuations()
    {
        GameSynchronizationContext.GameThread.DrainPendingContinuations();
    }

    /// <summary>
    /// Called when work is posted to the game thread, so a platform that pumps it on demand (the
    /// browser's, during startup) can schedule a pump. Null for none.
    /// </summary>
    public static Action? WorkPosted
    {
        get => GameSynchronizationContext.GameThread.WorkPosted;
        set => GameSynchronizationContext.GameThread.WorkPosted = value;
    }

    // ── The launch screen ──────────────────────────────────────────────

    /// <summary>
    /// Shows the platform's launch screen, which stays until the game's first frame is on screen
    /// (<see cref="NotifyFirstFramePresented"/>). Called once, on the platform's UI thread, before
    /// <see cref="BeginStartup"/>. A host whose platform has no launch screen doesn't call it.
    /// </summary>
    public void ShowLaunchScreen(
        ILaunchScreenStrategy launchScreen)
    {
        ArgumentNullException.ThrowIfNull(launchScreen);
        if (Volatile.Read(ref this._launchScreen) is not null)
        {
            throw new InvalidOperationException("The launch screen has already been shown.");
        }

        Volatile.Write(ref this._launchScreen, launchScreen);
        launchScreen.Show();
    }

    /// <summary>
    /// The game's first drawn frame is on screen: dismisses the launch screen. The platform knows
    /// when a frame is presented (after its swap, its presented handler, its submit), so its host
    /// reports it, from any thread; only the first report counts. On the web, the page waits for
    /// the frame to be composited itself, so its host reports the first drawn frame.
    /// </summary>
    public void NotifyFirstFramePresented()
    {
        if (Interlocked.Exchange(ref this._isFirstFramePresented, 1) != 0)
        {
            return;
        }

        Volatile.Read(ref this._launchScreen)?.Dismiss();
    }

    /// <summary>
    /// How long every platform takes to fade its launch screen out, once the game's first frame
    /// is on screen (<c>docs/architecture/launch-screen.md</c>).
    /// </summary>
    public static TimeSpan LaunchScreenFadeDuration { get; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The launch screen fade's curve on every platform: Material 3's emphasized accelerate,
    /// the easing it gives elements leaving the screen, the cubic Bézier from (0, 0) to (1, 1)
    /// through the control points (0.3, 0) and (0.8, 0.15). Each platform uses it as its own
    /// cubic Bézier timing: CSS's <c>cubic-bezier(0.3, 0, 0.8, 0.15)</c>, a
    /// <c>UICubicTimingParameters</c> on iOS, a <c>PathInterpolator</c> on Android, and
    /// <see cref="EaseLaunchScreenFade"/> on desktop. The fade starts gently and finishes
    /// quickly.
    /// </summary>
    public static (float X1, float Y1, float X2, float Y2) LaunchScreenFadeEase { get; } = (0.3f, 0f, 0.8f, 0.15f);

    /// <summary>
    /// <see cref="LaunchScreenFadeEase"/> as a CSS timing function, for the web page.
    /// </summary>
    public static string LaunchScreenFadeCssEase
    {
        get
        {
            var (x1, y1, x2, y2) = LaunchScreenFadeEase;
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"cubic-bezier({x1}, {y1}, {x2}, {y2})");
        }
    }

    /// <summary>
    /// How far the launch screen fade is at <paramref name="time"/>, from 0 to 1 of
    /// <see cref="LaunchScreenFadeDuration"/>, eased by <see cref="LaunchScreenFadeEase"/>: for
    /// hosts that animate it themselves.
    /// </summary>
    public static float EaseLaunchScreenFade(
        float time)
    {
        if (time <= 0)
        {
            return 0;
        }

        if (time >= 1)
        {
            return 1;
        }

        var (x1, y1, x2, y2) = LaunchScreenFadeEase;

        // The curve's parameter whose x is the time, by bisection (x is increasing), then its y
        float low = 0;
        float high = 1;
        float s = time;
        for (int i = 0; i < 24; i++)
        {
            s = (low + high) * 0.5f;
            if (Bezier(s, x1, x2) < time)
            {
                low = s;
            }
            else
            {
                high = s;
            }
        }

        return Bezier(s, y1, y2);

        // A coordinate of the cubic Bézier from 0 to 1 through two control values
        static float Bezier(float s, float c1, float c2)
        {
            float u = 1 - s;
            return (3 * u * u * s * c1) + (3 * u * s * s * c2) + (s * s * s);
        }
    }

    // ── Diagnostics ────────────────────────────────────────────────────

    /// <summary>
    /// Ends the process for an error the game can't recover from, reporting it first.
    /// </summary>
    public static void Terminate(
        uint tag,
        Exception? exception)
    {
        FatalError.Terminate(tag, exception);
    }

    /// <summary>
    /// Reports a failure the game runs on without, such as a GPU failure the platform can retry.
    /// Each site reports once per run. Any thread may call this.
    /// </summary>
    public static void ReportNonFatal(
        uint tag,
        Exception exception)
    {
        NonFatalError.Report(tag, exception);
    }

    /// <summary>
    /// Handles a failure in audio: the game runs on silently and it's reported, except in a Debug
    /// build, where it ends the game so it can't be missed. Any thread may call this.
    /// </summary>
    public static void ReportAudioFailure(
        uint tag,
        Exception exception)
    {
        if (FeatureSwitches.IsDebug)
        {
            FatalError.Terminate(tag, exception);
        }
        else
        {
            NonFatalError.Report(tag, exception);
        }
    }

    /// <summary>
    /// Counts <paramref name="drawCalls"/> draw calls of <paramref name="sprites"/> sprites in
    /// all in the frame's statistics. A sprite batch reports each flush once.
    /// </summary>
    public static void CountDraws(
        int drawCalls,
        int sprites)
    {
        StatsCollector.CountDraws(drawCalls, sprites);
    }

    /// <summary>
    /// Receives each startup timeline mark (its name and detail), for a platform that records
    /// startup timing of its own (the browser's performance timeline). Null for none.
    /// </summary>
    public static Action<string, string?>? StartupTimelineSink
    {
        get => StartupTimeline.PlatformSink;
        set => StartupTimeline.PlatformSink = value;
    }
}
