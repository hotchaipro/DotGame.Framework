// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;
using DotGame.Framework.Threading;
using DotGame.Framework.Time;

namespace DotGame.Framework;

/// <summary>
/// The base class of a game: its services, the display it draws to, and the hooks it overrides
/// to load, update, draw, and respond to the app's lifecycle.
/// </summary>
/// <remarks>
/// A platform runs the game through a <see cref="GameHost"/>, which owns the sequencing: when
/// each hook runs, what a pause or stop during startup does, and how frames are timed. This
/// class holds only what a game sees, and the internal members the host calls.
/// </remarks>
public abstract class Game : IDisposable
{
    private bool _isActive;
    private bool _isDisposed;
    private TaskCompletionSource _firstFrameDrawn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RectangleInt _clientBounds;
    private RectangleInt _safeAreaBounds;

    private readonly GameSynchronizationContext _synchronizationContext;

    /// <summary>Makes the game. Its services don't exist until <see cref="OnInitialize"/>.</summary>
    protected Game()
    {
        // Set the synchronization context for async/await
        this._synchronizationContext = GameSynchronizationContext.GameThread;
    }

    /// <summary>Loads content by key.</summary>
    public ContentService Content { get; private set; } = null!;

    /// <summary>Draws: sprite batches, textures, render targets and the scissor.</summary>
    public GraphicsService Graphics { get; private set; } = null!;

    /// <summary>Plays sound and music.</summary>
    public AudioService Audio { get; private set; } = null!;

    /// <summary>Reads the keyboard and gamepads; pointers arrive as events.</summary>
    public InputService Input { get; private set; } = null!;

    /// <summary>The game's clock: game time and delays.</summary>
    public TimeService Time { get; private set; } = null!;

    /// <summary>The statistics overlay and other diagnostics.</summary>
    public DiagnosticsService Diagnostics { get; private set; } = null!;

    /// <summary>The window's drawable area, in pixels.</summary>
    public RectangleInt ClientBounds => this._clientBounds;

    /// <summary>The part of <see cref="ClientBounds"/> clear of notches, rounded corners and system bars.</summary>
    public RectangleInt SafeAreaBounds => this._safeAreaBounds;

    /// <summary>
    /// Effective display density in physical dots-per-inch, as reported by
    /// the host platform. Used by gesture recognizers and other UI code that
    /// needs thresholds expressed in physical units (e.g. millimeters).
    /// Defaults to 96 DPI (the historical desktop baseline) until the
    /// platform reports a real value.
    /// </summary>
    public float DisplayDpi { get; private set; } = 96f;

    /// <summary>
    /// Whether the game is in the foreground: true from when it has started until it's paused,
    /// and again once it's resumed.
    /// </summary>
    protected bool IsActive => this._isActive;

    /// <summary>
    /// Whether the game keeps running while it's inactive. An inactive game (its window lost
    /// focus, or something covers it) stops game time and <see cref="OnUpdate"/>, and keeps its
    /// picture correct; set this for a game that should keep going instead, such as one played
    /// on a second monitor. A hidden game stops drawing either way.
    /// </summary>
    public bool RunWhenInactive { get; set; }

    /// <summary>
    /// The frame rate the game would like, or null for the display's own, which is the default. A
    /// casual game can ask for 30 or 60 on a fast display, for the battery, and it can change at any
    /// time, such as to run menus slower than play. It's a preference: the desktop and the web draw
    /// at most this many frames a second, unevenly if it doesn't divide into the display's rate,
    /// and Android and iOS ask the system, which has the last word. It says what the game asked
    /// for, not the rate it's getting.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't positive.</exception>
    public int? PreferredFrameRate
    {
        get => this._preferredFrameRate;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The frame rate must be positive, or null for the display's own.");
            }

            this._preferredFrameRate = value;

