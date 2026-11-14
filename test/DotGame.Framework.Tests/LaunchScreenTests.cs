// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Content;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Content;
using Xunit;

namespace DotGame.Framework.Tests;

public sealed class LaunchScreenTests
{
    private static ContentManifestRegistry Registry(
        params KeyValuePair<string, string>[] properties)
    {
        var manifest = new ContentManifest(LaunchScreen.ManifestIdentity);
        manifest.Add(LaunchScreen.SettingsKey, LaunchScreen.SettingsKey, properties: properties);
        var registry = new ContentManifestRegistry();
        registry.Register(manifest);
        return registry;
    }

    [Fact]
    public void NothingRegistered_IsNoLaunchScreen()
    {
        var settings = LaunchScreen.Read(new ContentManifestRegistry());

        Assert.False(settings.IsDefined);
        Assert.Equal(Color.Black, settings.Background);
        Assert.Equal(Vector2.Zero, settings.LogoSize);
    }

    [Theory]
    [InlineData("#12345G")]
    [InlineData("#")]
    public void ABackgroundTheBuildWouldNotWrite_Throws(
        string background)
    {
        Assert.Throws<FormatException>(() => LaunchScreen.Read(Registry(
            new(LaunchScreen.BackgroundProperty, background),
            new(LaunchScreen.LogoWidthProperty, "160"),
            new(LaunchScreen.LogoHeightProperty, "80"))));
    }

    [Fact]
    public void ReadsWhatTheBuildRegisters()
    {
        var settings = LaunchScreen.Read(Registry(
            new(LaunchScreen.BackgroundProperty, "#5ba7dd"),
            new(LaunchScreen.LogoWidthProperty, "160"),
            new(LaunchScreen.LogoHeightProperty, "80.5"),
            new(LaunchScreen.BrandingWidthProperty, "200"),
            new(LaunchScreen.BrandingHeightProperty, "40")));

        Assert.True(settings.IsDefined);
        Assert.Equal(Color.FromRgb(0x5BA7DD), settings.Background);
        Assert.Equal(new Vector2(160, 80.5f), settings.LogoSize);
        Assert.Equal(new Vector2(200, 40), settings.BrandingSize);
    }

    [Fact]
    public void NoBranding_HasNoSize()
    {
        var settings = LaunchScreen.Read(Registry(
            new(LaunchScreen.BackgroundProperty, "#000000"),
            new(LaunchScreen.LogoWidthProperty, "160"),
            new(LaunchScreen.LogoHeightProperty, "160")));

        Assert.Equal(Vector2.Zero, settings.BrandingSize);
    }
}
