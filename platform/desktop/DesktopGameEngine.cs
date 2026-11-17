// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotGame.Framework.Content;
using DotGame.Framework.Desktop.Audio;
using DotGame.Framework.Desktop.Content;
using DotGame.Framework.Desktop.Graphics;
using DotGame.Framework.Desktop.Input;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common;
using DotGame.Framework.Platform.Content;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DotGame.Framework.Desktop;

/// <summary>Runs a game on Windows, macOS or Linux, in a window of its own.</summary>
public sealed class DesktopGameEngine
{
    private Game? _game;
    private GameHost? _host;
    private GraphicsServiceStrategy? _graphicsService;
    private InputServiceStrategy? _inputService;
    private AudioServiceStrategy? _audioService;
    private bool _isRunning;

    // Last window size/DPI reported to the game via HandleBoundsChanged. Used
    // to detect changes when polling each frame. Some window-state transitions
    // (notably restoring from maximized on Windows) deliver the state-change
    // event before SDL has updated the queryable window size, so the value read
    // in the event handler is stale. Polling each frame catches the corrected
    // size on the following frame without waiting for another user resize.
    private int _reportedWindowW = -1;
    private int _reportedWindowH = -1;
    private float _reportedDpi = -1f;
    private float _reportedRefreshHz = -1f;
    private bool _isRefreshRateStale = true;

    // Safety cap on pumping the game thread while the game stops, after the window closes
    private static readonly TimeSpan StopPumpCap = TimeSpan.FromSeconds(5);

    /// <summary>How the engine starts. Only <see cref="Title"/> is required.</summary>
    public sealed class Options
    {
        /// <summary>Makes the options, with their defaults.</summary>
        public Options()
        {
        }

        /// <summary>The window's title.</summary>
        public required string Title { get; set; }

        /// <summary>
        /// How finished frames reach the screen. The default is <see cref="PresentMode.VSync"/>.
        /// </summary>
        public PresentMode PresentMode { get; set; } = PresentMode.VSync;

        /// <summary>
        /// Whether the GPU device runs SDL's debug mode, with its checks and any validation
        /// layers installed on the machine. Off by default. It's much slower, so leave it off in
        /// shipped games.
        /// </summary>
        public bool GraphicsDebug { get; set; }

        /// <summary>
        /// How big the window opens when it's a window, and what it returns to on leaving
        /// <see cref="WindowMode.Maximized"/> or <see cref="WindowMode.FullScreen"/>. The default is
        /// <see cref="WindowSize.Default"/>.
        /// </summary>
        public WindowSize WindowSize { get; set; } = WindowSize.Default;

        /// <summary>
        /// The state the window opens in. The default is <see cref="WindowMode.Windowed"/>.
        /// <see cref="DesktopGameEngine.WindowMode"/> changes it while the game runs.
        /// </summary>
        public WindowMode WindowMode { get; set; } = WindowMode.Windowed;

        /// <summary>
        /// Whether the window reopens as the player left it, with its last size, position and
        /// mode, rather than as <see cref="WindowSize"/> and <see cref="WindowMode"/> say. Those still
        /// decide if they've changed since, such as in an update to the game. On by default in
        /// Release builds and off in Debug builds, so runs are repeatable. The window's state is
        /// saved either way. A window that's no longer on a screen, or doesn't fit one, opens at
        /// <see cref="WindowSize"/>.
        /// </summary>
        public bool RememberWindow { get; set; } = !FeatureSwitches.IsDebug;
    }

    // The command-line switch that forgets the saved window state, so the window opens as the
    // options say (and is saved afresh when the game quits)
    private const string ResetWindowSwitch = "--reset-window";

    // The window's last normal (not maximized, minimized or fullscreen) bounds
    private WindowState _windowState = new WindowState();

    // What to open at when SDL can't say how large the screen is, in device-independent pixels
    private static readonly Vector2Int NoScreenSize = new Vector2Int(1600, 900);

    private readonly Options _options;

    /// <summary>Makes an engine with these options; it does nothing until <see cref="RunAsync"/>.</summary>
    public DesktopGameEngine(
        Options options)
    {
        this._options = options ?? throw new ArgumentNullException(nameof(options));
        this._windowModeBox = new WindowModeBox(options.WindowMode);
    }

    // The window's mode, replaced whole so any thread can read it
    private volatile WindowModeBox _windowModeBox;

    // A change to make, from any thread, for the frame loop to pick up
    private volatile WindowModeBox? _requestedWindowMode;

    private sealed class WindowModeBox(
        WindowMode value)
    {
        public WindowMode Value { get; } = value;
    }

    /// <summary>
    /// The state the window is in. Setting it asks for a change, from any thread, which starts by
    /// the next frame. Reading it says what the window is now, so it changes only once the change
    /// is complete, which can be a second later on systems that animate it. The game also sees that
    /// as a change of size. It follows the player's changes by the system's own controls too. Use
    /// <see cref="Options.WindowMode"/> to choose how the window opens.
    /// </summary>
    public WindowMode WindowMode
    {
        get => this._windowModeBox.Value;
        set => this._requestedWindowMode = new WindowModeBox(value);
    }

