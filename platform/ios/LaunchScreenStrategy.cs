// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using Foundation;
using UIKit;

namespace DotGame.Framework.iOS;

/// <summary>
/// iOS's launch screen: the app's launch storyboard (Info.plist's <c>UILaunchStoryboardName</c>,
/// which the content pipeline generates from the game's <c>LaunchScreen</c> item), which the system
/// shows while the app launches. It's kept over the game view as an overlay until the game's first
/// frame is on screen, then faded out over <see cref="GameHost.LaunchScreenFadeDuration"/>.
/// </summary>
internal sealed class LaunchScreenStrategy : ILaunchScreenStrategy
{
    private readonly UIView _gameView;

    // The overlay, until it's dismissed. Main thread only.
    private UIView? _overlay;

    public LaunchScreenStrategy(
        UIView gameView)
    {
        this._gameView = gameView;
    }

    /// <summary>
    /// Overlays the launch storyboard on the game view, if the app has one. Added in the same run
    /// loop turn the window appears, so the system's launch screen and the overlay are
    /// indistinguishable.
    /// </summary>
    public void Show()
    {
        if (NSBundle.MainBundle.ObjectForInfoDictionary("UILaunchStoryboardName") is not NSString name)
        {
            return;
        }

        var overlay = UIStoryboard.FromName(name, null).InstantiateInitialViewController()?.View;
        if (overlay is null)
        {
            return;
        }

        overlay.Frame = this._gameView.Bounds;
        overlay.AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
        this._gameView.AddSubview(overlay);
        this._overlay = overlay;

        StartupTimeline.Mark("launch-shown");
    }

    /// <summary>
    /// Fades the overlay out. Called on whatever thread Metal reports the presentation on.
    /// </summary>
    public void Dismiss()
    {
        StartupTimeline.Mark("first-frame-presented");

        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            var overlay = this._overlay;
            this._overlay = null;
            if (overlay is null)
            {
                return;
            }

            // On the curve every platform's fade follows (GameHost.LaunchScreenFadeEase)
            var (x1, y1, x2, y2) = GameHost.LaunchScreenFadeEase;
            var fade = new UIViewPropertyAnimator(
                GameHost.LaunchScreenFadeDuration.TotalSeconds,
                new UICubicTimingParameters(new CoreGraphics.CGPoint(x1, y1), new CoreGraphics.CGPoint(x2, y2)));
            fade.AddAnimations(() => overlay.Alpha = 0);
            fade.AddCompletion(_ => overlay.RemoveFromSuperview());
            fade.StartAnimation();
        });
    }
}
