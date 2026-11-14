// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAnimation;
using CoreGraphics;
using DotGame.Framework.Content;
using DotGame.Framework.Input;
using DotGame.Framework.iOS.Audio;
using DotGame.Framework.iOS.Content;
using DotGame.Framework.iOS.Graphics;
using DotGame.Framework.iOS.Input;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common;
using DotGame.Framework.Platform.Content;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;
using Foundation;
using Metal;
using MetalKit;
using UIKit;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DotGame.Framework.iOS;

/// <summary>
/// Runs a game on iOS, drawing with Metal.
/// </summary>
/// <remarks>
/// The engine does not own the <see cref="UIApplicationDelegate"/> or the
/// <see cref="UIWindow"/>. The host application creates both as part of
/// its normal iOS lifecycle and then calls
/// <see cref="Run"/> to hand the window to the
/// engine. The engine creates a <see cref="MTKView"/> and a
/// <see cref="UIViewController"/>, sets them as the window's root, and
/// subscribes to application lifecycle notifications automatically.
/// </remarks>
/// <example>
/// <code>
/// // In your AppDelegate.FinishedLaunching:
/// new iOSGameEngine(this.Window).Run(new MyGame());
/// </code>
/// </example>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The game engine lives for the lifetime of the app.")]
public sealed class iOSGameEngine
{
    private Game _game = null!;
    private GameHost _host = null!;
    private Input.Gamepads? _gamepads;
    private MTKView? _metalView;
    private CADisplayLink? _displayLink;
    private bool _hasDrawnFirstFrame;
    private TimeSpan _frameTimestamp;
    private GameMetalViewDelegate? _viewDelegate;
    private NSObject? _willResignActiveObserver;
    private NSObject? _didBecomeActiveObserver;
    private NSObject? _didEnterBackgroundObserver;
    private NSObject? _willEnterForegroundObserver;
    private NSObject? _willTerminateObserver;
    private NSObject? _memoryWarningObserver;
    private InputServiceStrategy? _inputService;
    private readonly UIWindow _window;

    // Safety cap on pumping the main thread while the game stops, as the app terminates
    private static readonly TimeSpan StopPumpCap = TimeSpan.FromSeconds(2);

    internal GraphicsServiceStrategy GraphicsService { get; private set; } = null!;

    internal ContentServiceStrategy ContentService { get; private set; } = null!;

    internal Game Game => this._game;

    internal GameHost Host => this._host;

    /// <summary>How the engine starts.</summary>
    public sealed class Options
    {
        /// <summary>Makes the options, with their defaults.</summary>
        public Options()
        {
        }

        /// <summary>
        /// How the game's audio sits with other apps' and the silent switch. The default is
        /// <see cref="AudioCategory.Ambient"/>.
        /// </summary>
        public AudioCategory AudioCategory { get; set; } = AudioCategory.Ambient;

        /// <summary>
        /// Whether the home indicator fades away after a moment without touches, which suits a game
        /// and is the default. It comes back with a touch.
        /// </summary>
        public bool AutoHidesHomeIndicator { get; set; } = true;

        /// <summary>
        /// The screen edges where a swipe reaches the game first, and the system's gesture needs a
        /// second one. All of them by default, as a game's drag shouldn't open Control Center.
        /// </summary>
        public UIRectEdge DeferredSystemGestureEdges { get; set; } = UIRectEdge.All;

    }

    private readonly Options _options;

    /// <summary>
    /// Makes an engine for a window, with these options or the defaults; it does nothing until
    /// <see cref="Run"/>.
    /// </summary>
    /// <param name="window">
    /// The host window. Can be any <see cref="UIWindow"/> instance —
    /// no special subclass is required.
    /// </param>
    /// <param name="options">Engine configuration.</param>
    public iOSGameEngine(
        UIWindow window,
        Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        this._window = window;
        this._options = options ?? new Options();
    }

