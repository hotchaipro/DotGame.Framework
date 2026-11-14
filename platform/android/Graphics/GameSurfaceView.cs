// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
using DotGame.Framework.Android.Input;

namespace DotGame.Framework.Android.Graphics;

/// <summary>
/// A <see cref="SurfaceView"/> that drives a self-managed EGL render thread
/// (<see cref="RenderThread"/>) instead of the framework-owned GL
/// thread used by <c>GLSurfaceView</c>.
/// </summary>
/// <remarks>
/// The Android UI thread delivers surface lifecycle callbacks here. Every
/// callback forwards to the render thread as a non-blocking command so the UI
/// thread never makes an unbounded cross-thread wait — the structural cause of
/// the pause/teardown ANRs that <c>GLSurfaceView.onPause()</c>
/// (<c>Object.wait()</c> with no timeout) produced. The single exception is
/// <see cref="SurfaceDestroyed(ISurfaceHolder)"/>, which performs a short,
/// ANR-safe bounded handshake (&lt;= 250 ms) so the render thread releases its
/// EGL window surface before the framework frees the underlying
/// <c>ANativeWindow</c>, as required by the
/// <see cref="ISurfaceHolderCallback"/> contract.
/// <para>
/// This view also delivers the earliest window-going-away signals
/// (<see cref="OnWindowVisibilityChanged"/>,
/// <see cref="OnWindowVisibilityChanged(ViewStates)"/>,
/// <see cref="OnDetachedFromWindow"/>) to the render thread so it suspends
/// presentation before the OS performs its synchronous
/// <c>HardwareRenderer.nSetStopped</c> handoff to the system RenderThread. That
/// keeps the render thread out of <c>eglSwapBuffers</c> during a window stop,
/// which is what makes it impossible for the main thread to block behind it.
/// </para>
/// </remarks>
internal sealed class GameSurfaceView : SurfaceView, ISurfaceHolderCallback
{
    // Upper bound on how long the UI thread waits in surfaceDestroyed for the
    // render thread to release its EGL window surface. The SurfaceHolder
    // contract requires the rendering thread to stop touching the Surface
    // before this method returns; we honor that with a bounded wait kept far
    // under the ~5s ANR threshold so a wedged render thread degrades to a
    // skipped release instead of a system-fatal ANR (SIGQUIT).
    private static readonly TimeSpan SurfaceReleaseTimeout = TimeSpan.FromMilliseconds(250);

    private readonly RenderThread _renderThread;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private volatile int _imeBottomInset;
    private volatile WindowInsets? _appliedInsets;
    private string? _lastInsets;
    private SoftKeyboardTracker? _softKeyboardTracker;

    public GameSurfaceView(
        Context context,
        RenderThread renderThread)
        : base(context)
    {
        this._renderThread = renderThread;

        // Register for surface lifecycle callbacks. The holder is always
        // available once the SurfaceView is constructed.
        this.Holder!.AddCallback(this);
    }

    /// <summary>
    /// Delivered on the UI thread when the surface is first created. Forwards
    /// the native <see cref="Surface"/> to the render thread (non-blocking) so
    /// it can create its EGL window surface.
    /// </summary>
    public void SurfaceCreated(
        ISurfaceHolder holder)
    {
        var surface = holder.Surface;
        if (surface is not null)
        {
            this._renderThread.NotifySurfaceCreated(surface);
        }
    }

    /// <summary>
    /// Delivered on the UI thread after any structural change (always at least
    /// once, after <see cref="SurfaceCreated(ISurfaceHolder)"/>). Forwards the
    /// new dimensions to the render thread (non-blocking).
    /// </summary>
    public void SurfaceChanged(
        ISurfaceHolder holder,
        Format format,
        int width,
        int height)
    {
        this._surfaceWidth = width;
        this._surfaceHeight = height;

        this._renderThread.NotifySurfaceChanged(width, height);
    }

    /// <summary>
    /// How far the soft keyboard (IME) covers the bottom of the view, in pixels. The window is
    /// not resized for the keyboard (the activity uses <c>adjustNothing</c>), so the engine
    /// treats this as part of the bottom safe-area inset instead, which shortens the game's
    /// layout area without resizing the surface.
    /// </summary>
    public int ImeBottomInset => this._imeBottomInset;

