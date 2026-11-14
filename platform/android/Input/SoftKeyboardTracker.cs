// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace DotGame.Framework.Android.Input;

/// <summary>
/// Measures how far the soft keyboard covers the bottom of a view on Android versions before 11
/// (API 30). Those versions report the keyboard in the window insets only when the window is
/// resized for it (<c>adjustResize</c>), which the game's window isn't, and which is ignored for
/// fullscreen windows anyway.
/// </summary>
/// <remarks>
/// The measurement is made by an invisible, zero-width popup window that is the full height of
/// the screen and is resized for the keyboard. The bottom of its visible frame is the top of the
/// keyboard, whatever the soft input mode of the game's own window.
/// </remarks>
internal sealed class SoftKeyboardTracker : PopupWindow, ViewTreeObserver.IOnGlobalLayoutListener
{
    // Uncovered strips shorter than this, such as a navigation bar, are not a keyboard
    private const int MinimumKeyboardHeightDp = 100;

    private readonly View _anchorView;
    private readonly View _measureView;
    private readonly Action<int> _coveredHeightChanged;
    private readonly int _minimumKeyboardHeight;
    private readonly Rect _visibleFrame = new Rect();
    private readonly int[] _anchorLocation = new int[2];
    private bool _isStarted;
    private int _coveredHeight;

    /// <param name="anchorView">The view whose covered height is measured.</param>
    /// <param name="coveredHeightChanged">
    /// Called on the UI thread with the new height, in pixels, of the bottom of
    /// <paramref name="anchorView"/> that the keyboard covers.
    /// </param>
    public SoftKeyboardTracker(
        View anchorView,
        Action<int> coveredHeightChanged)
        : base(anchorView.Context)
    {
        this._anchorView = anchorView;
        this._coveredHeightChanged = coveredHeightChanged;

        float density = anchorView.Resources?.DisplayMetrics?.Density ?? 1f;
        this._minimumKeyboardHeight = (int)(MinimumKeyboardHeightDp * density);

        this._measureView = new View(anchorView.Context);
        this.ContentView = this._measureView;
        this.Width = 0;
        this.Height = ViewGroup.LayoutParams.MatchParent;
        this.SoftInputMode = SoftInput.AdjustResize;
        this.InputMethodMode = InputMethod.Needed;
        this.Touchable = false;
        this.SetBackgroundDrawable(new ColorDrawable(global::Android.Graphics.Color.Transparent));

        this._measureView.ViewTreeObserver!.AddOnGlobalLayoutListener(this);
    }

    /// <summary>
    /// Starts measuring once the anchor view is attached to its window. Call on the UI thread.
    /// </summary>
    public void Start()
    {
        this._isStarted = true;

        // The popup needs the anchor's window token, which isn't usable until the window has
        // finished attaching, so it is shown after the current UI message.
        this._anchorView.Post(() =>
        {
            if ((this._isStarted) && (!this.IsShowing) && (this._anchorView.IsAttachedToWindow))
            {
                this.ShowAtLocation(this._anchorView, GravityFlags.NoGravity, 0, 0);
            }
        });
    }

    /// <summary>
    /// Stops measuring, before the anchor view is detached from its window. Call on the UI
    /// thread.
    /// </summary>
    public void Stop()
    {
        this._isStarted = false;

        if (this.IsShowing)
        {
            this.Dismiss();
        }

        this.SetCoveredHeight(0);
    }

    public void OnGlobalLayout()
    {
        this._measureView.GetWindowVisibleDisplayFrame(this._visibleFrame);
        this._anchorView.GetLocationOnScreen(this._anchorLocation);

        int anchorBottom = this._anchorLocation[1] + this._anchorView.Height;
        int coveredHeight = anchorBottom - this._visibleFrame.Bottom;

        if (coveredHeight < this._minimumKeyboardHeight)
        {
            coveredHeight = 0;
        }

        this.SetCoveredHeight(coveredHeight);
    }

    private void SetCoveredHeight(
        int coveredHeight)
    {
        if (coveredHeight != this._coveredHeight)
        {
            this._coveredHeight = coveredHeight;
            this._coveredHeightChanged(coveredHeight);
        }
    }
}
