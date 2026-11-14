// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using Android.Animation;
using Android.App;
using Android.Views;
using DotGame.Framework.Android.Graphics;

namespace DotGame.Framework.Android;

/// <summary>
/// Android's launch screen, from the theme and drawable the content pipeline generates from the
/// game's <c>LaunchScreen</c> item. From Android 12 the system's splash screen shows it, and it's
/// held on screen until the game's first frame; before Android 12 the launch screen drawable, also
/// the theme's window background, is the game surface's background until then. Either way it fades
/// out over <see cref="GameHost.LaunchScreenFadeDuration"/>.
/// </summary>
internal sealed class LaunchScreenStrategy : ILaunchScreenStrategy
{
    // The drawable resource the content pipeline generates from the game's LaunchScreen item (also
    // the window background of its theme, DotGame.LaunchScreen)
    private const string LaunchScreenDrawableName = "dotgame_launch_screen";

    // NOTE: An undrawn window has no input focus, and a key pressed then (Back, during a slow start)
    // is an ANR after 5 s, so the hold has to end sooner
    private const long SplashScreenHoldLimitMilliseconds = 3000;

    private readonly Activity _activity;
    private readonly GameSurfaceView _surfaceView;

    // From Android 12: holds the system's splash screen until the first frame is presented. UI
    // thread only.
    private SplashScreenHold? _splashScreenHold;

    public LaunchScreenStrategy(
        Activity activity,
        GameSurfaceView surfaceView)
    {
        this._activity = activity;
        this._surfaceView = surfaceView;
    }

    public void Show()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            this.HoldSplashScreen();
        }
        else
        {
            this.ShowAsSurfaceBackground();
        }

        StartupTimeline.Mark("launch-shown");
    }

    public void Dismiss()
    {
        StartupTimeline.Mark("first-frame-presented");
        Trace.Write(0x6f1a2b9a, "[LaunchScreen] first frame presented; dismissing the launch screen on the UI thread");

        // The view hierarchy is the UI thread's; the first frame is reported on the render thread
        this._activity.RunOnUiThread(this.DismissOnUiThread);
    }

    /// <summary>
    /// Keeps the system's splash screen on screen until the game's first frame is presented, and
    /// fades it out then (<see cref="SplashScreenFade"/>). The splash screen stays until the window
    /// first draws, so the content view's draws are held back until then
    /// (<see cref="SplashScreenHold"/>); the game's surface is a separate surface, which draws
    /// meanwhile.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private void HoldSplashScreen()
    {
        var content = this._activity.FindViewById(global::Android.Resource.Id.Content);
        if (content?.ViewTreeObserver is not { } observer)
        {
            return;
        }

        var hold = new SplashScreenHold(content);
        observer.AddOnPreDrawListener(hold);
        this._splashScreenHold = hold;

        this._activity.SplashScreen.SetOnExitAnimationListener(new SplashScreenFade());

        // The launch screen then stays as the surface's background, as before Android 12, until the
        // first frame
        content.PostDelayed(this.EndSplashScreenHoldEarly, SplashScreenHoldLimitMilliseconds);
    }

    private void EndSplashScreenHoldEarly()
    {
        if (this._splashScreenHold is not { } hold)
        {
            return;
        }

        this._splashScreenHold = null;
        this.ShowAsSurfaceBackground();
        hold.Release();
    }

    // The launch screen drawable as the game surface's background, if the app has one
    private void ShowAsSurfaceBackground()
    {
        int launchScreenId = this._activity.Resources?.GetIdentifier(LaunchScreenDrawableName, "drawable", this._activity.PackageName) ?? 0;
        if (launchScreenId != 0)
        {
            this._surfaceView.Background = this._activity.GetDrawable(launchScreenId);
        }
    }

    /// <summary>
    /// Fades out the launch screen, then removes it: from Android 12 by letting the window draw,
    /// which ends the splash screen (faded by <see cref="SplashScreenFade"/>); before, the surface
    /// view's background.
    /// </summary>
    private void DismissOnUiThread()
    {
        if (this._splashScreenHold is { } hold)
        {
            this._splashScreenHold = null;
            hold.Release();
            return;
        }

        var view = this._surfaceView;
        var launchScreen = view.Background;
        if (launchScreen is null)
        {
            return;
        }

        var fade = ValueAnimator.OfInt(255, 0)!;
        fade.SetDuration((long)GameHost.LaunchScreenFadeDuration.TotalMilliseconds);
        fade.SetInterpolator(CreateFadeInterpolator());
        fade.Update += (_, e) => launchScreen.Alpha = (int)e.Animation.AnimatedValue!;
        fade.AnimationEnd += (_, _) => view.Background = null;
        fade.Start();
    }

    // The launch screen fade's curve, as every platform's (GameHost.LaunchScreenFadeEase)
    private static global::Android.Views.Animations.PathInterpolator CreateFadeInterpolator()
    {
        var (x1, y1, x2, y2) = GameHost.LaunchScreenFadeEase;
        return new global::Android.Views.Animations.PathInterpolator(x1, y1, x2, y2);
    }

    /// <summary>
    /// Holds back the content view's draws until released: the system's splash screen stays until
    /// the window first draws.
    /// </summary>
    private sealed class SplashScreenHold : Java.Lang.Object, ViewTreeObserver.IOnPreDrawListener
    {
        private readonly View _content;
        private bool _isReleased;

        public SplashScreenHold(
            View content)
        {
            this._content = content;
        }

        public bool OnPreDraw()
        {
            return this._isReleased;
        }

        public void Release()
        {
            this._isReleased = true;
            this._content.ViewTreeObserver?.RemoveOnPreDrawListener(this);
            this._content.Invalidate();
        }
    }

    /// <summary>
    /// Fades the system's splash screen out over <see cref="GameHost.LaunchScreenFadeDuration"/> when
    /// it ends, as every platform's launch screen does.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private sealed class SplashScreenFade : Java.Lang.Object, global::Android.Window.ISplashScreenOnExitAnimationListener
    {
        public void OnSplashScreenExit(
            global::Android.Window.SplashScreenView view)
        {
            var fade = ObjectAnimator.OfFloat(view, "alpha", 1f, 0f)!;
            fade.SetDuration((long)GameHost.LaunchScreenFadeDuration.TotalMilliseconds);
            fade.SetInterpolator(CreateFadeInterpolator());
            fade.AnimationEnd += (_, _) => view.Remove();
            fade.Start();
        }
    }
}