    /// <summary>
    /// The native OS window handle for the engine's host window, or
    /// <see cref="nint.Zero"/> if the window has not been created yet
    /// (or the current OS is unsupported). On Windows this is the
    /// HWND, on macOS the NSWindow*, and on Linux the X11 Window id
    /// or the Wayland wl_surface* depending on the active video
    /// driver.
    /// </summary>
    public nint NativeWindow { get; private set; }

    /// <summary>
    /// Raised once during <see cref="RunAsync"/> after the host window has
    /// been created, but before the game is initialized.
    /// </summary>
    public event EventHandler? Initialized;

    /// <summary>Opens the window and runs the game. The task completes when the game has stopped.</summary>
    public Task RunAsync(
        Game game)
    {
        ArgumentNullException.ThrowIfNull(game);

        if (this._game is not null)
        {
            throw new InvalidOperationException("This engine instance is already running a game.");
        }

        this._game = game;
        var host = new GameHost(game);
        this._host = host;

        Trace.Write(0x79764323, "Initializing SDL3...");

        // Prevent SDL from generating synthetic mouse events for touch input.
        // The game handles touch and mouse events independently, and the
        // synthetic events would cause the multi-touch tracker to see an extra
        // pointer (mouse pointerId=0 alongside the real touch pointerId),
        // incorrectly triggering pinch-to-zoom on a simple single-finger drag.
        SDL.SetHint("SDL_TOUCH_MOUSE_EVENTS", "0");

        // Initialize SDL
        if (!SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Audio | SDL.InitFlags.Gamepad))
        {
            throw new InvalidOperationException($"Failed to initialize SDL: {SDL.GetError()}");
        }

