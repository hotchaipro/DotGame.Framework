// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using Xunit;

namespace DotGame.Framework.Tests.Platform;

public sealed class PreferredFrameRateTests
{
    private sealed class EmptyGame : Game
    {
    }

    [Fact]
    public void Default_IsTheDisplaysOwn()
    {
        using var game = new EmptyGame();

        Assert.Null(game.PreferredFrameRate);
    }

    [Fact]
    public void SetBeforeTheServicesExist_ReachesThePlatform()
    {
        using var game = new EmptyGame();
        game.PreferredFrameRate = 60;

        var services = TestPlatform.CreateServices();
        game.CreateServices(services);

        Assert.Equal(60, services.Graphics.Interface.PreferredFrameRate);
    }

    [Fact]
    public void ChangedWhileRunning_ReachesThePlatform()
    {
        using var game = new EmptyGame();
        var services = TestPlatform.CreateServices();
        game.CreateServices(services);
        Assert.Null(services.Graphics.Interface.PreferredFrameRate);

        game.PreferredFrameRate = 30;
        Assert.Equal(30, game.PreferredFrameRate);
        Assert.Equal(30, services.Graphics.Interface.PreferredFrameRate);

        game.PreferredFrameRate = null;
        Assert.Null(services.Graphics.Interface.PreferredFrameRate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void AValueThatIsNotPositive_Throws_AndChangesNothing(
        int frameRate)
    {
        using var game = new EmptyGame();
        var services = TestPlatform.CreateServices();
        game.CreateServices(services);
        game.PreferredFrameRate = 60;

        Assert.Throws<ArgumentOutOfRangeException>(() => game.PreferredFrameRate = frameRate);

        Assert.Equal(60, game.PreferredFrameRate);
        Assert.Equal(60, services.Graphics.Interface.PreferredFrameRate);
    }
}
