// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Platform;

/// <summary>
/// A platform's launch screen at run time: it puts the launch screen up, and takes it down once the
/// game's first frame is on screen. The build makes each platform's native form of the launch screen
/// (<see cref="LaunchScreen"/>); this is the only code on a platform that knows what that form is,
/// so a host knows nothing of how its launch screen was made, only when the game is ready.
/// </summary>
/// <remarks>
/// A host gives its strategy to <see cref="GameHost.ShowLaunchScreen"/>, which shows it, and reports
/// the first frame on screen to <see cref="GameHost.NotifyFirstFramePresented"/>, which dismisses
/// it, once. A strategy records the startup timeline's <c>launch-shown</c> and
/// <c>first-frame-presented</c> marks where its platform can tell (the web page records them
/// itself).
/// </remarks>
internal interface ILaunchScreenStrategy
{
    /// <summary>
    /// Puts the launch screen on screen, or keeps the one the system showed at launch there. Called
    /// once, on the platform's UI thread, before the game starts.
    /// </summary>
    void Show();

    /// <summary>
    /// The game's first frame is on screen: fades the launch screen out over
    /// <see cref="GameHost.LaunchScreenFadeDuration"/> on <see cref="GameHost.LaunchScreenFadeEase"/>,
    /// then removes it. Called once, on any thread: the strategy moves to the thread it needs.
    /// </summary>
    void Dismiss();
}
