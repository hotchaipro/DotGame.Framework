// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.WebGL;

/// <summary>
/// The web's launch screen: the page's <c>#splash</c>, which the content pipeline generates from
/// the game's <c>LaunchScreen</c> item, and which the page shows before any of the game's code
/// runs. The page knows when a frame is composited, so dismissing it hands over to
/// <c>firstFrameDrawn</c> in <c>game.js</c>: it waits for the browser to composite the frame,
/// records the startup timeline's <c>first-frame-presented</c> (as <c>main.js</c> records
/// <c>launch-shown</c>), and fades <c>#splash</c> out over
/// <see cref="GameHost.LaunchScreenFadeDuration"/>.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("browser")]
internal sealed class LaunchScreenStrategy : ILaunchScreenStrategy
{
    // The page shows it already
    public void Show()
    {
    }

    public void Dismiss()
    {
        GameBindings.FirstFrameDrawn((int)GameHost.LaunchScreenFadeDuration.TotalMilliseconds, GameHost.LaunchScreenFadeCssEase);
    }
}