        try
        {
            // Create the window hidden and size it for its display (whose scale for the window
            // is only known once the window exists). It's shown once the GPU device can draw the
            // launch screen in it, so it never shows unpainted.
            var window = SDL.CreateWindow(this._options.Title, 640, 480, SDL.WindowFlags.Resizable | SDL.WindowFlags.HighPixelDensity | SDL.WindowFlags.Hidden);
            if (window == nint.Zero)
            {
                throw new InvalidOperationException($"Failed to create window: {SDL.GetError()}");
            }

            WindowOpening opening = this.SizeWindowForDisplay(window);

            // Resolve the native OS window handle and expose it via
            // NativeWindow so subscribers of Initialized (and any
            // later consumers) can publish it to platform services
            // (passkeys, native dialogs, etc.) without taking a
            // dependency on SDL.
            this.NativeWindow = GetNativeWindowHandle(window);

            this.Initialized?.Invoke(this, EventArgs.Empty);

            try
            {
                // Create GPU device with all supported shader formats.
                // Use property-based creation to disable optional Vulkan
                // features (anisotropy, depth clamping, clip distance,
                // indirect draw first instance) for broader device
                // compatibility.
                var gpuProps = SDL.CreateProperties();
                SDL.SetBooleanProperty(gpuProps, SDL.Props.GPUDeviceCreateShadersSPIRVBoolean, true);
                SDL.SetBooleanProperty(gpuProps, SDL.Props.GPUDeviceCreateShadersDXILBoolean, true);
                SDL.SetBooleanProperty(gpuProps, SDL.Props.GPUDeviceCreateShadersMSLBoolean, true);
                SDL.SetBooleanProperty(gpuProps, SDL.Props.GPUDeviceCreateFeatureAnisotropyBoolean, false);
                SDL.SetBooleanProperty(gpuProps, SDL.Props.GPUDeviceCreateFeatureDepthClampingBoolean, false);
                SDL.SetBooleanProperty(gpuProps, SDL.Props.GPUDeviceCreateFeatureClipDistanceBoolean, false);
                SDL.SetBooleanProperty(gpuProps, SDL.Props.GPUDeviceCreateFeatureIndirectDrawFirstInstanceBoolean, false);
                // NOTE: SDL's default is on, which also turns on the Vulkan and D3D12 validation
                // layers wherever they're installed
                SDL.SetBooleanProperty(gpuProps, SDL.Props.GPUDeviceCreateDebugModeBoolean, this._options.GraphicsDebug);
                var gpuDevice = SDL.CreateGPUDeviceWithProperties(gpuProps);
                SDL.DestroyProperties(gpuProps);

                if (gpuDevice == nint.Zero)
                {
                    throw new InvalidOperationException($"Failed to create GPU device: {SDL.GetError()}");
                }

                try
                {
                    // Claim window for GPU device
                    if (!SDL.ClaimWindowForGPUDevice(gpuDevice, window))
                    {
                        throw new InvalidOperationException($"Failed to claim window for GPU device: {SDL.GetError()}");
                    }

                    // V-sync by default, pacing frames to the display refresh rate, which
                    // reduces CPU usage and prevents tearing. Only V-sync is sure to be supported.
                    var presentMode = this._options.PresentMode switch
                    {
                        PresentMode.Mailbox => SDL.GPUPresentMode.Mailbox,
                        PresentMode.Immediate => SDL.GPUPresentMode.Immediate,
                        _ => SDL.GPUPresentMode.VSync,
                    };
                    if (!SDL.WindowSupportsGPUPresentMode(gpuDevice, window, presentMode))
                    {
                        presentMode = SDL.GPUPresentMode.VSync;
                    }

                    SDL.SetGPUSwapchainParameters(
                        gpuDevice,
                        window,
                        SDL.GPUSwapchainComposition.SDR,
                        presentMode);

                    Trace.Write(0xde8f3193, $"GPU Driver: {SDL.GetGPUDeviceDriver(gpuDevice)}");

                    // Create the platform services
                    this._graphicsService = new GraphicsServiceStrategy(gpuDevice, window, this._game!.PreferredMultisampleCount);
                    var graphicsServiceStrategy = new Strategy<IGraphicsServiceStrategy>(this._graphicsService);

                    var audioService = new Strategy<IAudioServiceStrategy>(new AudioServiceStrategy());
                    this._audioService = audioService.ToConcrete<AudioServiceStrategy>();

                    this._inputService = new InputServiceStrategy(host, window);
                    var inputServiceStrategy = new Strategy<IInputServiceStrategy>(this._inputService);

                    // The content root is a build-time fact carried by the
                    // manifest; prepend the application base directory to
                    // locate the physical assets on disk.
                    var contentManifest = ContentManifest.Registry.BuildSnapshot();
                    var contentBasePath = Path.Combine(AppContext.BaseDirectory, contentManifest.BasePath);
                    var contentService = new ContentServiceStrategy(this._graphicsService, contentBasePath, contentManifest)
                    {
                        MusicPlaybackListener = this._audioService,
                    };
                    var contentServiceStrategy = new Strategy<IContentServiceStrategy>(contentService);

                    // The launch screen, until the game's first frame. Its first frame is
                    // presented before the window is shown (the backends that present to a
                    // hidden window then show it at once), and again once it's shown, so at most
                    // one frame is unpainted
                    using var launchScreen = new LaunchScreenStrategy(this._graphicsService, window, contentService);
                    launchScreen.PresentFrame();
                    SDL.ShowWindow(window);
                    this.KeepWindowShape(window);
                    this.TrackWindowState(window);
                    if (opening.IsMaximized)
                    {
                        SDL.MaximizeWindow(window);
                    }

                    if (opening.IsFullScreen)
                    {
                        SDL.SetWindowFullscreen(window, true);
                    }

                    this.ApplyRequestedWindowMode(window);
                    host.ShowLaunchScreen(launchScreen);

                    // Establish the game thread identity and synchronization
                    // context on this thread BEFORE any async calls.
                    // This must be done outside of an async method because
                    // AsyncTaskMethodBuilder.Start saves and restores
                    // SynchronizationContext.Current, which would revert
                    // a SetSynchronizationContext call made inside an
                    // async method body.
                    GameHost.InitializeGameThread();

                    // The display first, so the game's OnInitialize can read it
                    this.SyncDisplay(host, window);

                    // Start the game. Startup runs inside the frame loop, as it does on every
                    // platform, so the window stays responsive (it can be moved, resized or
                    // closed) while the game loads, and shows the launch screen until the
                    // game's first frame.
                    host.BeginStartup(new PlatformServices(
                        contentServiceStrategy,
                        graphicsServiceStrategy,
                        audioService,
                        inputServiceStrategy));

                    this._isRunning = true;
                    this.RunGameLoop(host, window, launchScreen);

                    // NOTE: The window goes first, so closing it doesn't look like nothing happened
                    // while the game saves in OnStop
                    SDL.HideWindow(window);

                    StopGame(host);
                }
                finally
                {
                    // Textures still held by the game can be finalized later, with no device
                    this._graphicsService?.Shutdown();

                    SDL.ReleaseWindowFromGPUDevice(gpuDevice, window);
                    SDL.DestroyGPUDevice(gpuDevice);
                }
            }
            finally
            {
                this.SaveWindowState(window);
                SDL.DestroyWindow(window);
                this.NativeWindow = nint.Zero;
            }
        }
        finally
        {
            // Quitting destroys the music's device streams, so first let any acquire still on
            // a track's background queue finish and hand its stream back
            MusicStrategy.DisposeAndDrainAll(TimeSpan.FromSeconds(2));

            // And the sounds' stream, which their audio thread is writing to
            this._audioService?.Shutdown();

            SDL.Quit();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks the game to stop, as closing its window does, such as for a Quit button: the frame loop
    /// ends, and <see cref="RunAsync"/> stops the game and disposes it, then completes. Any thread
    /// may ask.
    /// </summary>
    public void Stop()
    {
        this._isStopRequested = true;
    }

    // NOTE: Any thread may ask, even before the loop starts
    private volatile bool _isStopRequested;

    /// <summary>
    /// Stops the game and disposes it, once the frame loop has ended, pumping the game thread
    /// while it stops (up to a safety cap) so its continuations run.
    /// </summary>
    private static void StopGame(
        GameHost host)
    {
        var stopTask = host.StopAsync();

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
                Trace.Write(0x3c0e7a51, "[DesktopGameEngine] Stop: pump exceeded cap; abandoning wait");
                break;
            }

            Thread.Sleep(1);
        }