    /// <summary>
    /// Delivered on the UI thread when the window insets change, including, on Android 11
    /// (API 30) and later, when the soft keyboard is shown or hidden.
    /// </summary>
    /// <remarks>
    /// NOTE: Not called if a listener is set with <c>SetOnApplyWindowInsetsListener</c> on
    /// this view, which replaces this method.
    /// </remarks>
    public override WindowInsets? OnApplyWindowInsets(
        WindowInsets? insets)
    {
        var result = base.OnApplyWindowInsets(insets);

        // NOTE: Immutable, and for the game thread to read, as the view's own state is the UI thread's
        this._appliedInsets = insets;

        if ((insets is not null) && (OperatingSystem.IsAndroidVersionAtLeast(30)))
        {
            this.SetImeBottomInset(insets.GetInsets(WindowInsets.Type.Ime())?.Bottom ?? 0);
        }

        // System bars and cutouts change the safe area too, as when the bars hide after the first layout
        string safeInsets = insets?.ToString() ?? string.Empty;
        if (safeInsets != this._lastInsets)
        {
            this._lastInsets = safeInsets;
            if ((this._surfaceWidth > 0) && (this._surfaceHeight > 0))
            {
                this._renderThread.NotifySurfaceChanged(this._surfaceWidth, this._surfaceHeight);
            }
        }

        return result;
    }

    /// <summary>
    /// The window insets last delivered to the view, for any thread.
    /// </summary>
    internal WindowInsets? AppliedInsets => this._appliedInsets;

    /// <summary>
    /// Forwards a change in the keyboard's height as a size change of the unchanged surface, so
    /// the engine recomputes its safe area. The surface and its buffers are not resized.
    /// </summary>
    private void SetImeBottomInset(
        int imeBottomInset)
    {
        if (imeBottomInset == this._imeBottomInset)
        {
            return;
        }

        this._imeBottomInset = imeBottomInset;

        if ((this._surfaceWidth > 0) && (this._surfaceHeight > 0))
        {
            this._renderThread.NotifySurfaceChanged(this._surfaceWidth, this._surfaceHeight);
        }
    }

    /// <summary>
    /// Delivered on the UI thread when the view is attached to its window. Versions before
    /// Android 11 (API 30) don't report the keyboard in the window insets, so it is measured
    /// separately while the view is attached.
    /// </summary>
    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();

        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            this._softKeyboardTracker ??= new SoftKeyboardTracker(this, this.SetImeBottomInset);
            this._softKeyboardTracker.Start();
        }
    }

    /// <summary>
    /// Delivered on the UI thread immediately before the surface is destroyed.
    /// Marks the surface invalid at once (so the render thread stops swapping
    /// immediately), enqueues the destroy command, and waits up to
    /// <see cref="SurfaceReleaseTimeout"/> for the render thread to release its
    /// EGL window surface before returning — regardless of whether the release
    /// acknowledged in time.
    /// </summary>
    public void SurfaceDestroyed(
        ISurfaceHolder holder)
    {
        this._renderThread.NotifySurfaceDestroyed(SurfaceReleaseTimeout);
    }

    /// <summary>
    /// Delivered on the UI thread when the window gains or loses focus. Focus lost makes the game
    /// inactive, and it keeps drawing: the window is still on screen (under the notification
    /// shade, or beside the focused window in split screen). The signals that the window is going
    /// away are visibility, detach and the activity's pause, and those stop presentation.
    /// </summary>
    public override void OnWindowFocusChanged(
        bool hasWindowFocus)
    {
        base.OnWindowFocusChanged(hasWindowFocus);

        this._renderThread.NotifyWindowFocus(hasWindowFocus);
    }

    /// <summary>
    /// Delivered on the UI thread when the view's window visibility changes.
    /// A window that is no longer <see cref="ViewStates.Visible"/> is being
    /// backgrounded, so presentation is suspended (a second, independent signal
    /// alongside focus loss). Visibility restored resumes presentation. Both
    /// calls are non-blocking.
    /// </summary>
    protected override void OnWindowVisibilityChanged(
        ViewStates visibility)
    {
        base.OnWindowVisibilityChanged(visibility);

        if (visibility == ViewStates.Visible)
        {
            this._renderThread.ResumePresentation("window-visible");
        }
        else
        {
            this._renderThread.SuspendPresentation("window-not-visible");
        }
    }

    /// <summary>
    /// Delivered on the UI thread when the view is detached from its window.
    /// This is the last point at which the render thread must not be presenting
    /// to the outgoing window, so presentation is suspended (non-blocking).
    /// </summary>
    protected override void OnDetachedFromWindow()
    {
        this._renderThread.SuspendPresentation("detached-from-window");

        // The popup window must be dismissed before its anchor's window goes away
        this._softKeyboardTracker?.Stop();

        base.OnDetachedFromWindow();
    }
}
