// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotGame.Framework.Input;
using DotGame.Framework.Platform.Common;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Threading;
using DotGame.Framework.WebGL.Graphics;

namespace DotGame.Framework.WebGL;

[SupportedOSPlatform("browser")]
internal sealed partial class GameBindings
{
    private const string ModuleName = "game.js";

    internal sealed record GameInstance(
        GameHost Host,
        WebGLGameEngine Engine,
        GraphicsServiceStrategy GraphicsService,
        string CanvasId)
    {
        public Game Game => this.Host.Game;
    }

    private static readonly List<GameInstance> _instances = new();

    internal static GameInstance GetInstance(int instanceId)
    {
        return _instances[instanceId];
    }

    /// <returns>The game's instance ID, which identifies it to JavaScript.</returns>
    internal static int StartGameLoop(
        string canvasId,
        GameHost host,
        WebGLGameEngine engine)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(engine);

        var instance = new GameInstance(host, engine, engine.GraphicsService, canvasId);
        int instanceId = _instances.Count;
        _instances.Add(instance);

        StartGameLoop(canvasId, instanceId);

        return instanceId;
    }

    /// <summary>
    /// Asks JavaScript to call <see cref="RunFrame"/> for the instance soon, without
    /// waiting for an animation frame (see requestStartupPump in game.js).
    /// </summary>
    [JSImport("gamePlatform.requestStartupPump", ModuleName)]
    internal static partial void RequestStartupPump(int instanceId);

    /// <summary>
    /// Tells the page the canvas's frame rate limit, where 0 is none.
    /// </summary>
    [JSImport("gamePlatform.setPreferredFrameRate", ModuleName)]
    internal static partial void SetPreferredFrameRate(string canvasId, int preferredFrameRate);

    /// <summary>
    /// Starts the game loop for the specified canvas.
    /// This method calls the JavaScript startGameLoop function.
    /// </summary>
    [JSImport("gamePlatform.startGameLoop", ModuleName)]
    private static partial void StartGameLoop(string canvasId, int instanceId);

    /// <summary>
    /// Tells the page the game has drawn its first frame, so it fades out its launch screen
    /// (see firstFrameDrawn in game.js).
    /// </summary>
    [JSImport("gamePlatform.firstFrameDrawn", ModuleName)]
    internal static partial void FirstFrameDrawn(int fadeMilliseconds, string fadeEase);

    /// <summary>
    /// Tells the page the color around and behind the game (0xRRGGBB), which it shows once the
    /// launch screen is gone wherever the canvas doesn't reach (see setWindowBackground in
    /// game.js).
    /// </summary>
    [JSImport("gamePlatform.setWindowBackground", ModuleName)]
    internal static partial void SetWindowBackground(int rgb);

    [JSImport("gamePlatform.initialize", ModuleName)]
    internal static partial void Initialize(string elementId);

    /// <summary>
    /// Called once per frame from JavaScript via requestAnimationFrame (paced
    /// at the display refresh rate, typically 60 FPS).
    /// This static method delegates to the engine, whose frame advances startup
    /// until the game has started.
    /// </summary>
    /// <param name="instanceId">The game's instance ID.</param>
    /// <param name="timestamp">The frame's timestamp on the page's clock
    /// (<c>performance.now()</c>), in milliseconds.</param>
    [JSExport]
    public static void RunFrame(
        int instanceId,
        double timestamp)
    {
        try
        {
            var instance = _instances[instanceId];

            // The engine re-establishes the game synchronization context (which the
            // WASM runtime may reset across interop calls) before pumping.
            instance.Engine.RunFrame(TimeSpan.FromMilliseconds(timestamp));
        }
        catch (Exception e)
        {
            // Route every escape to the fatal path so the crash dialog is
            // shown. Terminate reports the fault and then unwinds, which
            // deliberately stops the loop: game.js does not schedule the next
            // frame once this callback throws. That is what keeps the dialog
            // rendered once and interactive, instead of being rebuilt every
            // frame by a loop that keeps re-entering the same fault. The
            // dialog removes the splash itself, so nothing is left covering it.
            GameHost.Terminate(0x7b2e9f14, e);
        }
    }

    /// <summary>
    /// Called when the browser window is activated (gains focus).
    /// This method can be used to resume game logic and rendering.
    /// </summary>
    // <paramref name="control"/> is a GamepadButton or a GamepadAxis, as the kind says
    [JSExport]
    public static void OnGamepadEvent(
        int instanceId,
        int kind,
        int index,
        int control,
        double value)
    {
        try
        {
            var gamepadKind = (GamepadEventKind)kind;
            GetInstance(instanceId).Host.QueueGamepadEvent(new GamepadEvent(
                gamepadKind,
                index,
                (gamepadKind == GamepadEventKind.AxisMoved) ? default : (GamepadButton)control,
                (gamepadKind == GamepadEventKind.AxisMoved) ? (GamepadAxis)control : default,
                (float)value));
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x3e91a0d7, e);
        }
    }

    [JSExport]
    public static void OnWindowActivated()
    {
        try
        {
            GameHost.EnsureGameThreadContext();

            foreach (var instance in _instances)
            {
                // Not awaited: this is a JS interop entry point. The
                // catch below only sees synchronous throws, so
                // SafeAsync covers a fault raised after the first await.
                SafeAsync.Invoke(0x6ad3f819, instance.Host.ResumeAsync());
            }
        }
        catch (Exception e)
        {
            // Invoked directly from the browser 'focus' event handler; a throw
            // here would escape into JS with no managed dialog. Terminate loudly.
            GameHost.Terminate(0x6d4a1c83, e);
        }
    }

    /// <summary>
    /// Called when the browser window is deactivated (loses focus).
    /// This method can be used to pause game logic and rendering.
    /// </summary>
    [JSExport]
    public static void OnWindowDeactivated()
    {
        try
        {
            GameHost.EnsureGameThreadContext();

            foreach (var instance in _instances)
            {
                // Not awaited: this is a JS interop entry point. The
                // catch below only sees synchronous throws, so
                // SafeAsync covers a fault raised after the first await.
                SafeAsync.Invoke(0xb04c7e25, instance.Host.PauseAsync());
            }
        }
        catch (Exception e)
        {
            // Invoked directly from the browser 'blur' event handler; a throw
            // here would escape into JS with no managed dialog. Terminate loudly.
            GameHost.Terminate(0x5f8b3e27, e);
        }
    }

    /// <summary>
    /// Called when the page is hidden or shown, which ends and restarts its animation frames.
    /// </summary>
    [JSExport]
    public static void OnPageVisibilityChanged(
        bool isHidden)
    {
        try
        {
            GameHost.EnsureGameThreadContext();

            foreach (var instance in _instances)
            {
                instance.Engine.SetPageHidden(isHidden);
            }
        }
        catch (Exception e)
        {
            GameHost.Terminate(0x3e7a91c4, e);
        }
    }

    [JSExport]
    public static void OnCanvasResized(
        int instanceId,
        int width,
        int height,
        int safeX,
        int safeY,
        int safeWidth,
        int safeHeight,
        double devicePixelRatio)
    {
        try
        {
            GameHost.EnsureGameThreadContext();

            // Browsers do not expose true panel DPI; the standard convention is
            // 96 CSS DPI × devicePixelRatio for the effective physical density.
            var displayDpi = (float)(96.0 * (devicePixelRatio > 0 ? devicePixelRatio : 1));

            // NOTE: Browsers don't report the display's refresh rate; animation frames
            // usually come at 60 Hz, which the continuation budget assumes for 0
            var host = _instances[instanceId].Host;
            host.SetDisplay(new DisplayInfo(
                ClientBounds: new RectangleInt(0, 0, width, height),
                SafeAreaBounds: new RectangleInt(safeX, safeY, safeWidth, safeHeight),
                Dpi: displayDpi,
                RefreshRateHz: 0));
        }
        catch (Exception e)
        {
            // Invoked from ResizeObserver / visualViewport handlers in game.js;
            // a throw here would escape into JS with no managed dialog.
            GameHost.Terminate(0x4a9c7d61, e);
        }
    }

    [JSExport]
    public static void OnContextLost(int instanceId)
    {
        try
        {
            GameHost.EnsureGameThreadContext();

            _instances[instanceId].GraphicsService.HandleContextLost();
        }
        catch (Exception e)
        {
            // Invoked from the canvas 'webglcontextlost' handler; a throw here
            // would escape into JS with no managed dialog. Terminate loudly.
            GameHost.Terminate(0x3e6f2b58, e);
        }
    }

    [JSExport]
    public static void OnContextRestored(int instanceId)
    {
        try
        {
            GameHost.EnsureGameThreadContext();

            var instance = _instances[instanceId];
            GameThread.Post(async () =>
            {
                await instance.GraphicsService.HandleContextRestored();
                instance.Host.NotifyDeviceReset();
            });
        }
        catch (Exception e)
        {
            // Guards the synchronous dispatch (EnsureContext / instance lookup /
            // queueing); the posted async body is itself wrapped by
            // GameThread.Post. Invoked from the 'webglcontextrestored' handler.
            GameHost.Terminate(0x2c1d8a4f, e);
        }
    }

    /// <summary>
    /// Reports a JavaScript error or unhandled promise rejection that reached the
    /// browser's global error handlers and then terminates the process. Unhandled
    /// browser failures are never silently swallowed; benign, expected exceptions
    /// (for example an interrupted <c>play()</c>) are caught explicitly at their
    /// JavaScript call sites instead.
    /// </summary>
    [JSExport]
    public static void ReportUnhandledError(
        string source,
        string name,
        string message,
        string stack)
    {
        GameHost.Terminate(
            0x8f3ac2d1,
            new UnhandledBrowserException(source, name, message, stack));
    }

    /// <summary>
    /// Handles a fault in the audio engine's JavaScript as any audio failure: reported, and the
    /// game runs on silently.
    /// </summary>
    [JSExport]
    public static void ReportAudioFailure(
        string message,
        string stack)
    {
        GameHost.ReportAudioFailure(
            0xa4e17c39,
            new UnhandledBrowserException("audio fault", "AudioEngineError", message, stack));
    }
}
