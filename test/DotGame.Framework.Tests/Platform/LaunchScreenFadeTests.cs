// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Platform;
using Xunit;

namespace DotGame.Framework.Tests.Platform;

/// <summary>
/// The launch screen fade's curve, which hosts that animate the fade themselves (desktop)
/// evaluate, and the others give their platform as a cubic Bézier timing.
/// </summary>
public sealed class LaunchScreenFadeTests
{
    [Fact]
    public void Ease_IsMaterialEmphasizedAccelerate()
    {
        Assert.Equal((0.3f, 0f, 0.8f, 0.15f), GameHost.LaunchScreenFadeEase);
        Assert.Equal("cubic-bezier(0.3, 0, 0.8, 0.15)", GameHost.LaunchScreenFadeCssEase);
    }

    // cubic-bezier(0.3, 0, 0.8, 0.15), evaluated independently
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.1f, 0.0055f)]
    [InlineData(0.25f, 0.0353f)]
    [InlineData(0.5f, 0.1540f)]
    [InlineData(0.75f, 0.4056f)]
    [InlineData(0.9f, 0.6833f)]
    [InlineData(1f, 1f)]
    public void EaseLaunchScreenFade_FollowsTheCurve(
        float time,
        float expected)
    {
        Assert.Equal(expected, GameHost.EaseLaunchScreenFade(time), 0.001f);
    }

    [Fact]
    public void EaseLaunchScreenFade_ClampsOutsideTheFade()
    {
        Assert.Equal(0f, GameHost.EaseLaunchScreenFade(-1f));
        Assert.Equal(1f, GameHost.EaseLaunchScreenFade(2f));
    }
}
