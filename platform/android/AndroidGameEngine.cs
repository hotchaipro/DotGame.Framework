// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Hardware.Input;
using Android.Views;
using DotGame.Framework.Android.Audio;
using DotGame.Framework.Android.Content;
using DotGame.Framework.Android.Graphics;
using DotGame.Framework.Android.Input;
using DotGame.Framework.Content;
using DotGame.Framework.Input;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Content;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;
using AndroidKeyEvent = Android.Views.KeyEvent;
using AndroidWindow = global::Android.Window;

namespace DotGame.Framework.Android;

/// <summary>
/// Runs a game on Android, drawing with OpenGL ES on a thread of its own.
/// </summary>
/// <example>
/// <code>
/// // In your MainActivity.OnCreate:
/// var hostView = new FrameLayout(this);
/// SetContentView(hostView);
/// new AndroidGameEngine(this, hostView).Run(new MyGame());
/// </code>
/// </example>
/// <remarks>
/// A game runs once in a process, and the process lives as long as the game: when the activity
/// finishes, the process ends once the game has stopped. When Android destroys the activity without
/// ending the process and makes another of its class in its place (a configuration change the
/// activity doesn't handle, such as the language, or the system reclaiming it in the background, as
/// Don't keep activities does), the app starts again in a new process, from inside the new
/// activity's <c>base.OnCreate</c>, before its own code goes on to make a game: the game starts
/// afresh from what it saved, as after Android ended the process, so no one-time setup of the
/// game's runs twice in one process.
/// </remarks>
#pragma warning disable CA1001 // Its only disposable field is a Java object the runtime manages
public sealed class AndroidGameEngine
#pragma warning restore CA1001
{
    /// <summary>
    /// The configuration changes a game's activity handles itself, for its <c>Activity</c> attribute's
    /// <c>ConfigurationChanges</c>. Any it leaves out, such as a foldable opening, or the player changing
    /// the font size or dark mode, make Android destroy and recreate the activity, which starts the app
    /// again in a new process (see the remarks on <see cref="AndroidGameEngine"/>).
    /// </summary>
    public const global::Android.Content.PM.ConfigChanges ConfigurationChanges =
        global::Android.Content.PM.ConfigChanges.Orientation
        | global::Android.Content.PM.ConfigChanges.ScreenSize
        | global::Android.Content.PM.ConfigChanges.SmallestScreenSize
        | global::Android.Content.PM.ConfigChanges.ScreenLayout
        | global::Android.Content.PM.ConfigChanges.Density
        | global::Android.Content.PM.ConfigChanges.FontScale
        | global::Android.Content.PM.ConfigChanges.UiMode
        | global::Android.Content.PM.ConfigChanges.Keyboard
        | global::Android.Content.PM.ConfigChanges.KeyboardHidden
        | global::Android.Content.PM.ConfigChanges.Navigation;

    private Game _game = null!;
    private GameHost _host = null!;
    private GameSurfaceView? _surfaceView;
    private RenderThread? _renderThread;
    private InputServiceStrategy? _inputService;
    private readonly Activity _activity;
    private readonly ViewGroup _hostView;
#pragma warning disable CA1001, CA2213 // A Java object the runtime manages, which ShutdownGamepads unregisters
    private Gamepads? _gamepads;
#pragma warning restore CA1001, CA2213

    // Liveness signal for the self-managed render loop (the render thread that
    // services RenderThread.RenderFrame). This is the engine's own
    // render loop, NOT the system hwui RenderThread owned by the platform. A
    // stall watchdog samples these to detect a wedged render loop
    // independently of the main/UI thread. The counter is written only by the
    // render thread; the active gate only by the UI lifecycle; both are read
    // from the watchdog thread, so each uses volatile/interlocked access.
    private long _renderLoopHeartbeat;
    private volatile bool _renderLoopActive = true;

    internal GraphicsServiceStrategy GraphicsService { get; private set; } = null!;

    internal ContentServiceStrategy ContentService { get; private set; } = null!;

    internal Game Game => this._game;

    internal GameHost Host => this._host;

    /// <summary>
    /// A monotonically increasing counter advanced once per serviced frame on
    /// the engine's render loop (see
    /// <see cref="Graphics.RenderThread"/>). A stall watchdog can
    /// sample this to detect a stalled render loop: while
    /// <see cref="IsRenderLoopActive"/> is <see langword="true"/>, a value that
    /// stops advancing indicates the render/game thread is wedged.
    /// <para>
    /// This tracks the engine's own render loop, which is distinct from the
    /// system hwui RenderThread the main thread hands off to during a window
    /// stop.
    /// </para>
    /// </summary>
    internal long RenderLoopHeartbeat => Volatile.Read(ref this._renderLoopHeartbeat);

    /// <summary>
    /// Whether the render loop is expected to be advancing
    /// <see cref="RenderLoopHeartbeat"/>. This is <see langword="false"/> while
    /// the activity is paused or presentation is suspended (e.g. on window focus
    /// loss, before <c>OnPause</c> fires), so a watchdog can suppress false stall
    /// reports during any legitimate interval when the render thread is parked.
    /// </summary>
    internal bool IsRenderLoopActive => this._renderLoopActive && !(this._renderThread?.IsPresentationSuspended ?? false);

    /// <summary>
    /// What the options asked for, until the render thread chooses its EGL config, then what it got.
    /// </summary>
    internal int MultisampleCount { get; set; } = 1;

    // The engine running in this process, if any
    private static AndroidGameEngine? s_current;

    /// <summary>
    /// Makes an engine for an activity; it does nothing until <see cref="Run"/>.
    /// </summary>
    /// <param name="activity">
    /// The host activity. Can be any <see cref="Activity"/> subclass —
    /// no special base class is required.
    /// </param>
    /// <param name="hostView">
    /// The <see cref="ViewGroup"/> that will host the surface view.
    /// The caller is responsible for creating the view hierarchy and calling
    /// <see cref="Activity.SetContentView(View)"/> before or after this call.
    /// </param>
    public AndroidGameEngine(
        Activity activity,
        ViewGroup hostView)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(hostView);

        this._activity = activity;
        this._hostView = hostView;
    }

    // Set when the activity finishes, so the process ends, which the render thread does after the
    // game has stopped
    private volatile bool _isExitRequested;

    private int _isStopRequested;

    private AudioServiceStrategy? _audioService;

    internal void ExitProcessIfRequested()
    {
        if (this._isExitRequested)
        {
            // Through Trace first, as KillProcess is abrupt
            Trace.Write(0xadddbc2b, "[AndroidGameEngine] The activity finished and the game has stopped: ending the process");

            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
        }
    }

    internal void ShutdownGamepads()
    {
        this._gamepads?.Shutdown();
        this._gamepads = null;
    }

    internal void ShutdownAudio()
    {
        this._audioService?.Shutdown();
        this._audioService = null;
    }

    /// <summary>
    /// Starts the game, drawing into a surface view added to the host view. It returns at once: the
    /// game runs on the engine's own thread, which is the game thread.
    /// </summary>
    /// <param name="game">The game instance to run.</param>
    /// <exception cref="InvalidOperationException">
    /// The engine is already running a game, or a game has run in this process already (a game runs
    /// once in a process: see the remarks on <see cref="AndroidGameEngine"/>).
    /// </exception>
    public void Run(
        Game game)
    {
        ArgumentNullException.ThrowIfNull(game);

        if (this._game is not null)
        {
            throw new InvalidOperationException("The engine is already running a game.");
        }

        var activity = this._activity;
        var hostView = this._hostView;

        // An activity of the game's class made after this one, in the same process, starts the app
        // again in a new one (RecreateCallbacks), so a second game here is a second activity class
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
        {
            throw new InvalidOperationException("A game has run in this process already: a game runs once in a process.");
        }

        activity.Application!.RegisterActivityLifecycleCallbacks(
            new RecreateCallbacks(activity));

        // The engine takes ownership of the game instance immediately so the
        // render thread (which drives the whole lifecycle) has it available
        // before the first surface callback arrives.
        this._game = game;
        this._host = new GameHost(game);
        this.MultisampleCount = game.PreferredMultisampleCount;

        // Start the self-managed EGL render thread. It owns the entire EGL and
        // GL lifecycle and never makes an unbounded blocking cross-thread call
        // back onto the UI thread (unlike GLSurfaceView's internal GL thread).
        var renderThread = new RenderThread(this, this._host);
        this._renderThread = renderThread;

        // Create the custom surface view. It forwards surface lifecycle
        // callbacks to the render thread as non-blocking commands (the sole
        // bounded wait is the <=250ms surface-destroy release handshake).
        var surfaceView = new GameSurfaceView(activity, renderThread);
        this._surfaceView = surfaceView;
        activity.RegisterComponentCallbacks(new MemoryCallbacks(this._host));

        // The launch screen, until the game's first frame is on screen (the render thread reports
        // it to the host), then faded out
        this._host.ShowLaunchScreen(new LaunchScreenStrategy(activity, surfaceView));

        // Handle touch input on the surface
        surfaceView.SetOnTouchListener(new GameTouchListener(this));

        // Enable keyboard input on the surface
        surfaceView.Focusable = true;
        surfaceView.FocusableInTouchMode = true;
        surfaceView.SetOnKeyListener(new GameKeyListener(this));

        this._gamepads = new Gamepads(
            this._host,
            (InputManager?)activity.GetSystemService(Context.InputService));
        surfaceView.SetOnGenericMotionListener(this._gamepads);

        // Register the back button handler (API 33+).
        // The legacy path (KEYCODE_BACK) in GameKeyListener handles older versions.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            activity.OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(
                0, // OnBackInvokedDispatcher.PRIORITY_DEFAULT
                new GameOnBackInvokedCallback(this));
        }

        // Observe activity lifecycle for pause / resume / destroy
        activity.Application!.RegisterActivityLifecycleCallbacks(
            new GameLifecycleCallbacks(activity, this));

        // Add the surface view to the caller-supplied host view
        hostView.AddView(surfaceView, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        // Start the render thread. It parks until the first surface arrives.
        renderThread.Start();

        // Enter sticky immersive fullscreen — hides the status bar
        // and navigation bar. A swipe gesture reveals them temporarily.
        EnableImmersiveMode(activity);
    }

    /// <summary>
    /// Advances the render-loop liveness counter. Called once per serviced
    /// frame by <see cref="Graphics.RenderThread"/> so a stall
    /// watchdog can detect a wedged render/game thread.
    /// </summary>
    internal void StampRenderHeartbeat()
    {
        Interlocked.Increment(ref this._renderLoopHeartbeat);
    }

    /// <summary>
    /// Creates the platform's services. Called once on the render (game) thread
    /// by <see cref="Graphics.RenderThread"/> when the first EGL
    /// surface is current, just before it starts the game with them.
    /// </summary>
    internal PlatformServices CreateServices()
    {
        if (this.GraphicsService is not null)
        {
            throw new InvalidOperationException("This engine instance is already initialized.");
        }

        Trace.Write(0x2d716822, "Initializing Android engine...");

        // Create the required game services
        this.GraphicsService = new GraphicsServiceStrategy(this);
        var graphicsServiceStrategy = new Strategy<IGraphicsServiceStrategy>(this.GraphicsService);
        var androidAudioService = new AudioServiceStrategy(this._activity!);
        this._audioService = androidAudioService;
        var audioService = new Strategy<IAudioServiceStrategy>(androidAudioService);
        this._inputService = new InputServiceStrategy(this._host, this._activity!, this._hostView!);
        var inputService = new Strategy<IInputServiceStrategy>(this._inputService);

        // Create the content service. The content root is a build-time fact
        // carried by the manifest; Android resolves it relative to the APK's
        // assets/ folder, so no additional runtime root is prepended.
        var contentManifest = ContentManifest.Registry.BuildSnapshot();
        this.ContentService = new ContentServiceStrategy(this, contentManifest.BasePath, contentManifest)
        {
            MusicPlaybackListener = androidAudioService,
        };
        var contentServiceStrategy = new Strategy<IContentServiceStrategy>(this.ContentService);

        return new PlatformServices(
            contentServiceStrategy,
            graphicsServiceStrategy,
            audioService,
            inputService);
    }

    /// <summary>
    /// Called when the GL surface is created or re-created (context lost + restored).
    /// </summary>
    internal async Task OnSurfaceCreated()
    {
        if (this.GraphicsService is not null && this.GraphicsService.IsDeviceLost)
        {
            await this.GraphicsService.HandleContextRestored();
            this._host.NotifyDeviceReset();
        }
    }

    /// <summary>
    /// Called when the GL surface size changes. Runs on the game thread
    /// and reports the new viewport size and the display (bounds, safe area,
    /// DPI, refresh rate) to the game.
    /// </summary>
    internal void OnSurfaceChanged(
        int width,
        int height)
    {
        this.GraphicsService?.SetViewportSize(width, height);

        // The display's current refresh rate (covers 60 / 90 / 120 Hz panels
        // and display-mode changes). View.Display is null until the view is
        // attached; 0 makes the continuation budget assume 60 FPS.
        float refreshHz = this._surfaceView?.Display?.RefreshRate ?? 0f;

        var canvasBounds = new RectangleInt(0, 0, width, height);
        var safeBounds = this.GetSafeBounds(width, height);

        // Average X/Y DPI gives the best approximation of physical density
        // since pixels can be slightly non-square on some panels.
        var metrics = this._surfaceView?.Resources?.DisplayMetrics;
        float displayDpi = (metrics is not null)
            ? (metrics.Xdpi + metrics.Ydpi) * 0.5f
            : 0f;

        this._host.SetDisplay(new DisplayInfo(canvasBounds, safeBounds, displayDpi, refreshHz));
    }

    /// <summary>
    /// Computes the safe area bounds by insetting the surface bounds
    /// according to system bars, display cutouts, and rounded display
    /// corners.
    /// </summary>
    private RectangleInt GetSafeBounds(
        int surfaceWidth,
        int surfaceHeight)
    {
        int top = 0, right = 0, bottom = 0, left = 0;

        var rootInsets = OperatingSystem.IsAndroidVersionAtLeast(23) ? this._surfaceView?.AppliedInsets : null;
        if (rootInsets is not null)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var insets = rootInsets.GetInsets(
                    WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                if (insets is not null)
                {
                    top = insets.Top;
                    right = insets.Right;
                    bottom = insets.Bottom;
                    left = insets.Left;
                }

                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    // Account for rounded display corners by insetting
                    // only the short edges (shortening the long edges).
                    // Since the perpendicular edges are not inset, the
                    // full corner radius is needed to keep a right-angle
                    // of content inside the arc.
                    int topLeftR = rootInsets.GetRoundedCorner(
                        (int)RoundedCornerPosition.TopLeft)?.Radius ?? 0;
                    int topRightR = rootInsets.GetRoundedCorner(
                        (int)RoundedCornerPosition.TopRight)?.Radius ?? 0;
                    int bottomRightR = rootInsets.GetRoundedCorner(
                        (int)RoundedCornerPosition.BottomRight)?.Radius ?? 0;
                    int bottomLeftR = rootInsets.GetRoundedCorner(
                        (int)RoundedCornerPosition.BottomLeft)?.Radius ?? 0;

                    if (surfaceWidth > surfaceHeight)
                    {
                        // Landscape: inset left and right
                        left = Math.Max(left,
                            Math.Max(topLeftR, bottomLeftR));
                        right = Math.Max(right,
                            Math.Max(topRightR, bottomRightR));
                    }
                    else
                    {
                        // Portrait or square: inset top and bottom
                        top = Math.Max(top,
                            Math.Max(topLeftR, topRightR));
                        bottom = Math.Max(bottom,
                            Math.Max(bottomLeftR, bottomRightR));
                    }
                }
            }
            else
            {
                top = rootInsets.SystemWindowInsetTop;
                right = rootInsets.SystemWindowInsetRight;
                bottom = rootInsets.SystemWindowInsetBottom;
                left = rootInsets.SystemWindowInsetLeft;

                if (OperatingSystem.IsAndroidVersionAtLeast(28))
                {
                    var cutout = rootInsets.DisplayCutout;
                    if (cutout is not null)
                    {
                        top = Math.Max(top, cutout.SafeInsetTop);
                        right = Math.Max(right, cutout.SafeInsetRight);
                        bottom = Math.Max(bottom, cutout.SafeInsetBottom);
                        left = Math.Max(left, cutout.SafeInsetLeft);
                    }
                }
            }
        }

        // The soft keyboard covers the bottom of the surface, which isn't resized for it
        bottom = Math.Max(bottom, this._surfaceView?.ImeBottomInset ?? 0);

        return new RectangleInt(
            left,
            top,
            surfaceWidth - left - right,
            surfaceHeight - top - bottom);
    }

    /// <summary>
    /// Called when the activity is paused.
    /// </summary>
    /// <remarks>
    /// Invoked by <see cref="GameLifecycleCallbacks"/> on the Android UI
    /// thread. This is fully fire-and-forget: it closes the render-loop
    /// liveness gate and enqueues a non-blocking <c>Pause</c> command onto the
    /// self-managed render thread, then returns immediately. The render thread
    /// marshals <see cref="GameHost.PauseAsync"/> onto the game thread and parks itself.
    /// The UI thread never makes an unbounded blocking cross-thread call here —
    /// the structural cause of the pause ANRs that <c>GLSurfaceView.onPause()</c>
    /// produced.
    /// </remarks>
    internal void OnPause()
    {
        // Close the render-loop liveness gate first so the stall watchdog stops
        // expecting heartbeats: from here the render thread legitimately stops
        // drawing and parks once it processes the Pause command.
        this._renderLoopActive = false;

        // Backstop the earliest window-going-away signals delivered to
        // GameSurfaceView (focus/visibility/detach): suspend presentation
        // immediately so the render thread stops issuing eglSwapBuffers even if a
        // device delivers those view callbacks late or not at all. This is a
        // plain volatile flag set — it takes effect before the queued Pause
        // command is processed — so our render thread is never inside a GL/EGL
        // call while the OS performs its HardwareRenderer.nSetStopped handoff to
        // the system RenderThread (the wait the main thread would otherwise be
        // stuck on).
        this._renderThread?.SuspendPresentation("pause");

        Trace.Write(0x6f1a2b8c, "[AndroidGameEngine] OnPause: enqueuing non-blocking Pause");
        this._renderThread?.NotifyPause();
    }

    /// <summary>
    /// Called when the activity is resumed.
    /// </summary>
    /// <remarks>
    /// Invoked by <see cref="GameLifecycleCallbacks"/> on the Android UI
    /// thread. Enqueues a non-blocking <c>Resume</c> command onto the render
    /// thread (which marshals <see cref="GameHost.ResumeAsync"/> onto the game thread and
    /// un-parks) and re-applies immersive mode on the UI thread. Returns
    /// immediately.
    /// </remarks>
    internal void OnResume()
    {
        Trace.Write(0x6f1a2b91, "[AndroidGameEngine] OnResume: enqueuing non-blocking Resume");
        this._renderThread?.NotifyResume();

        // Backstop resume: clear the presentation-suspend flag in case the view
        // focus/visibility callbacks that normally resume presentation are
        // delivered late. This pulses the render thread's command monitor so a
        // parked loop wakes and resumes drawing promptly.
        this._renderThread?.ResumePresentation("resume");

        // Reopen the render-loop liveness gate now that the render thread is
        // being un-parked, so the stall watchdog resumes expecting heartbeats.
        this._renderLoopActive = true;

        // Reapply immersive mode — the legacy SystemUiVisibility flags
        // can be cleared by the system when the activity is paused. This is a
        // fast, UI-only call and is safe to run on the UI thread.
        if (this._activity is Activity activity)
        {
            EnableImmersiveMode(activity);
        }
    }

    /// <summary>
    /// Enables sticky immersive fullscreen mode, hiding the status bar
    /// and navigation bar. A swipe gesture will reveal them temporarily.
    /// </summary>
    private static void EnableImmersiveMode(
        Activity activity)
    {
        var window = activity.Window;
        if (window is null)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(35))
            {
                // API 35+ defaults to edge-to-edge; calling
                // SetDecorFitsSystemWindows is unnecessary and obsolete.
                window.SetDecorFitsSystemWindows(false);
            }

            var controller = window.InsetsController;
            if (controller is not null)
            {
                controller.Hide(WindowInsets.Type.SystemBars());
                controller.SystemBarsBehavior =
                    (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
        }
        else
        {
            window.DecorView.SystemUiFlags =
                SystemUiFlags.Fullscreen |
                SystemUiFlags.HideNavigation |
                SystemUiFlags.ImmersiveSticky |
                SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.LayoutHideNavigation |
                SystemUiFlags.LayoutStable;
        }
    }

    /// <summary>
    /// Requests engine teardown. Fully non-blocking on the caller.
    /// </summary>
    /// <remarks>
    /// Invoked by <see cref="GameLifecycleCallbacks.OnActivityDestroyed"/> on
    /// the Android UI thread. <see cref="GameHost.StopAsync"/> and
    /// <see cref="Game.Dispose"/> must run on the game thread, so this simply
    /// enqueues a <c>Stop</c> command onto the self-managed render thread and
    /// returns immediately. The render thread runs teardown while pumping the
    /// game synchronization context to completion, then releases EGL and exits.
    /// The UI thread never blocks — critical state is already persisted during
    /// <see cref="GameHost.PauseAsync"/> (which fires on <c>OnActivityPaused</c>, before
    /// destroy), so fire-and-forget teardown is safe.
    /// </remarks>
    internal void Stop()
    {
        if (Interlocked.Exchange(ref this._isStopRequested, 1) != 0)
        {
            return;
        }

        Trace.Write(0x1c7d4e90, "[AndroidGameEngine] Stop: enqueuing non-blocking teardown");

        // Close the liveness gate so the stall watchdog stops expecting
        // heartbeats during teardown.
        this._renderLoopActive = false;

        this._renderThread?.NotifyStop();
    }

    /// <summary>
    /// Forwards touch events from the game surface to the game engine.
    /// </summary>
    private sealed class GameTouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly AndroidGameEngine _engine;

        public GameTouchListener(
            AndroidGameEngine engine)
        {
            this._engine = engine;
        }

        public bool OnTouch(
            View? v,
            MotionEvent? e)
        {
            if (e is null)
            {
                return false;
            }

            int pointerIndex;

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                case MotionEventActions.PointerDown:
                    pointerIndex = e.ActionIndex;
                    this._engine._host?.QueuePointerEvent(new PointerEventData
                    {
                        State = Framework.Input.PointerEventKind.Began,
                        PointerId = e.GetPointerId(pointerIndex),
                        PointerType = Framework.Input.PointerDevice.Touch,
                        Position = new Vector2(e.GetX(pointerIndex), e.GetY(pointerIndex))
                    });
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.PointerUp:
                    pointerIndex = e.ActionIndex;
                    this._engine._host?.QueuePointerEvent(new PointerEventData
                    {
                        State = Framework.Input.PointerEventKind.Ended,
                        PointerId = e.GetPointerId(pointerIndex),
                        PointerType = Framework.Input.PointerDevice.Touch,
                        Position = new Vector2(e.GetX(pointerIndex), e.GetY(pointerIndex))
                    });
                    return true;

                case MotionEventActions.Move:
                    for (int i = 0; i < e.PointerCount; i++)
                    {
                        this._engine._host?.QueuePointerEvent(new PointerEventData
                        {
                            State = Framework.Input.PointerEventKind.Moved,
                            PointerId = e.GetPointerId(i),
                            PointerType = Framework.Input.PointerDevice.Touch,
                            Position = new Vector2(e.GetX(i), e.GetY(i))
                        });
                    }
                    return true;

                // The system took the gesture away (the window lost focus, a system gesture
                // claimed it): a cancel, not a release, so nothing acts on it
                case MotionEventActions.Cancel:
                    for (int i = 0; i < e.PointerCount; i++)
                    {
                        this._engine._host?.QueuePointerEvent(new PointerEventData
                        {
                            State = Framework.Input.PointerEventKind.Canceled,
                            PointerId = e.GetPointerId(i),
                            PointerType = Framework.Input.PointerDevice.Touch,
                            Position = new Vector2(e.GetX(i), e.GetY(i))
                        });
                    }
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Forwards hardware key events from the GL surface to the game engine.
    /// Also handles the legacy back button (KEYCODE_BACK) on API &lt; 33.
    /// </summary>
    private sealed class GameKeyListener : Java.Lang.Object, View.IOnKeyListener
    {
        private readonly AndroidGameEngine _engine;

        public GameKeyListener(
            AndroidGameEngine engine)
        {
            this._engine = engine;
        }

        public bool OnKey(
            View? v,
            Keycode keyCode,
            AndroidKeyEvent? e)
        {
            if (e is null)
            {
                return false;
            }

            if (this._engine._gamepads?.TryHandleKey(keyCode, e) == true)
            {
                return true;
            }

            var key = KeyMap.FromKeycode(keyCode);
            if (key == Key.None)
            {
                return false;
            }

            var modifiers = KeyMap.FromMetaState((MetaKeyStates)e.MetaState);
            bool isRepeat = e.RepeatCount > 0;

            switch (e.Action)
            {
                case KeyEventActions.Down:
                    this._engine._inputService?.QueueKeyEvent(
                        KeyEventKind.Pressed, key, modifiers, isRepeat, (int)keyCode);
                    return true;

                case KeyEventActions.Up:
                    this._engine._inputService?.QueueKeyEvent(
                        KeyEventKind.Released, key, modifiers, virtualKey: (int)keyCode);
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Handles the predictive back gesture on API 33+ by queuing a
    /// <see cref="Key.Back"/> pressed+released pair.
    /// </summary>
    private sealed class GameOnBackInvokedCallback : Java.Lang.Object, AndroidWindow.IOnBackInvokedCallback
    {
        private readonly AndroidGameEngine _engine;

        public GameOnBackInvokedCallback(
            AndroidGameEngine engine)
        {
            this._engine = engine;
        }

        public void OnBackInvoked()
        {
            this._engine._inputService?.QueueKeyEvent(KeyEventKind.Pressed, Key.Back, ModifierKeys.None);
            this._engine._inputService?.QueueKeyEvent(KeyEventKind.Released, Key.Back, ModifierKeys.None);
        }
    }

    /// <summary>
    /// Observes the host activity lifecycle to pause, resume, and stop
    /// the engine automatically — no user code required beyond <see cref="Run"/>.
    /// </summary>
    private sealed class GameLifecycleCallbacks : Java.Lang.Object, Application.IActivityLifecycleCallbacks
    {
        private readonly Activity _activity;
        private readonly AndroidGameEngine _engine;

        public GameLifecycleCallbacks(
            Activity activity,
            AndroidGameEngine engine)
        {
            this._activity = activity;
            this._engine = engine;
        }

        public void OnActivityPaused(
            Activity activity)
        {
            if (activity == this._activity)
            {
                this._engine.OnPause();
            }
        }

        public void OnActivityResumed(
            Activity activity)
        {
            if (activity == this._activity)
            {
                this._engine.OnResume();
            }
        }

        public void OnActivityDestroyed(
            Activity activity)
        {
            if (activity == this._activity)
            {
                // Capture WHY the activity is being destroyed. Android destroys
                // an activity for several distinct reasons while keeping the
                // process (and its process-global/static state) alive:
                //   - IsChangingConfigurations: a configuration change not
                //     absorbed by the activity's ConfigChanges is recreating it.
                //   - IsFinishing (and not changing configurations): a genuine
                //     finish (back press / finish()); the activity is gone for
                //     good.
                //   - neither: the system/OEM is reclaiming the backgrounded
                //     activity (e.g. the "Don't keep activities" developer
                //     option, or memory pressure).
                bool isFinishing = activity.IsFinishing;
                bool isChangingConfigurations = activity.IsChangingConfigurations;

                Trace.Write(0x4b9e0a17, FormattableString.Invariant(
                    $"[AndroidGameEngine] OnActivityDestroyed: stopping engine (IsFinishing={isFinishing}, IsChangingConfigurations={isChangingConfigurations})"));

                // Before the stop, which the render thread runs next: a finished activity ends the
                // process once the game has stopped. Not when Android will make another in its place
                // (a recreate, or the system reclaiming it in the background): the process waits for
                // it, and RecreateCallbacks starts the app again in a new one when it comes
                this._engine._isExitRequested = isFinishing && !isChangingConfigurations;

                try
                {
                    // Fully non-blocking: enqueue teardown onto the self-managed
                    // render thread and return immediately. The render thread
                    // runs Game.Stop()/Dispose() while pumping the game
                    // synchronization context, within a time limit, then releases
                    // EGL and exits. The UI thread never blocks here, so a wedged
                    // game thread can't escalate to a teardown ANR (SIGQUIT).
                    this._engine.Stop();
                }
                catch (Exception ex)
                {
                    // Never let a teardown failure propagate out of a
                    // lifecycle callback — that would itself crash the app.
                    Trace.Write(0x4b9e0a1a, "[AndroidGameEngine] OnActivityDestroyed: teardown faulted", ex);
                }

                activity.Application?.UnregisterActivityLifecycleCallbacks(this);

            }
        }

        public void OnActivityCreated(
            Activity activity,
            global::Android.OS.Bundle? savedInstanceState)
        {
        }

        public void OnActivityStarted(
            Activity activity)
        {
        }

        public void OnActivityStopped(
            Activity activity)
        {
        }

        public void OnActivitySaveInstanceState(
            Activity activity,
            global::Android.OS.Bundle outState)
        {
        }
    }

    // The system's reports of its memory, from TRIM_MEMORY_RUNNING_LOW up
    /// <summary>
    /// For the life of the process: an activity of the game's class made after the game's own, in its
    /// place, starts the app again in a new process (see the remarks on
    /// <see cref="AndroidGameEngine"/>). Android calls this from inside the new activity's
    /// <c>base.OnCreate</c>, before its own code goes on to make a game. Activities of other classes
    /// (an ad's, a purchase's, sharing's, a sign-in's) come and go over the game untouched.
    /// </summary>
    private sealed class RecreateCallbacks(
        Activity gameActivity) : Java.Lang.Object, Application.IActivityLifecycleCallbacks
    {
        private readonly string? _gameActivityClass = gameActivity.ComponentName?.ClassName;

        public void OnActivityCreated(
            Activity activity,
            global::Android.OS.Bundle? savedInstanceState)
        {
            if ((activity == gameActivity)
                || (this._gameActivityClass is null)
                || (activity.ComponentName?.ClassName != this._gameActivityClass))
            {
                return;
            }

            Trace.Write(0x5e2b7c31, "[AndroidGameEngine] Another of the game's activity in the process: starting the app again in a new one");

            // The launch intent, not the activity's, whose link the game had already
            var intent = activity.PackageManager?.GetLaunchIntentForPackage(activity.PackageName!)
                ?? new Intent(activity, activity.Class);
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
            activity.StartActivity(intent);

            activity.Finish();
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
        }

        public void OnActivityStarted(
            Activity activity)
        {
        }

        public void OnActivityResumed(
            Activity activity)
        {
        }

        public void OnActivityPaused(
            Activity activity)
        {
        }

        public void OnActivityStopped(
            Activity activity)
        {
        }

        public void OnActivitySaveInstanceState(
            Activity activity,
            global::Android.OS.Bundle outState)
        {
        }

        public void OnActivityDestroyed(
            Activity activity)
        {
        }
    }

    private sealed class MemoryCallbacks(
        GameHost host) : Java.Lang.Object, IComponentCallbacks2
    {
        public void OnTrimMemory(
            [global::Android.Runtime.GeneratedEnum] TrimMemory level)
        {
            if (level >= TrimMemory.RunningLow)
            {
                host.NotifyLowMemory();
            }
        }

        public void OnLowMemory()
        {
            host.NotifyLowMemory();
        }

        public void OnConfigurationChanged(
            global::Android.Content.Res.Configuration newConfig)
        {
        }
    }
}