            // NOTE: Before the services exist, CreateServices passes it on
            this.Graphics?.SetPreferredFrameRate(value);
        }
    }

    private int? _preferredFrameRate;

    /// <summary>
    /// The samples per pixel the game would like, to smooth the edges of rotated quads, from 1 for
    /// none, which is the default. It's a preference: a device may give fewer, and
    /// <c>Graphics.MultisampleCount</c> says what it gave. Set it in the game's constructor, as the
    /// graphics device is made with it. Render textures aren't multisampled, so a game drawing its
    /// scene through one gets no benefit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1.</exception>
    /// <exception cref="InvalidOperationException">The game's services already exist.</exception>
    public int PreferredMultisampleCount
    {
        get => this._preferredMultisampleCount;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);

            if (this.Graphics is not null)
            {
                throw new InvalidOperationException("Set PreferredMultisampleCount in the game's constructor: the graphics device has been made.");
            }

            this._preferredMultisampleCount = value;
        }
    }

    private int _preferredMultisampleCount = 1;

    /// <summary>
    /// How many times a second of game time <see cref="OnFixedUpdate"/> runs, or null for it not to
    /// run at all, which is the default, so that a game that doesn't use it pays nothing for it.
    /// Unlike <see cref="PreferredFrameRate"/>, it's not a preference, and it has nothing to do with
    /// the display. A game usually sets it once, in its constructor.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't positive.</exception>
    public int? FixedUpdateRate
    {
        get => this._fixedUpdateRate;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The rate must be positive, or null for no fixed updates.");
            }

            this._fixedUpdateRate = value;

            // NOTE: Before the services exist, CreateServices passes it on
            if (this.Time is not null)
            {
                this.Time.FixedStep = FixedStepAt(value);
            }
        }
    }

    private int? _fixedUpdateRate;

    private static TimeSpan? FixedStepAt(
        int? rate)
    {
        return (rate is int perSecond) ? TimeSpan.FromTicks(TimeSpan.TicksPerSecond / perSecond) : null;
    }

    /// <summary>
    /// Completes once the first frame since starting has been drawn. Start work the first
    /// frame doesn't need after it, such as signing in, syncing, or analytics: on the web the
    /// game shares the page's only thread, so work started earlier delays the first frame
    /// even when nothing awaits it. Continuations run on the game thread in a later frame.
    /// </summary>
    public Task FirstFrameDrawn => this._firstFrameDrawn.Task;

    // ── Hooks ──────────────────────────────────────────────────────────

    /// <summary>The first thing to run, once the services exist. Load what the launch screen should wait for.</summary>
    protected virtual Task OnInitialize()
    {
        return Task.CompletedTask;
    }

    /// <summary>Runs after initialization, before the first frame: load content and set up the game.</summary>
    protected virtual Task OnStart()
    {
        return Task.CompletedTask;
    }

    /// <summary>Runs when the game is stopping: save what needs saving.</summary>
    protected virtual Task OnStop()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The game has become inactive: its window lost focus, something covers it, or the app went
    /// to the background. An app can be ended without warning from here, so save now. Work this
    /// awaits runs on, including while the game is hidden, within what the platform allows (a few
    /// seconds on iOS and on a hidden web page); game time doesn't, so don't wait on
    /// <c>Time.DelayAsync</c> here.
    /// </summary>
    protected virtual Task OnPause()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The game is active again.
    /// </summary>
    protected virtual Task OnResume()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The system is short of memory and may end the app: release what can be loaded again. Not
    /// every platform reports this. Runs on the game thread.
    /// </summary>
    protected virtual void OnLowMemory()
    {
    }

    internal void InvokeOnLowMemory()
    {
        this.OnLowMemory();
    }

    /// <summary>
    /// Runs <see cref="FixedUpdateRate"/> times a second of game time, for physics and anything that
    /// must behave the same at any frame rate: none, one or several times a frame, before
    /// <see cref="OnUpdate"/>. It doesn't run until that rate is set, which build rule DG0005 checks.
    /// <c>Time.DeltaSeconds</c> is the step's length here, and
    /// <see cref="TimeService.FixedAlpha"/> smooths drawing between steps.
    /// </summary>
    protected virtual void OnFixedUpdate()
    {
    }

    /// <summary>Runs once a frame while the game is active: move things on by <c>Time.DeltaSeconds</c>.</summary>
    protected virtual void OnUpdate()
    {
    }

    /// <summary>Runs once a frame: draw everything. Draw only, as a hidden game skips it.</summary>
    protected virtual void OnDraw()
    {
    }

    /// <summary>Receives the frame's pointer events together. The default calls <see cref="OnPointerEvent"/> for each.</summary>
    protected virtual void HandlePointerEvents(
        ReadOnlySpan<PointerEvent> pointerEvents)
    {
        foreach (var pointerEvent in pointerEvents)
        {
            this.OnPointerEvent(pointerEvent);
        }
    }

    /// <summary>A touch, mouse or pen event, before <see cref="OnUpdate"/>.</summary>
    protected virtual void OnPointerEvent(
        PointerEvent pointerEvent)
    {
    }

    /// <summary>Receives the frame's key events together. The default calls <see cref="OnKeyEvent"/> for each.</summary>
    protected virtual void HandleKeyEvents(
        ReadOnlySpan<KeyEvent> keyEvents)
    {
        foreach (var keyEvent in keyEvents)
        {
            this.OnKeyEvent(keyEvent);
        }
    }

    /// <summary>A key event, before <see cref="OnUpdate"/>.</summary>
    protected virtual void OnKeyEvent(
        KeyEvent keyEvent)
    {
    }

    /// <summary>Receives the frame's gamepad events together. The default calls <see cref="OnGamepadEvent"/> for each.</summary>
    protected virtual void HandleGamepadEvents(
        ReadOnlySpan<GamepadEvent> gamepadEvents)
    {
        foreach (var gamepadEvent in gamepadEvents)
        {
            this.OnGamepadEvent(gamepadEvent);
        }
    }

    /// <summary>A gamepad connecting, disconnecting or changing, before <see cref="OnUpdate"/>. Events that arrive while the game is starting are dropped, so read <c>Input.GetGamepadState</c> for what's already connected.</summary>
    protected virtual void OnGamepadEvent(
        GamepadEvent gamepadEvent)
    {
    }

    /// <summary>The window's size or safe area changed.</summary>
    protected virtual void OnClientBoundsChanged()
    {
    }

    // ── Called by GameHost ─────────────────────────────────────────────

    /// <summary>
    /// Creates the game's services over the platform's.
    /// </summary>
    internal void CreateServices(
        PlatformServices services)
    {
        this.Time = new TimeService { FixedStep = FixedStepAt(this._fixedUpdateRate) };
        this.Content = new ContentService(services.Content);
        this.Graphics = new GraphicsService(services.Graphics);
        this.Graphics.SetPreferredFrameRate(this._preferredFrameRate);
        this.Audio = new AudioService(services.Audio);
        this.Input = new InputService(services.Input);
        this.Diagnostics = new DiagnosticsService(this.Graphics);
    }

    /// <summary>
    /// Initializes the game's services, in order.
    /// </summary>
    internal async Task InitializeServices()
    {
        await this.Content.Initialize();
        await this.Graphics.Initialize();
        await this.Audio.Initialize();
        await this.Input.Initialize();
    }

    internal bool IsActiveState
    {
        get => this._isActive;
        set => this._isActive = value;
    }

    internal bool IsDisposed => this._isDisposed;

    internal Task InvokeOnInitialize() => this.OnInitialize();

    internal Task InvokeOnStart() => this.OnStart();

    internal Task InvokeOnStop() => this.OnStop();

    internal Task InvokeOnPause() => this.OnPause();

    internal Task InvokeOnResume() => this.OnResume();

    internal void InvokeOnFixedUpdate() => this.OnFixedUpdate();

    internal void InvokeOnUpdate() => this.OnUpdate();

    internal void InvokeOnDraw() => this.OnDraw();

    internal void InvokeHandlePointerEvents(ReadOnlySpan<PointerEvent> pointerEvents) => this.HandlePointerEvents(pointerEvents);

    internal void InvokeHandleGamepadEvents(ReadOnlySpan<GamepadEvent> gamepadEvents) => this.HandleGamepadEvents(gamepadEvents);

    internal void InvokeHandleKeyEvents(ReadOnlySpan<KeyEvent> keyEvents) => this.HandleKeyEvents(keyEvents);

    internal void InvokeOnClientBoundsChanged() => this.OnClientBoundsChanged();

    /// <summary>
    /// Stores the display's bounds and density; a density that isn't positive keeps the last.
    /// </summary>
    internal void SetDisplay(
        RectangleInt clientBounds,
        RectangleInt safeAreaBounds,
        float displayDpi)
    {
        this._clientBounds = clientBounds;
        this._safeAreaBounds = safeAreaBounds;
        if (displayDpi > 0f)
        {
            this.DisplayDpi = displayDpi;
        }
    }

    /// <summary>
    /// Completes <see cref="FirstFrameDrawn"/>.
    /// </summary>
    internal void CompleteFirstFrameDrawn()
    {
        this._firstFrameDrawn.TrySetResult();
    }

    /// <summary>
    /// Replaces a completed <see cref="FirstFrameDrawn"/>, when the game stops, so a later start
    /// waits for its own first frame.
    /// </summary>
    internal void ResetFirstFrameDrawn()
    {
        if (this._firstFrameDrawn.Task.IsCompleted)
        {
            this._firstFrameDrawn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Releases the game's services, after it has stopped.</summary>
    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        GC.SuppressFinalize(this);

        this._isDisposed = true;
        this._isActive = false;

        // Dispose the shared sprite batch
        this.Graphics?.SharedSpriteBatch?.Dispose();

        // Dispose stats overlay resources
        this.Diagnostics?.Release();
    }
}
