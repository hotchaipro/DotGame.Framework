// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Platform;
using Xunit;
using static DotGame.Framework.Tests.Platform.TestPlatform;

namespace DotGame.Framework.Tests.Platform;

/// <summary>
/// How <see cref="GameHost"/> drives a platform's launch screen: shown once, dismissed once, at the
/// first frame a host reports on screen, from whatever thread reports it.
/// </summary>
public sealed class LaunchScreenStrategyTests
{
    [Fact]
    public void ShowLaunchScreen_ShowsIt_WithoutDismissingIt()
    {
        var host = new GameHost(new TestGame());
        var launchScreen = new RecordingLaunchScreen();

        host.ShowLaunchScreen(launchScreen);

        Assert.Equal(["Show"], launchScreen.Calls);
    }

    [Fact]
    public void FirstFramePresented_DismissesIt_Once()
    {
        var host = new GameHost(new TestGame());
        var launchScreen = new RecordingLaunchScreen();
        host.ShowLaunchScreen(launchScreen);

        host.NotifyFirstFramePresented();
        host.NotifyFirstFramePresented();

        Assert.Equal(["Show", "Dismiss"], launchScreen.Calls);
    }

    [Fact]
    public void FirstFramePresented_FromAnotherThread_DismissesIt()
    {
        var host = new GameHost(new TestGame());
        var launchScreen = new RecordingLaunchScreen();
        host.ShowLaunchScreen(launchScreen);

        OnOtherThread(host.NotifyFirstFramePresented);

        Assert.Equal(["Show", "Dismiss"], launchScreen.Calls);
    }

    [Fact]
    public void FirstFramePresented_WithNoLaunchScreen_DoesNothing()
    {
        var host = new GameHost(new TestGame());

        host.NotifyFirstFramePresented();
    }

    [Fact]
    public void ShowLaunchScreen_Twice_Throws()
    {
        var host = new GameHost(new TestGame());
        host.ShowLaunchScreen(new RecordingLaunchScreen());

        Assert.Throws<InvalidOperationException>(() => host.ShowLaunchScreen(new RecordingLaunchScreen()));
    }

    [Fact]
    public void ShowLaunchScreen_WithNone_Throws()
    {
        var host = new GameHost(new TestGame());

        Assert.Throws<ArgumentNullException>(() => host.ShowLaunchScreen(null!));
    }

    private sealed class RecordingLaunchScreen : ILaunchScreenStrategy
    {
        public List<string> Calls { get; } = [];

        public void Show()
        {
            this.Calls.Add("Show");
        }

        public void Dismiss()
        {
            this.Calls.Add("Dismiss");
        }
    }
}
