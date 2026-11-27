// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Text.RegularExpressions;
using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.BuildTool;
using DotGame.Framework.ContentPipeline.BuildTool.Graphics;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

/// <summary>
/// iOS's launch storyboard and its images: iOS keeps a launch screen's images by name, so a
/// changed image has to have another name.
/// </summary>
public sealed class LaunchScreenCommandTests
{
    // Builds iOS's launch screen, and returns the names of the images its storyboard shows
    private static string[] BuildIos(TestDirectory directory, string output, RgbaImage logo, RgbaImage branding, string background = "#faecd2", int logoSize = 64)
    {
        string logoFile = directory.Path_(output + "-logo.png");
        string brandingFile = directory.Path_(output + "-branding.png");
        logo.SavePng(logoFile);
        branding.SavePng(brandingFile);
        string outputDir = directory.Path_(output);

        LaunchScreenCommand.Run(CommandLineOptions.Parse([
            "--platform", "ios",
            "--logo", logoFile,
            "--logo-size", logoSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--branding", brandingFile,
            "--branding-width", "40",
            "--background", background,
            "--output-dir", outputDir,
        ]));

        string storyboard = File.ReadAllText(Path.Combine(outputDir, "LaunchScreen.storyboard"));
        var shown = new List<string>();
        foreach (Match match in Regex.Matches(storyboard, "<imageView [^>]*image=\"([^\"]+)\""))
        {
            string name = match.Groups[1].Value;
            shown.Add(name);
            Assert.Contains($"<image name=\"{name}\"", storyboard, StringComparison.Ordinal);
            foreach (string suffix in new[] { "", "@2x", "@3x" })
            {
                Assert.True(File.Exists(Path.Combine(outputDir, name + suffix + ".png")), name + suffix);
            }
        }

        Assert.Equal(6, Directory.GetFiles(outputDir, "*.png").Length);
        return [.. shown];
    }

    private static RgbaImage Logo(byte r = 200) => Images.Rectangle(32, 32, r, 40, 60, 8, 8, 24, 24);

    private static RgbaImage Branding(byte r = 30) => Images.Rectangle(40, 16, r, 20, 80);

    [Fact]
    public void Ios_TheSameImages_KeepTheirNames_WhateverTheColor()
    {
        using var directory = new TestDirectory();

        string[] first = BuildIos(directory, "a", Logo(), Branding());
        string[] again = BuildIos(directory, "b", Logo(), Branding(), background: "#5ba7dd");

        Assert.Equal(2, first.Length);
        Assert.Equal(first, again);
    }

    [Fact]
    public void Ios_AChangedImage_HasAnotherName()
    {
        using var directory = new TestDirectory();

        string[] first = BuildIos(directory, "a", Logo(), Branding());
        string[] logoChanged = BuildIos(directory, "b", Logo(r: 90), Branding());
        string[] brandingChanged = BuildIos(directory, "c", Logo(), Branding(r: 90));

        Assert.NotEqual(first[0], logoChanged[0]);
        Assert.Equal(first[1], logoChanged[1]);
        Assert.Equal(first[0], brandingChanged[0]);
        Assert.NotEqual(first[1], brandingChanged[1]);
    }

    [Fact]
    public void Ios_AnImageDrawnAtAnotherSize_HasAnotherName()
    {
        using var directory = new TestDirectory();

        string[] first = BuildIos(directory, "a", Logo(), Branding());
        string[] larger = BuildIos(directory, "b", Logo(), Branding(), logoSize: 80);

        Assert.NotEqual(first[0], larger[0]);
    }

    // Builds the web's launch screen images, and returns the logo's and branding's WebP chunk
    // kinds ("VP8L" lossless; "VP8X" for lossy with alpha)
    private static (string Logo, string Branding) BuildWeb(TestDirectory directory, string output, params string[] encoding)
    {
        string logoFile = directory.Path_(output + "-logo.png");
        string brandingFile = directory.Path_(output + "-branding.png");
        Logo().SavePng(logoFile);
        Branding().SavePng(brandingFile);
        string outputDir = directory.Path_(output);

        LaunchScreenCommand.Run(CommandLineOptions.Parse([
            "--platform", "webgl",
            "--logo", logoFile,
            "--logo-size", "64",
            "--branding", brandingFile,
            "--background", "#faecd2",
            "--output-dir", outputDir,
            .. encoding,
        ]));

        static string Kind(string file) => System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file), 12, 4);
        return (Kind(Path.Combine(outputDir, "logo.webp")), Kind(Path.Combine(outputDir, "branding.webp")));
    }

    [Fact]
    public void Web_ImagesAreLossless_ByDefault()
    {
        using var directory = new TestDirectory();

        Assert.Equal(("VP8L", "VP8L"), BuildWeb(directory, "a"));
    }

    [Fact]
    public void Web_LossyCompression_EncodesBothImagesLossy()
    {
        using var directory = new TestDirectory();

        var (logo, branding) = BuildWeb(directory, "a", "--compression", "Lossy", "--lossy-quality", "9");

        Assert.NotEqual("VP8L", logo);
        Assert.NotEqual("VP8L", branding);
    }
}