        if (stopTask.IsFaulted)
        {
            Trace.Write(0x3c0e7a52, "[DesktopGameEngine] Stop: game teardown faulted", stopTask.Exception);
        }

        host.Game.Dispose();
    }

    /// <summary>
    /// Sizes the (hidden) window from the options and centers it on its display, at the largest
    /// size of its shape that fits, or restores it as the player left it if the options haven't
    /// changed since. Returns the states to put it in once it's shown.
    /// </summary>
    /// <remarks>
    /// Window sizes are in the platform's window coordinates: pixels on Windows and X11, points
    /// on macOS, logical pixels on Wayland. The screen's usable bounds are in the same
    /// coordinates, so fitting needs no conversion. Device-independent pixels convert by the
    /// window's display scale over its pixel density, which is the desktop's scaling (125%
    /// makes 1.25) on Windows and X11, and 1 where window coordinates are already scaled.
    /// </remarks>
    private WindowOpening SizeWindowForDisplay(
        nint window)
    {
        WindowSize windowSize = this._options.WindowSize ?? WindowSize.Default;
        this._windowSettingsKey = windowSize.ToSettingsKey() + ":" + this._options.WindowMode;

        // What the options say, unless the player's window takes over
        var opening = new WindowOpening(
            this._options.WindowMode == WindowMode.Maximized,
            this._options.WindowMode == WindowMode.FullScreen);

        if (Array.IndexOf(Environment.GetCommandLineArgs(), ResetWindowSwitch) >= 0)
        {
            WindowState.Delete();
        }
        else if (this._options.RememberWindow && (WindowState.Load() is { } saved) && (saved.Settings == this._windowSettingsKey))
        {
            // NOTE: Its mode comes back even if its old place is gone
            opening = new WindowOpening(saved.IsMaximized, saved.IsFullScreen);

            if (TryRestoreWindowState(window, saved))
            {
                this._windowState = saved;
                return opening;
            }
        }

        float displayScale = SDL.GetWindowDisplayScale(window);
        float pixelDensity = SDL.GetWindowPixelDensity(window);
        float dipScale = ((displayScale > 0f) && (pixelDensity > 0f)) ? displayScale / pixelDensity : 1f;

        uint display = SDL.GetDisplayForWindow(window);
        if ((display == 0) || !SDL.GetDisplayUsableBounds(display, out var usable) || (usable.W <= 0) || (usable.H <= 0))
        {
            // Nothing to fit to, and nothing else limits the size
            usable = new SDL.Rect { W = (int)(NoScreenSize.X * dipScale), H = (int)(NoScreenSize.Y * dipScale) };
        }

        // NOTE: The title bar has to fit too: a system shortens a window that's too tall for its
        // screen, and only its height, which would spoil the shape
        SDL.GetWindowBordersSize(window, out int borderTop, out int borderLeft, out int borderBottom, out int borderRight);
        float maxWidth = MathF.Min(usable.W * (windowSize.MaxScreenPercent / 100f), usable.W - (borderLeft + borderRight));
        float maxHeight = MathF.Min(usable.H * (windowSize.MaxScreenPercent / 100f), usable.H - (borderTop + borderBottom));
        if (windowSize.MaxSize is { } maxSize)
        {
            maxWidth = MathF.Min(maxWidth, maxSize.X * dipScale);
            maxHeight = MathF.Min(maxHeight, maxSize.Y * dipScale);
        }

        float width = maxWidth;
        float height = maxWidth / windowSize.AspectRatio;

        // Shrink to fit, keeping the shape
        float fit = MathF.Min(1f, MathF.Min(maxWidth / width, maxHeight / height));
        int windowWidth = Math.Max(1, (int)MathF.Round(width * fit));
        int windowHeight = Math.Max(1, (int)MathF.Round(height * fit));

        SDL.SetWindowSize(window, windowWidth, windowHeight);
        SDL.SetWindowPosition(window, (int)SDL.WindowPosCenteredDisplay((int)display), (int)SDL.WindowPosCenteredDisplay((int)display));
        this._fittedAspectRatio = windowSize.AspectRatio;
        return opening;
    }

    // The shape the window was sized to, if it was sized rather than restored
    private float _fittedAspectRatio;

    /// <summary>
    /// Puts the just-shown window back in its shape if the system changed it. macOS shortens a
    /// window too tall for its screen with its title bar, and only its height, and it can't say how
    /// tall the title bar is until the window is shown.
    /// </summary>
    private void KeepWindowShape(
        nint window)
    {
        if (this._fittedAspectRatio <= 0f)
        {
            return;
        }

        SDL.SyncWindow(window);
        SDL.GetWindowSize(window, out int width, out int height);

        int fittedWidth = Math.Min(width, (int)MathF.Round(height * this._fittedAspectRatio));
        int fittedHeight = Math.Min(height, (int)MathF.Round(width / this._fittedAspectRatio));
        if ((Math.Abs(width - fittedWidth) <= 1) && (Math.Abs(height - fittedHeight) <= 1))
        {
            return;
        }

        int display = (int)SDL.GetDisplayForWindow(window);
        SDL.SetWindowSize(window, fittedWidth, fittedHeight);
        SDL.SetWindowPosition(window, (int)SDL.WindowPosCenteredDisplay(display), (int)SDL.WindowPosCenteredDisplay(display));
        SDL.SyncWindow(window);
    }

    // The states to put the window in once it's shown. Maximized as well as full screen is a
    // window that returns to maximized on leaving full screen.
    private readonly record struct WindowOpening(
        bool IsMaximized,
        bool IsFullScreen);

    // The window settings the saved state goes with
    private string _windowSettingsKey = string.Empty;

    /// <summary>
    /// Places the (hidden) window at its saved bounds, rescaled if its display's scaling
    /// changed, if they're still wholly within a screen's usable area.
    /// </summary>
    private static bool TryRestoreWindowState(
        nint window,
        WindowState saved)
    {
        var bounds = new SDL.Rect { X = saved.X, Y = saved.Y, W = saved.Width, H = saved.Height };
        uint display = SDL.GetDisplayForRect(in bounds);
        if ((display == 0) || !SDL.GetDisplayUsableBounds(display, out var usable))
        {
            return false;
        }

        float scale = SDL.GetDisplayContentScale(display);
        if ((scale > 0f) && (MathF.Abs(scale - saved.DisplayScale) > 0.001f))
        {
            bounds.W = (int)MathF.Round(saved.Width * scale / saved.DisplayScale);
            bounds.H = (int)MathF.Round(saved.Height * scale / saved.DisplayScale);
        }

        bool fits = (bounds.X >= usable.X) && (bounds.Y >= usable.Y)
            && (bounds.X + bounds.W <= usable.X + usable.W) && (bounds.Y + bounds.H <= usable.Y + usable.H);
        if (!fits)
        {
            return false;
        }

        SDL.SetWindowSize(window, bounds.W, bounds.H);
        SDL.SetWindowPosition(window, bounds.X, bounds.Y);
        return true;
    }

    /// <summary>
    /// Records the window's bounds while it's in its normal state, since SDL reports the
    /// maximized bounds once it's maximized; and whether it's full screen or maximized.
    /// </summary>
    private void TrackWindowState(
        nint window)
    {
        var flags = SDL.GetWindowFlags(window);
        if ((flags & (SDL.WindowFlags.Hidden | SDL.WindowFlags.Minimized)) != 0)
        {
            return;
        }

        bool isFullScreen = (flags & SDL.WindowFlags.Fullscreen) != 0;
        this._windowState.IsFullScreen = isFullScreen;
        if (!isFullScreen)
        {
            // NOTE: While full screen, this stays as what it returns to
            this._windowState.IsMaximized = (flags & SDL.WindowFlags.Maximized) != 0;
        }

        WindowMode mode = isFullScreen ? WindowMode.FullScreen
            : this._windowState.IsMaximized ? WindowMode.Maximized
            : WindowMode.Windowed;
        if (mode != this._windowModeBox.Value)
        {
            this._windowModeBox = new WindowModeBox(mode);
        }

        if (isFullScreen || this._windowState.IsMaximized)
        {
            return;
        }

        SDL.GetWindowPosition(window, out int x, out int y);
        SDL.GetWindowSize(window, out int width, out int height);
        float scale = SDL.GetDisplayContentScale(SDL.GetDisplayForWindow(window));

        this._windowState.X = x;
        this._windowState.Y = y;
        this._windowState.Width = width;
        this._windowState.Height = height;
        this._windowState.DisplayScale = (scale > 0f) ? scale : 1f;
    }

    /// <summary>
    /// Asks SDL for the mode <see cref="WindowMode"/> was last set to, if any. SDL's events then
    /// report the change, which is immediate on some systems and animated on others.
    /// </summary>
    private void ApplyRequestedWindowMode(
        nint window)
    {
        if (this._requestedWindowMode is null)
        {
            return;
        }

        WindowMode mode = Interlocked.Exchange(ref this._requestedWindowMode, null)!.Value;
        this._modeAfterFullScreen = null;
        if (mode == WindowMode.FullScreen)
        {
            SDL.SetWindowFullscreen(window, true);
        }
        else if ((SDL.GetWindowFlags(window) & SDL.WindowFlags.Fullscreen) != 0)
        {
            // NOTE: It goes back to what it was before full screen, and only then can that change,
            // when SDL reports that it's left
            this._modeAfterFullScreen = new WindowModeBox(mode);
            SDL.SetWindowFullscreen(window, false);
        }
        else
        {
            SetWindowedOrMaximized(window, mode);
        }
    }

    private static void SetWindowedOrMaximized(
        nint window,
        WindowMode mode)
    {
        if (mode == WindowMode.Maximized)
        {
            SDL.MaximizeWindow(window);
        }
        else if ((SDL.GetWindowFlags(window) & SDL.WindowFlags.Maximized) != 0)
        {
            SDL.RestoreWindow(window);
        }
    }

    // What was asked for on leaving full screen, until it has left
    private WindowModeBox? _modeAfterFullScreen;

    private void SaveWindowState(
        nint window)
    {
        this.TrackWindowState(window);
        if ((this._windowState.Width > 0) && (this._windowState.Height > 0))
        {
            this._windowState.Settings = this._windowSettingsKey;
            this._windowState.Save();
        }
    }

    /// <summary>
    /// Forgets the saved window state, so the window next opens as the options say, as the
    /// <c>--reset-window</c> command-line switch does.
    /// </summary>
    public static void ResetWindow()
    {
        WindowState.Delete();
    }

    /// <summary>
    /// Returns the display's DPI per unit of the window's coordinates, the units of
    /// <c>ClientBounds</c>. SDL3 does not expose the panel's true PPI, so this is the OS-suggested
    /// content scale over the window's pixel density, times the 96 DPI desktop baseline: 144 at
    /// 150% on Windows, where window coordinates are pixels, and 96 on a Retina Mac, where they're
    /// points.
    /// </summary>
    private static float GetDisplayDpi(nint window)
    {
        float scale = SDL.GetWindowDisplayScale(window);
        float pixelDensity = SDL.GetWindowPixelDensity(window);
        return ((scale > 0f) && (pixelDensity > 0f)) ? (96f * scale) / pixelDensity : 96f;
    }

    /// <summary>
    /// Queries the window's size, DPI and display refresh rate and, if any has changed since
    /// the last report, reports the display to the game. Safe to call every frame (it is a
    /// no-op when nothing changed), which is how delayed window-state transitions such as
    /// restoring from maximized are picked up.
    /// </summary>
    private void SyncDisplay(
        GameHost host,
        nint window)
    {
        SDL.GetWindowSize(window, out int windowW, out int windowH);
        float dpi = GetDisplayDpi(window);

        // NOTE: Only when it can have changed: reading the display mode allocates
        float refreshHz = this._reportedRefreshHz;
        if (this._isRefreshRateStale)
        {
            this._isRefreshRateStale = false;
            refreshHz = GetDisplayRefreshHz(window);
        }

        if ((windowW == this._reportedWindowW)
            && (windowH == this._reportedWindowH)
            && (dpi == this._reportedDpi)
            && (refreshHz == this._reportedRefreshHz))
        {
            return;
        }

        this._reportedWindowW = windowW;
        this._reportedWindowH = windowH;
        this._reportedDpi = dpi;
        this._reportedRefreshHz = refreshHz;

        var bounds = new RectangleInt(0, 0, windowW, windowH);
        host.SetDisplay(new DisplayInfo(bounds, bounds, dpi, refreshHz));
    }

    /// <summary>
    /// Returns the refresh rate (in hertz) of the display the supplied window
    /// is on, or <c>0</c> if it cannot be determined. A zero result makes the
    /// adaptive continuation pump fall back to its 60 FPS default. Used to tune
    /// the pump against high-refresh (90 / 120 / 144 Hz) panels.
    /// </summary>
    private static float GetDisplayRefreshHz(nint window)
    {
        var displayId = SDL.GetDisplayForWindow(window);
        if (displayId == 0)
        {
            return 0f;
        }

        var mode = SDL.GetCurrentDisplayMode(displayId);
        // RefreshRate is 0 when the platform reports an unspecified rate.
        return mode?.RefreshRate ?? 0f;
    }

    /// <summary>
    /// Resolves the native OS window handle from the SDL window.
    /// Returns <see cref="nint.Zero"/> if the OS is unsupported or
    /// the underlying property is unavailable (e.g. a Wayland
    /// session when SDL is built without Wayland support).
    /// </summary>
    private static nint GetNativeWindowHandle(nint window)
    {
        var props = SDL.GetWindowProperties(window);
        if (props == 0)
        {
            return nint.Zero;
        }

        if (OperatingSystem.IsWindows())
        {
            return SDL.GetPointerProperty(props, SDL.Props.WindowWin32HWNDPointer, nint.Zero);
        }

        if (OperatingSystem.IsMacOS())
        {
            return SDL.GetPointerProperty(props, SDL.Props.WindowCocoaWindowPointer, nint.Zero);
        }

        if (OperatingSystem.IsLinux())
        {
            // Prefer X11 when available; fall back to the Wayland
            // wl_surface pointer. SDL only populates the property
            // matching the active video driver, so the unused branch
            // returns zero.
            var x11Window = SDL.GetNumberProperty(props, SDL.Props.WindowX11WindowNumber, 0);
            if (x11Window != 0)
            {
                return (nint)x11Window;
            }

            return SDL.GetPointerProperty(props, SDL.Props.WindowWaylandSurfacePointer, nint.Zero);
        }

        return nint.Zero;
    }

    private void RunGameLoop(
        GameHost host,
        nint window,
        LaunchScreenStrategy launchScreen)
    {
        this._frameHost = host;
        this._frameWindow = window;
        this._frameLaunchScreen = launchScreen;

        // NOTE: Moving or resizing a window on Windows runs a modal loop inside SDL's event polling,
        // and only an event watch sees the frames it offers meanwhile
        this._eventWatch = this.WatchEvent;
        SDL.AddEventWatch(this._eventWatch, nint.Zero);

        try
        {
            while (this._isRunning && !this._isStopRequested)
            {
                this.ProcessEvents(host, window);
                if (!this._isRunning || this._isStopRequested)
                {
                    break;
                }

                if (!this.IsFrameDue())
                {
                    continue;
                }

                // Input and the frame's time are read after the wait for the swapchain, so they're
                // as fresh as they can be and the time follows the display, not the last frame's cost
                this.AcquireFrame();
                this.ProcessEvents(host, window);
                if (!this._isRunning || this._isStopRequested)
                {
                    break;
                }

                this.RunFrame(host, window, launchScreen);
            }
        }
        finally
        {
            SDL.RemoveEventWatch(this._eventWatch, nint.Zero);

            // A frame acquired and then not run still has to be given back
            if (this._isFrameAcquired)
            {
                this._isFrameAcquired = false;
                this._graphicsService!.SubmitFrame();
            }
        }
    }

    private GameHost? _frameHost;
    private nint _frameWindow;
    private LaunchScreenStrategy? _frameLaunchScreen;
    private SDL.EventFilter? _eventWatch;

    // Whether the game has drawn a frame: the launch screen shows until it has, which is after it
    // has started (a game starts, then loads its first scene)
    private bool _hasDrawn;
    private bool _isFrameAcquired;
    private bool _hasSwapchain;
    private bool _isInFrame;

    // Runs on the main thread, inside SDL's event polling
    private bool WatchEvent(
        nint userdata,
        ref SDL.Event ev)
    {
        // NOTE: Nothing may cross back into native code
        try
        {
            if (((SDL.EventType)ev.Type == SDL.EventType.WindowExposed) && (ev.Window.Data1 == 1) && !this._isInFrame
                && (this._frameHost is { } host) && (this._frameLaunchScreen is { } launchScreen))
            {
                this._isInFrame = true;
                try
                {
                    this.AcquireFrame();
                    this.RunFrame(host, this._frameWindow, launchScreen);
                }
                finally
                {
                    this._isInFrame = false;
                }
            }
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x56a3d1f8, e);
        }

        return true;
    }

    // When the next frame is due, for a game with a preferred frame rate
    private long _nextFrameDue;

    // A frame is due this early, so that 60 on a 60 Hz display, whose frames wait for the display
    // anyway, is every frame, and a little timing noise doesn't drop one
    private static readonly long FrameDueTolerance = Stopwatch.Frequency * 4 / 1000;

    // The longest to wait between looking at events, so the window stays responsive
    private const ulong MaxFrameWaitNanoseconds = 2_000_000;

    /// <summary>
    /// Whether the game's preferred frame rate allows a frame now, waiting a little if it doesn't.
    /// </summary>
    /// <remarks>
    /// NOTE: The wait comes before acquiring a frame, since one acquired has to be presented. Each
    /// frame is due an interval after the last was due, not after it started, so the tolerance
    /// doesn't add up to a higher rate where nothing else paces the frames.
    /// </remarks>
    private bool IsFrameDue()
    {
        if (this._graphicsService!.PreferredFrameRate is not int rate)
        {
            return true;
        }

        long interval = Stopwatch.Frequency / rate;
        long now = Stopwatch.GetTimestamp();
        long remaining = this._nextFrameDue - FrameDueTolerance - now;
        if (remaining > 0)
        {
            ulong nanoseconds = (ulong)(remaining * 1_000_000_000.0 / Stopwatch.Frequency);
            SDL.DelayPrecise(Math.Min(nanoseconds, MaxFrameWaitNanoseconds));
            return false;
        }

        // Running behind, or the first frame: from now, rather than racing to catch up
        this._nextFrameDue = ((now - this._nextFrameDue) > interval) ? now + interval : this._nextFrameDue + interval;
        return true;
    }

    private void AcquireFrame()
    {
        if (!this._isFrameAcquired)
        {
            this._isFrameAcquired = true;
            this._hasSwapchain = this._graphicsService!.AcquireSwapchain();
        }
    }

    private void ProcessEvents(
        GameHost host,
        nint window)
    {
        long start = Environment.TickCount64;

        this.ApplyRequestedWindowMode(window);

        // Process SDL events
        while (SDL.PollEvent(out SDL.Event ev))
        {
            switch ((SDL.EventType)ev.Type)
            {
                case SDL.EventType.LowMemory:
                    this._host?.NotifyLowMemory();
                    break;

                case SDL.EventType.Quit:
                case SDL.EventType.WindowCloseRequested:
                    this._isRunning = false;
                    break;

                // WindowResized fires when the logical window size changes.
                // WindowPixelSizeChanged covers DPI/drawable changes, and
                // WindowMaximized/WindowRestored cover state transitions
                // (e.g. restoring from maximized on Windows) that change the
                // client size without always emitting a WindowResized event.
                // Handle them all so the cached window bounds stay in sync.
                case SDL.EventType.WindowResized:
                case SDL.EventType.WindowPixelSizeChanged:
                case SDL.EventType.WindowMaximized:
                case SDL.EventType.WindowRestored:
                case SDL.EventType.WindowDisplayChanged:
                    this._isRefreshRateStale = true;
                    this.SyncDisplay(host, window);
                    this.TrackWindowState(window);
                    this.UpdateWindowHidden(window);
                    break;

                // By the game's request, or the player's by the system's own controls
                case SDL.EventType.WindowEnterFullscreen:
                case SDL.EventType.WindowLeaveFullscreen:
                    if (((SDL.EventType)ev.Type == SDL.EventType.WindowLeaveFullscreen) && (this._modeAfterFullScreen is { } after))
                    {
                        this._modeAfterFullScreen = null;
                        SetWindowedOrMaximized(window, after.Value);
                    }

                    this._isRefreshRateStale = true;
                    this.SyncDisplay(host, window);
                    this.TrackWindowState(window);
                    this.UpdateWindowHidden(window);
                    break;

                case SDL.EventType.WindowMinimized:
                case SDL.EventType.WindowHidden:
                case SDL.EventType.WindowShown:
                    this.UpdateWindowHidden(window);
                    break;

                case SDL.EventType.WindowMoved:
                    this.TrackWindowState(window);
                    break;

                case SDL.EventType.DisplayCurrentModeChanged:
                    this._isRefreshRateStale = true;
                    break;

                // An unfocused window is inactive, and its music plays on; music stops when
                // it's hidden (UpdateWindowHidden)
                case SDL.EventType.WindowFocusLost:
                    SafeAsync.Invoke(0x3b7c1e04, host.PauseAsync());
                    break;

                case SDL.EventType.WindowFocusGained:
                    SafeAsync.Invoke(0x9d20af51, host.ResumeAsync());
                    break;

                default:
                    // Forward input events
                    this._inputService?.ProcessEvent(ref ev);
                    break;
            }
        }

        // Holding the title bar or a menu stops events here with no frames, and that isn't game time
        if ((Environment.TickCount64 - start) > MaxEventPumpMilliseconds)
        {
            host.ResetFrameClock();
        }
    }

    private const long MaxEventPumpMilliseconds = 100;

    private void RunFrame(
        GameHost host,
        nint window,
        LaunchScreenStrategy launchScreen)
    {
        this._isFrameAcquired = false;

        // Poll the display every frame. Some window-state transitions
        // (e.g. restoring from maximized on Windows) fire their event before
        // SDL updates the queryable window size, so the event handler reads a
        // stale value. Polling here reports the corrected size on the next
        // frame without requiring the user to resize the window again.
        this.SyncDisplay(host, window);

        // The frame's time, on SDL's monotonic clock: the vsync-paced wait has just ended
        var frameTimestamp = TimeSpan.FromTicks((long)(SDL.GetTicksNS() / 100));

        if (this._hasSwapchain)
        {
            bool drew = host.Tick(frameTimestamp, canDraw: true);

            // The launch screen: until the game has drawn, the window shows it rather than
            // whatever the swapchain held; then it's drawn over the game's frames until it has
            // faded out
            if (drew)
            {
                launchScreen.DrawOverGame();
            }
            else if (!this._hasDrawn)
            {
                launchScreen.Draw();
            }

            this._graphicsService!.SubmitFrame();

            if (drew && !this._hasDrawn)
            {
                host.NotifyFirstFramePresented();
            }

            this._hasDrawn |= drew;
        }
        else
        {
            // No swapchain available (too many frames in flight, or
            // window minimized/occluded). Still run game logic so
            // input and timers stay responsive, but skip rendering.
            host.Tick(frameTimestamp, canDraw: false);

            // NOTE: Nothing paces this loop while there's no swapchain, and a hidden window
            // won't have one for a long while, so it shouldn't spin
            Thread.Sleep(this._isWindowHidden ? HiddenFrameMilliseconds : 1);
        }
    }

    // The pace of frames while the window is hidden, which only run continuations
    private const int HiddenFrameMilliseconds = 16;

    private bool _isWindowHidden;

    // Stops and restarts music as the window is hidden and shown
    private void UpdateWindowHidden(
        nint window)
    {
        bool isHidden = (SDL.GetWindowFlags(window) & (SDL.WindowFlags.Minimized | SDL.WindowFlags.Hidden)) != 0;
        if (isHidden == this._isWindowHidden)
        {
            return;
        }

        this._isWindowHidden = isHidden;
        if (isHidden)
        {
            this._audioService?.OnSystemSuspend();
        }
        else
        {
            this._audioService?.OnSystemResume();
        }
    }
}