    /// <summary>
    /// Runs the game in the window. A <see cref="MTKView"/> is created, set as the window's root view
    /// controller, and the full game lifecycle (rendering, input, pause/resume, teardown) is managed
    /// automatically via <see cref="NSNotificationCenter"/> notifications.
    /// </summary>
    /// <param name="game">The game instance to run.</param>
    /// <exception cref="InvalidOperationException">The engine is already running a game.</exception>
    public void Run(
        Game game)
    {
        ArgumentNullException.ThrowIfNull(game);

        if (this._game is not null)
        {
            throw new InvalidOperationException("The engine is already running a game.");
        }

        this._game = game;
        var window = this._window;

        this._host = new GameHost(game);

        // Create the Metal device
        var device = MTLDevice.SystemDefault
            ?? throw new InvalidOperationException("Metal is not supported on this device.");

        // Create the Metal view. Its own timer is paused: a display link draws
        // it (see OnDisplayLink), so each frame has the display's timestamp.
        var screenBounds = UIScreen.MainScreen.Bounds;
        var metalView = new MTKView(screenBounds, device)
        {
            ColorPixelFormat = MTLPixelFormat.BGRA8Unorm,
            FramebufferOnly = true,
            Paused = true,
            EnableSetNeedsDisplay = false,
            AutoResizeDrawable = true,
            MultipleTouchEnabled = true,
        };
        this._metalView = metalView;

        // Set the delegate BEFORE ContentScaleFactor — changing the
        // scale factor triggers drawableSizeWillChange:, and the
        // delegate must already be wired up to receive it.
        // The delegate must be stored as a field to prevent GC
        // collection, because MTKView.delegate is a weak ObjC ref.
        this._viewDelegate = new GameMetalViewDelegate(this);
        metalView.Delegate = this._viewDelegate;

        metalView.ContentScaleFactor = UIScreen.MainScreen.Scale;

        // Establish the game thread identity and synchronization
        // context on the current (main) thread BEFORE the view controller
        // is attached (showing the window lays it out, and its safe area
        // callbacks report the display to the game) and before kicking off
        // any async work. Display link and MetalKit callbacks are also
        // dispatched on the main thread, so the game thread and the
        // run loop's thread are the same thread.
        //
        // This must be done outside of an async method because
        // AsyncTaskMethodBuilder.Start saves and restores
        // SynchronizationContext.Current, which would revert a
        // SetSynchronizationContext call made inside the async
        // method body.
        GameHost.InitializeGameThread();

        // Create view controller and set as window root
        var viewController = new GameViewController(metalView, this);
        window.RootViewController = viewController;
        window.MakeKeyAndVisible();

        // The launch screen: the app's launch storyboard, which the system showed
        // while it launched, kept over the game view until its first frame is on
        // screen (the frame's presented handler reports it to the host)
        this._host.ShowLaunchScreen(new LaunchScreenStrategy(viewController.View!));

        // Create the services, report the display so the game's OnInitialize
        // can read ClientBounds, and start the game. Startup advances with each
        // display link frame; the view shows the launch color until the game's
        // first frame.
        var services = this.CreateServices(device);
        var scale = UIScreen.MainScreen.Scale;
        this.OnViewSizeChanged(
            (int)(screenBounds.Width * scale),
            (int)(screenBounds.Height * scale),
            window.SafeAreaInsets,
            scale);
        this._host.BeginStartup(services);
        this._gamepads = new Input.Gamepads(this._host);

        // The frame source: one callback per display refresh, on the main run loop
        this._displayLink = CADisplayLink.Create(this.OnDisplayLink);
        this._displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);

        // Observe application lifecycle via NSNotificationCenter so the
        // sample app's UIApplicationDelegate does not need to forward
        // any events to the engine.
        var center = NSNotificationCenter.DefaultCenter;

        // Inactive (Control Center, a call banner, the app switcher): the game stops updating and
        // keeps its picture. Hidden: no more GPU work, which the system wouldn't allow anyway.
        this._willResignActiveObserver = center.AddObserver(
            UIApplication.WillResignActiveNotification,
            _ => this.OnPause());

        this._didBecomeActiveObserver = center.AddObserver(
            UIApplication.DidBecomeActiveNotification,
            _ => this.OnResume());

        this._didEnterBackgroundObserver = center.AddObserver(
            UIApplication.DidEnterBackgroundNotification,
            _ => this.OnEnterBackground());

