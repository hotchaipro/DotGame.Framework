// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using DotGame.Framework.Content;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Content;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;
using DotGame.Framework.Threading;
using DotGame.Framework.WebGL.Audio;
using DotGame.Framework.WebGL.Content;
using DotGame.Framework.WebGL.Graphics;
using DotGame.Framework.WebGL.Input;

namespace DotGame.Framework.WebGL;

/// <summary>Runs a game in a browser, drawing into a canvas on the page.</summary>
[SupportedOSPlatform("browser")]
public sealed class WebGLGameEngine
{
    private Game _game = null!;
    private GameHost _host = null!;
    private readonly string _canvasElement;

    // This game's instance ID in GameBindings, and whether a startup pump is requested and
    // hasn't run yet (see RequestStartupPump)
    private int _instanceId;
    private bool _isStartupPumpRequested;
    private bool _hasDrawnFirstFrame;

    // The window background last sent to the page
    private Color? _windowBackground;

    // Completes when the game has started (or faults if startup fails), so the task returned
    // by Run() completes after the game's OnStart
    private readonly TaskCompletionSource _runningTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        /// The id of the canvas to draw into. The default is <c>"canvas"</c>, the one the build adds
        /// to the page; a game embedded in a larger page can use its own.
        /// </summary>
        public string CanvasElementId { get; set; } = "canvas";

    }

    // The frame rate last passed to the page, where 0 is the display's own
    private int _appliedFrameRate;

    private int _multisampleCount = 1;

    /// <summary>Makes an engine with these options, or the defaults; it does nothing until <see cref="RunAsync"/>.</summary>
    public WebGLGameEngine(
        Options? options = null)
    {
        options ??= new Options();

        this._canvasElement = options.CanvasElementId;
    }

    /// <summary>Runs the game. The task completes when the game has started, as a browser's page keeps running after <c>Main</c> returns.</summary>
    public Task RunAsync(
        Game game)
    {
        ArgumentNullException.ThrowIfNull(game);

        this._multisampleCount = game.PreferredMultisampleCount;

        if (this._game is not null)
        {
            throw new InvalidOperationException("This engine instance is already running a game.");
        }

        this._game = game;
        this._host = new GameHost(game);

        // The page's launch screen, until the game's first frame is on screen
        this._host.ShowLaunchScreen(new LaunchScreenStrategy());

        Trace.Write(0x83848a90, "Initializing engine...");

        // Establish the game thread identity and synchronization
        // context OUTSIDE of an async method so that
        // AsyncTaskMethodBuilder.Start does not revert it.
        GameHost.InitializeGameThread();

        this.StartUp();

        // The returned task completes once the game has started (or faults if
        // startup fails). Startup advances with each animation frame (and
        // startup pump) RunFrame runs.
        return this._runningTcs.Task;
    }

    /// <summary>
    /// Starts the game without blocking the browser main thread: creates the
    /// services, reports the canvas, and begins the game's startup, which each
    /// <see cref="RunFrame"/> then advances.
    /// </summary>
    private void StartUp()
    {
        GameThread.VerifyCurrent(0xcaeda43b);

        try
        {
            // Create the required game services
            this.GraphicsService = new GraphicsServiceStrategy(this, this._canvasElement, this._multisampleCount);
            var graphicsServiceStrategy = new Strategy<IGraphicsServiceStrategy>(this.GraphicsService);
            var audioService = new Strategy<IAudioServiceStrategy>(new AudioServiceStrategy());
            var inputService = new Strategy<IInputServiceStrategy>(new InputServiceStrategy());

            // Create the content service. The content root is a build-time
            // fact carried by the manifest; the browser resolves it relative
            // to the page base URL, so no additional runtime root is prepended.
            var contentManifest = ContentManifest.Registry.BuildSnapshot();
            this.ContentService = new ContentServiceStrategy(this, contentManifest.BasePath, contentManifest);
            var contentServiceStrategy = new Strategy<IContentServiceStrategy>(this.ContentService);

            // Startup milestones also become performance marks for the page's startup report
            GameHost.StartupTimelineSink = PageStartupTimeline.MarkStartup;

            // Register with the JS animation-frame loop and report the initial canvas
            // (initialize's OnCanvasResized runs before it returns), so the game's
            // OnInitialize can read ClientBounds.
            this._instanceId = GameBindings.StartGameLoop(this._canvasElement, this._host, this);
            GameBindings.Initialize(this._canvasElement);

            // While starting, pump whenever work is posted, not only on animation frames,
            // which a hidden page doesn't get; see RequestStartupPump
            GameHost.WorkPosted = this.RequestStartupPump;

            // Start the game. We must NOT wait here — this thread is the pump:
            // RunFrame runs the continuations that advance startup.
            this._host.BeginStartup(new PlatformServices(
                contentServiceStrategy,
                graphicsServiceStrategy,
                audioService,
                inputService));

            // The first pump
            this.RequestStartupPump();
        }
        catch (Exception e)
        {
            // Synchronous startup runs before the frame loop begins pumping, so
            // a throw here (most commonly WebGL context creation failing — the
            // typical iOS Safari splash-hang cause) would only surface as a
            // rejected Main() task and the leaner JS-side error dialog. Route it
            // through the managed fatal path so the full crash report (managed
            // stack + trace dump) is shown. The "Crash" module is already bound
            // (main.js registers it before runMain -> Main -> Run), so the dialog
            // renders reliably.
            this._runningTcs.TrySetException(e);
            GameHost.Terminate(0xe4f1b26d, e);
        }
    }

    /// <summary>
    /// Asks for <see cref="RunFrame"/> to run soon, without waiting for an animation frame.
    /// Used while starting, when a hidden page (a game opened in a background tab) gets no
    /// animation frames: without it, the game would load nothing until it was shown. Requests
    /// are coalesced until the pump runs.
    /// </summary>
    private void RequestStartupPump()
    {
        if (this._isStartupPumpRequested)
        {
            return;
        }

        this._isStartupPumpRequested = true;
        GameBindings.RequestStartupPump(this._instanceId);
    }

    private static int ToByte(float component) => (int)MathF.Round(Math.Clamp(component, 0f, 1f) * 255f);

    /// <summary>
    /// Called once per browser animation frame by
    /// <see cref="GameBindings.RunFrame"/>, and while starting, by the startup pump
    /// (see RequestStartupPump): runs the game's frame, which while starting only advances
    /// startup.
    /// </summary>
    /// <param name="frameTimestamp">The animation frame's timestamp, or for a startup pump,
    /// the time it runs, both on the page's clock (<c>performance.now()</c>).</param>
    internal void RunFrame(
        TimeSpan frameTimestamp)
    {
        // The .NET WASM runtime may reset SynchronizationContext.Current between
        // JS-to-managed interop calls; re-establish it before any pump.
        GameHost.EnsureGameThreadContext();

        // Whatever asked for a startup pump, this frame is it
        this._isStartupPumpRequested = false;

        int frameRate = this.GraphicsService.PreferredFrameRate ?? 0;
        if (frameRate != this._appliedFrameRate)
        {
            this._appliedFrameRate = frameRate;
            GameBindings.SetPreferredFrameRate(this._canvasElement, frameRate);
        }

        // Hidden after the game has started: run the continuations (an asynchronous OnPause), with
        // no frame to run
        if (this._isPageHidden && this._host.IsStarted)
        {
            GameHost.DrainContinuations();
            this.GraphicsService.EndFrame();
            return;
        }

        bool wasStarted = this._host.IsStarted;

        // NOTE: Nothing can be drawn while a lost context's resources are being recreated
        bool drew = this._host.Tick(frameTimestamp, canDraw: !this.GraphicsService.IsDeviceLost);

        // The frame's drawing, in one call
        this.GraphicsService.EndFrame();

        if (drew && !this._hasDrawnFirstFrame)
        {
            this._hasDrawnFirstFrame = true;
            this._host.NotifyFirstFramePresented();
        }

        // The page shows the color the game clears its window to wherever the canvas doesn't
        // reach (the side safe areas Chrome on iOS keeps the page out of in landscape, and
        // overscroll), as the other platforms' windows show it in their safe areas
        if (drew && (this._host.WindowBackground is { } background) && (background != this._windowBackground))
        {
            this._windowBackground = background;
            GameBindings.SetWindowBackground(
                (ToByte(background.R) << 16) | (ToByte(background.G) << 8) | ToByte(background.B));
        }

        if (!wasStarted && this._host.IsStarted)
        {
            // From here, animation frames drive the game
            GameHost.WorkPosted = this._isPageHidden ? this.RequestStartupPump : null;

            this._runningTcs.TrySetResult();
        }
        else if (this._host.StartupFault is { } fault)
        {
            this._runningTcs.TrySetException(fault);
        }
    }

    private bool _isPageHidden;

    /// <summary>
    /// Notes that the page was hidden or shown. A hidden page gets no animation frames, so work
    /// posted while it's hidden is pumped on demand, which is what lets an asynchronous
    /// <c>OnPause</c> save before a mobile browser discards the page.
    /// </summary>
    internal void SetPageHidden(
        bool isHidden)
    {
        this._isPageHidden = isHidden;

        if (this._host.IsStarted)
        {
            GameHost.WorkPosted = isHidden ? this.RequestStartupPump : null;
        }
    }
}
