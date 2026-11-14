// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using DotGame.Framework.Input;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;

namespace DotGame.Framework.Testing;

/// <summary>
/// Runs a real <see cref="Game"/> on the test platform, frame by frame, as a platform's run loop
/// would: <see cref="Start"/>, <see cref="SetDisplay"/>, then <see cref="Tick"/>, with input
/// queued between frames. Everything here runs on the game thread (<see cref="OnGameThread"/>).
/// </summary>
public sealed class TestHost
{
    private readonly GameHost _host;

    public TestHost(
        Game game,
        FakeContent? content = null)
    {
        ArgumentNullException.ThrowIfNull(game);

        this._host = new GameHost(game);
        this.Content = content ?? new FakeContent();
    }

    /// <summary>The game's content.</summary>
    public FakeContent Content { get; }

    /// <summary>The game's graphics device.</summary>
    public TestGraphics Graphics { get; } = new TestGraphics();

    /// <summary>The game's audio device.</summary>
    public FakeAudio Audio { get; } = new FakeAudio();

    public bool IsStarted => this._host.IsStarted;

    /// <summary>
    /// Starts the game, and returns once it has, running the work its startup awaits. Fails,
    /// rather than hanging, if it never does, and with the game's own exception if it fails.
    /// </summary>
    public void Start()
    {
        this._host.BeginStartup(new PlatformServices(
            this.Content.ToStrategy(),
            this.Graphics.ToStrategy(),
            this.Audio.ToStrategy(),
            new Strategy<IInputServiceStrategy>(new NoInput())));

        PumpUntil(() => this._host.IsStarted || (this._host.StartupFault is not null));
        if (this._host.StartupFault is { } fault)
        {
            ExceptionDispatchInfo.Capture(fault).Throw();
        }
    }

    /// <summary>
    /// Tells the game about its display.
    /// </summary>
    /// <param name="clientBounds">The window's client area, in pixels.</param>
    /// <param name="safeAreaBounds">The part of it clear of system bars and cutouts.</param>
    /// <param name="dpi">The display's density, in dots per inch.</param>
    /// <param name="refreshRateHz">The display's refresh rate.</param>
    public void SetDisplay(
        RectangleInt clientBounds,
        RectangleInt safeAreaBounds,
        float dpi = 96,
        double refreshRateHz = 60)
    {
        this._host.SetDisplay(new DisplayInfo(clientBounds, safeAreaBounds, dpi, refreshRateHz));
    }

    /// <summary>
    /// Runs one frame at <paramref name="frameTimestamp"/>.
    /// </summary>
    /// <returns>Whether the game drew.</returns>
    public bool Tick(
        TimeSpan frameTimestamp,
        bool canDraw = true)
    {
        return this._host.Tick(frameTimestamp, canDraw);
    }

    /// <summary>
    /// The game goes inactive, as when the app is suspended, and the work that starts runs.
    /// </summary>
    public void Pause()
    {
        _ = this._host.PauseAsync();
        GameHost.DrainContinuations();
    }

    /// <summary>
    /// The game comes back, and the work that starts runs.
    /// </summary>
    public void Resume()
    {
        _ = this._host.ResumeAsync();
        GameHost.DrainContinuations();
    }

    /// <summary>
    /// Queues a pointer event for the next frame.
    /// </summary>
    public void QueuePointer(
        PointerEventKind kind,
        Vector2 position,
        int pointerId = 1,
        PointerDevice device = PointerDevice.Touch)
    {
        this._host.QueuePointerEvent(new PointerEventData
        {
            State = kind,
            PointerId = pointerId,
            PointerType = device,
            Position = position,
        });
    }

    /// <summary>
    /// Queues a key event for the next frame.
    /// </summary>
    public void QueueKey(
        KeyEventKind kind,
        Key key)
    {
        this._host.QueueKeyEvent(new KeyEventData
        {
            State = kind,
            Key = key,
        });
    }

    /// <summary>
    /// Runs <paramref name="test"/> on a new game thread, as a platform's run loop would. The game
    /// thread is one per process, so tests that use it must not run in parallel.
    /// </summary>
    public static void OnGameThread(
        Action test)
    {
        ArgumentNullException.ThrowIfNull(test);

        RunOnNewThread(() =>
        {
            GameHost.InitializeGameThread();
            try
            {
                test();
            }
            finally
            {
                GameHost.ShutdownGameThread();
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a new thread that isn't the game thread, and waits for it.
    /// </summary>
    public static void OnOtherThread(
        Action body)
    {
        ArgumentNullException.ThrowIfNull(body);

        RunOnNewThread(body);
    }

    /// <summary>
    /// Runs the game thread's posted work until <paramref name="condition"/> holds. Fails, rather
    /// than hanging, if it never does.
    /// </summary>
    public static void PumpUntil(
        Func<bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("The condition never held.");
            }

            GameHost.DrainContinuations();
            Thread.Yield();
        }
    }

    /// <summary>
    /// Runs the game thread's posted work.
    /// </summary>
    public static void DrainContinuations()
    {
        GameHost.DrainContinuations();
    }

    private static void RunOnNewThread(
        Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });

        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class NoInput : IInputServiceStrategy
    {
        public Task OnInitialize() => Task.CompletedTask;

        public ITextInputHandle CreateTextInputHandle(ITextInputSink sink) => throw new NotSupportedException();
    }
}