        this._willEnterForegroundObserver = center.AddObserver(
            UIApplication.WillEnterForegroundNotification,
            _ => this.OnEnterForeground());

        this._memoryWarningObserver = center.AddObserver(
            UIApplication.DidReceiveMemoryWarningNotification,
            _ => this._host.NotifyLowMemory());

        this._willTerminateObserver = center.AddObserver(
            UIApplication.WillTerminateNotification,
            _ => this.Stop());
    }

    /// <summary>
    /// Creates the platform's services.
    /// </summary>
    private PlatformServices CreateServices(
        IMTLDevice device)
    {
        Trace.Write(0x9e12e7df, "Initializing iOS engine...");

        // Create the required game services
        this.GraphicsService = new GraphicsServiceStrategy(device, this._game.PreferredMultisampleCount);
        var graphicsServiceStrategy = new Strategy<IGraphicsServiceStrategy>(this.GraphicsService);
        var iosAudioService = new AudioServiceStrategy(this._options.AudioCategory);
        var audioService = new Strategy<IAudioServiceStrategy>(iosAudioService);
        this._inputService = new InputServiceStrategy(this._host, this._window!);
        var inputService = new Strategy<IInputServiceStrategy>(this._inputService);

        // Compute content base path from the app bundle. The content root is
        // a build-time fact carried by the manifest; prepend the app bundle
        // path to locate the physical assets.
        var contentManifest = ContentManifest.Registry.BuildSnapshot();
        var bundlePath = NSBundle.MainBundle.BundlePath;
        var contentBasePath = System.IO.Path.Combine(bundlePath, contentManifest.BasePath);

        // Create the content service
        this.ContentService = new ContentServiceStrategy(this.GraphicsService, contentBasePath, contentManifest)
        {
            AudioService = iosAudioService,
        };
        var contentServiceStrategy = new Strategy<IContentServiceStrategy>(this.ContentService);

        return new PlatformServices(
            contentServiceStrategy,
            graphicsServiceStrategy,
            audioService,
            inputService);
    }

    /// <summary>
    /// The display link's callback: draws the Metal view, at the time the frame
    /// will be shown.
    /// </summary>
    private void OnDisplayLink()
    {
        this._frameTimestamp = TimeSpan.FromSeconds(this._displayLink!.TargetTimestamp);
        this.UpdateRefreshRate();
        this.ApplyPreferredFrameRate();

        // NOTE: Drained each frame, as what's autoreleased (the drawable among it) otherwise stays
        // until the main run loop goes idle, which a loaded main thread may rarely do
        nint pool = ObjCAutoreleasePool.Push();
        try
        {
            this._metalView?.Draw();
        }
        finally
        {
            ObjCAutoreleasePool.Pop(pool);
        }
    }

    // The frame rate last asked of the display link, where 0 is the display's own
    private int _appliedFrameRate;

    /// <summary>
    /// Asks the display link for the frame rate the game would like, if that's changed. A ProMotion
    /// iPhone goes above 60 only if the app's Info.plist also sets
    /// <c>CADisableMinimumFrameDurationOnPhone</c>.
    /// </summary>
    private void ApplyPreferredFrameRate()
    {
        int frameRate = this.GraphicsService.PreferredFrameRate ?? 0;
        if (frameRate != this._appliedFrameRate)
        {
            this._appliedFrameRate = frameRate;
            float rate = frameRate;
            this._displayLink!.PreferredFrameRateRange = new CAFrameRateRange { Minimum = rate, Maximum = rate, Preferred = rate };
        }
    }

    /// <summary>
    /// The rate frames really come at, for the continuation budget: the screen's maximum isn't it,
    /// as a ProMotion iPhone runs at 60 unless the app's plist asks for more, and treating those
    /// frames as overruns pins the budget at its floor.
    /// </summary>
    private double _refreshRateHz = UIScreen.MainScreen.MaximumFramesPerSecond;

    // What was last reported to the host, to report a new rate with
    private DisplayInfo _display;

    private void UpdateRefreshRate()
    {
        double interval = this._displayLink!.TargetTimestamp - this._displayLink.Timestamp;
        if (interval <= 0)
        {
            return;
        }

        double refreshRate = Math.Round(1 / interval);
        if (Math.Abs(refreshRate - this._refreshRateHz) > 1)
        {
            this._refreshRateHz = refreshRate;
            this._display = this._display with { RefreshRateHz = refreshRate };
            this._host.SetDisplay(this._display);
        }
    }

    /// <summary>
    /// Called when the Metal view size changes: reports the display to the game.
    /// </summary>
    internal void OnViewSizeChanged(
        int width,
        int height,
        UIEdgeInsets safeAreaInsets,
        nfloat contentScaleFactor)
    {
        this.GraphicsService?.SetViewportSize(width, height);

        int safeLeft = (int)(safeAreaInsets.Left * contentScaleFactor);
        int safeTop = (int)(safeAreaInsets.Top * contentScaleFactor);
        int safeRight = (int)(safeAreaInsets.Right * contentScaleFactor);
        int safeBottom = (int)(safeAreaInsets.Bottom * contentScaleFactor);

        var canvasBounds = new RectangleInt(0, 0, width, height);
        var safeBounds = new RectangleInt(
            safeLeft,
            safeTop,
            width - safeLeft - safeRight,
            height - safeTop - safeBottom);

        // iOS does not expose physical PPI directly. Apple's documented
        // baseline is 163 PPI per point on iPhone Retina (132 on iPad);
        // we use 163 × contentScaleFactor as a close approximation that
        // matches the platform-native interpretation of points.
        var displayDpi = (float)(163.0 * contentScaleFactor);

        this._display = new DisplayInfo(canvasBounds, safeBounds, displayDpi, this._refreshRateHz);
        this._host.SetDisplay(this._display);
    }

    // How long the app stays in the background for an asynchronous OnPause to finish. The system
    // allows about five seconds on the main thread.
    private const long BackgroundPumpCapMs = 3000;

    private Task? _pauseTask;

    internal void OnPause()
    {
        this._pauseTask = this._host.PauseAsync();
        SafeAsync.Invoke(0x9e12e7e0, this._pauseTask);
    }

    internal void OnResume()
    {
        SafeAsync.Invoke(0x9e12e7e1, this._host.ResumeAsync());
    }

    // No display link frames arrive from here, so what an asynchronous OnPause awaits is pumped
    // now, under a background task, so a save isn't left for a return that may never come
    private void OnEnterBackground()
    {
        if (this._displayLink is not null)
        {
            this._displayLink.Paused = true;
        }

        var pauseTask = this._pauseTask;
        if ((pauseTask is null) || pauseTask.IsCompleted)
        {
            return;
        }

        var application = UIApplication.SharedApplication;
        nint backgroundTask = application.BeginBackgroundTask("OnPause", null);

        long start = Environment.TickCount64;
        while (!pauseTask.IsCompleted && ((Environment.TickCount64 - start) < BackgroundPumpCapMs))
        {
            GameHost.DrainContinuations();
            Thread.Sleep(1);
        }

        if (backgroundTask != UIApplication.BackgroundTaskInvalid)
        {
            application.EndBackgroundTask(backgroundTask);
        }
    }

    private void OnEnterForeground()
    {
        if (this._displayLink is not null)
        {
            this._displayLink.Paused = false;
        }
    }

    /// <summary>
    /// Stops the game and disposes it, when the app terminates. Runs on the main
    /// (game) thread, which it pumps while the game stops (up to a safety cap),
    /// since nothing else will: the display link is stopped first.
    /// </summary>
    internal void Stop()
    {
        this._displayLink?.Invalidate();
        this._displayLink = null;

        // The host lets a startup still in progress finish first, so the game
        // never stops alongside it
        var stopTask = this._host.StopAsync();

        long start = Stopwatch.GetTimestamp();
        while (!stopTask.IsCompleted)
        {
            GameHost.DrainContinuations();

            if (stopTask.IsCompleted)
            {
                break;
            }

            if (Stopwatch.GetElapsedTime(start) > StopPumpCap)
            {
                Trace.Write(0x9e12e7e2, "[iOSGameEngine] Stop: pump exceeded cap; abandoning wait");
                break;
            }

            System.Threading.Thread.Sleep(1);
        }

        if (stopTask.IsFaulted)
        {
            Trace.Write(0x9e12e7e3, "[iOSGameEngine] Stop: game teardown faulted", stopTask.Exception);
        }

        this._game.Dispose();

        // Remove lifecycle observers
        var center = NSNotificationCenter.DefaultCenter;

        if (this._willResignActiveObserver is not null)
        {
            center.RemoveObserver(this._willResignActiveObserver);
            this._willResignActiveObserver = null;
        }

        if (this._didBecomeActiveObserver is not null)
        {
            center.RemoveObserver(this._didBecomeActiveObserver);
            this._didBecomeActiveObserver = null;
        }

        if (this._didEnterBackgroundObserver is not null)
        {
            center.RemoveObserver(this._didEnterBackgroundObserver);
            this._didEnterBackgroundObserver = null;
        }

        if (this._willEnterForegroundObserver is not null)
        {
            center.RemoveObserver(this._willEnterForegroundObserver);
            this._willEnterForegroundObserver = null;
        }

        if (this._willTerminateObserver is not null)
        {
            center.RemoveObserver(this._willTerminateObserver);
            this._willTerminateObserver = null;
        }

        if (this._memoryWarningObserver is not null)
        {
            center.RemoveObserver(this._memoryWarningObserver);
            this._memoryWarningObserver = null;
        }
    }

    /// <summary>
    /// View controller that manages the Metal view and handles
    /// safe area insets and touch events.
    /// </summary>
    private sealed class GameViewController : UIViewController
    {
        private readonly MTKView _metalView;
        private readonly iOSGameEngine _engine;
        private readonly Keyboard _keyboard;
        private NSObject? _keyboardWillChangeFrameObserver;
        private NSObject? _keyboardWillHideObserver;
        private NSObject? _willResignActiveObserver;

        public GameViewController(
            MTKView metalView,
            iOSGameEngine engine)
        {
            this._metalView = metalView;
            this._engine = engine;
            this._keyboard = new Keyboard(() => engine._inputService);
        }

        // A hardware keyboard's presses go to the first responder: the game view, unless a text
        // field has the keyboard
        public override bool CanBecomeFirstResponder => true;

        public override void ViewDidAppear(
            bool animated)
        {
            base.ViewDidAppear(animated);
            this.BecomeFirstResponder();
        }

        public override bool ResignFirstResponder()
        {
            this._keyboard.ReleaseAll();
            return base.ResignFirstResponder();
        }

        public override void LoadView()
        {
            this.View = this._metalView;
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();

            // The view isn't resized for the soft keyboard, so the part of it that the keyboard
            // covers is added to the safe area instead, which shortens the game's layout area.
            this._keyboardWillChangeFrameObserver = UIKeyboard.Notifications.ObserveWillChangeFrame(
                (sender, e) => this.SetKeyboardFrame(e.FrameEnd));

            this._keyboardWillHideObserver = UIKeyboard.Notifications.ObserveWillHide(
                (sender, e) => this.SetKeyboardFrame(CGRect.Empty));

            // Presses stop arriving when the app resigns active, so no held key's release would
            this._willResignActiveObserver = UIApplication.Notifications.ObserveWillResignActive(
                (sender, e) => this._keyboard.ReleaseAll());
        }

        protected override void Dispose(
            bool disposing)
        {
            if (disposing)
            {
                this._keyboardWillChangeFrameObserver?.Dispose();
                this._keyboardWillChangeFrameObserver = null;

                this._keyboardWillHideObserver?.Dispose();
                this._keyboardWillHideObserver = null;

                this._willResignActiveObserver?.Dispose();
                this._willResignActiveObserver = null;

                this._keyboard.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Sets the bottom additional safe-area inset to how far the keyboard, whose frame is in
        /// screen coordinates, covers the bottom of the view. Changing it raises
        /// <see cref="ViewSafeAreaInsetsDidChange"/>, which passes the new safe area to the game.
        /// </summary>
        private void SetKeyboardFrame(
            CGRect keyboardFrame)
        {
            var view = this.View;
            if (view is null)
            {
                return;
            }

            nfloat coveredHeight = 0;

            if (!keyboardFrame.IsEmpty)
            {
                var frame = view.ConvertRectFromCoordinateSpace(keyboardFrame, UIScreen.MainScreen.CoordinateSpace);
                var bounds = view.Bounds;

                if (frame.IntersectsWith(bounds))
                {
                    coveredHeight = bounds.Bottom - frame.Top;
                }
            }

            // The covered height includes the system's own bottom inset (the home indicator), which
            // the safe area already has, so only the remainder is added.
            var additional = this.AdditionalSafeAreaInsets;
            var systemBottomInset = view.SafeAreaInsets.Bottom - additional.Bottom;
            var bottom = (nfloat)Math.Max(0.0, (double)(coveredHeight - systemBottomInset));

            if (bottom != additional.Bottom)
            {
                this.AdditionalSafeAreaInsets = new UIEdgeInsets(additional.Top, additional.Left, bottom, additional.Right);
            }
        }

        public override bool PrefersStatusBarHidden()
        {
            return true;
        }

        public override bool PrefersHomeIndicatorAutoHidden => this._engine._options.AutoHidesHomeIndicator;

        public override UIRectEdge PreferredScreenEdgesDeferringSystemGestures => this._engine._options.DeferredSystemGestureEdges;

        public override void ViewSafeAreaInsetsDidChange()
        {
            base.ViewSafeAreaInsetsDidChange();
            this.UpdateSafeBounds();
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();
            this.UpdateSafeBounds();
        }

        private void UpdateSafeBounds()
        {
            if (this.View is null)
            {
                return;
            }

            var scale = this._metalView.ContentScaleFactor;
            var viewBounds = this.View.Bounds;
            var safeInsets = this.View.SafeAreaInsets;

            int width = (int)(viewBounds.Width * scale);
            int height = (int)(viewBounds.Height * scale);

            int safeLeft = (int)(safeInsets.Left * scale);
            int safeTop = (int)(safeInsets.Top * scale);
            int safeRight = (int)(safeInsets.Right * scale);
            int safeBottom = (int)(safeInsets.Bottom * scale);

            var canvasBounds = new RectangleInt(0, 0, width, height);
            var safeBounds = new RectangleInt(
                safeLeft,
                safeTop,
                width - safeLeft - safeRight,
                height - safeTop - safeBottom);

            // 163 PPI per point on iPhone Retina baseline (see OnViewSizeChanged).
            var displayDpi = (float)(163.0 * scale);

            this._engine._display = new DisplayInfo(canvasBounds, safeBounds, displayDpi, this._engine._refreshRateHz);
            this._engine._host.SetDisplay(this._engine._display);
        }

        public override void TouchesBegan(
            NSSet touches,
            UIEvent? evt)
        {
            this.HandleTouches(
                touches,
                Framework.Input.PointerEventKind.Began);
        }

        public override void TouchesMoved(
            NSSet touches,
            UIEvent? evt)
        {
            this.HandleTouches(
                touches,
                Framework.Input.PointerEventKind.Moved);
        }

        public override void TouchesEnded(
            NSSet touches,
            UIEvent? evt)
        {
            this.HandleTouches(
                touches,
                Framework.Input.PointerEventKind.Ended);
        }

        // UIKit took the touches away (an alert, Control Center, a system gesture): a cancel,
        // not a release, so nothing acts on it
        public override void TouchesCancelled(
            NSSet touches,
            UIEvent? evt)
        {
            this.HandleTouches(
                touches,
                Framework.Input.PointerEventKind.Canceled);
        }

        private void HandleTouches(
            NSSet touches,
            Framework.Input.PointerEventKind state)
        {
            var host = this._engine._host;
            if (host is null)
            {
                return;
            }

            var scale = this._metalView.ContentScaleFactor;
            var view = this.View;

            foreach (UITouch touch in touches)
            {
                var location = touch.LocationInView(view);

                host.QueuePointerEvent(new PointerEventData
                {
                    State = state,
                    PointerId = touch.Handle.GetHashCode(),
                    PointerType = Framework.Input.PointerDevice.Touch,
                    Position = new Vector2((float)(location.X * scale), (float)(location.Y * scale))
                });
            }
        }

        public override void PressesBegan(
            NSSet<UIPress> presses,
            UIPressesEvent? evt)
        {
            if (!this._keyboard.HandlePresses(presses, KeyEventKind.Pressed))
            {
                base.PressesBegan(presses, evt!);
            }
        }

        public override void PressesEnded(
            NSSet<UIPress> presses,
            UIPressesEvent? evt)
        {
            if (!this._keyboard.HandlePresses(presses, KeyEventKind.Released))
            {
                base.PressesEnded(presses, evt!);
            }
        }

        public override void PressesChanged(
            NSSet<UIPress> presses,
            UIPressesEvent? evt)
        {
            base.PressesChanged(presses, evt!);
        }

        public override void PressesCancelled(
            NSSet<UIPress> presses,
            UIPressesEvent? evt)
        {
            this._keyboard.HandlePresses(presses, KeyEventKind.Released);
            base.PressesCancelled(presses, evt!);
        }
    }

    /// <summary>
    /// MetalKit view delegate that draws each frame.
    /// </summary>
    /// <remarks>
    /// Both <see cref="DrawableSizeWillChange"/> and <see cref="Draw"/> are
    /// invoked on the main thread, which is also the game thread.
    /// <see cref="Draw"/> runs once per display link callback: it runs the
    /// game's frame (which, while the game is starting, only advances startup)
    /// and presents it, showing the launch color until the game draws.
    /// </remarks>
    private sealed class GameMetalViewDelegate : NSObject, IMTKViewDelegate
    {
        private readonly iOSGameEngine _engine;

        public GameMetalViewDelegate(
            iOSGameEngine engine)
        {
            this._engine = engine;
        }

        [Export("mtkView:drawableSizeWillChange:")]
        public void DrawableSizeWillChange(
            MTKView view,
            CGSize size)
        {
            this._engine.OnViewSizeChanged(
                (int)size.Width,
                (int)size.Height,
                view.SafeAreaInsets,
                view.ContentScaleFactor);
        }

        [Export("drawInMTKView:")]
        public void Draw(
            MTKView view)
        {
            var engine = this._engine;
            var host = engine._host;

            engine.GraphicsService.BeginFrame(view);

            bool drew = host.Tick(engine._frameTimestamp, canDraw: true);

            // Until the game has drawn (it starts, then loads its first scene), the view shows
            // the launch color, under the launch storyboard's overlay
            if (!drew && !engine._hasDrawnFirstFrame)
            {
                engine.GraphicsService.Clear(LaunchScreen.Background);
            }

            engine.GraphicsService.FinishPendingDrawing();

            // Grab these before EndFrame clears them. No drawable means the frame drew nothing to
            // the screen, or there was none to have: startup still advances.
            using var commandBuffer = engine.GraphicsService.CurrentCommandBuffer;
            using var drawable = engine.GraphicsService.CurrentDrawable;

            engine.GraphicsService.EndFrame();

            if (commandBuffer is not null)
            {
                if (drew && (drawable is not null) && !engine._hasDrawnFirstFrame)
                {
                    engine._hasDrawnFirstFrame = true;

                    // The simulator's drawables don't implement presented handlers; there the
                    // frame's command buffer completing, just before it's shown, stands in
                    if (ObjCRuntime.Runtime.Arch == ObjCRuntime.Arch.SIMULATOR)
                    {
                        commandBuffer.AddCompletedHandler(_ => host.NotifyFirstFramePresented());
                    }
                    else
                    {
                        drawable.AddPresentedHandler(_ => host.NotifyFirstFramePresented());
                    }
                }

                // NOTE: A frame with nothing encoded leaves the drawable undefined, which the
                // app switcher's snapshot could show
                if ((drawable is not null) && (drew || !engine._hasDrawnFirstFrame))
                {
                    commandBuffer.PresentDrawable(drawable);
                }

                commandBuffer.Commit();
            }
        }
    }
}
