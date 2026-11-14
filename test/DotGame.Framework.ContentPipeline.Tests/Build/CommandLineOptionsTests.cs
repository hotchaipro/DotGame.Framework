// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.ContentPipeline.BuildTool;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

public sealed class CommandLineOptionsTests
{
    private static CommandLineOptions Parse(params string[] args) => CommandLineOptions.Parse(args);

    [Fact]
    public void Parse_RejectsABareArgument()
    {
        Assert.Throws<ContentBuildException>(() => Parse("texture"));
        Assert.Throws<ContentBuildException>(() => Parse("--"));
    }

    [Fact]
    public void Parse_RejectsARepeatedOption()
    {
        Assert.Throws<ContentBuildException>(() => Parse("--input", "a", "--input", "b"));
    }

    [Fact]
    public void GetRequired_FailsWhenMissing_OrWithNoValue()
    {
        Assert.Throws<ContentBuildException>(() => Parse().GetRequired("input"));
        Assert.Throws<ContentBuildException>(() => Parse("--input").GetRequired("input"));
        Assert.Throws<ContentBuildException>(() => Parse("--input", " ").GetRequired("input"));
    }

    [Fact]
    public void GetString_UsesTheDefaultWhenMissing()
    {
        Assert.Equal("d", Parse().GetString("name", "d"));
        Assert.Equal("v", Parse("--name", "v").GetString("name", "d"));
    }

    [Fact]
    public void GetToolPath_FallsBackToTheExecutableName_AndStripsQuotes()
    {
        Assert.Equal("ffmpeg", Parse().GetToolPath("ffmpeg", "ffmpeg"));
        Assert.Equal("ffmpeg", Parse("--ffmpeg", "\"\"").GetToolPath("ffmpeg", "ffmpeg"));
        Assert.Equal("my tool", Parse("--ffmpeg", "\"my tool\"").GetToolPath("ffmpeg", "ffmpeg"));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    public void GetInt32_ReadsAValueInRange(string text, int expected)
    {
        Assert.Equal(expected, Parse("--quality", text).GetInt32("quality", 8, 1, 10));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("x")]
    [InlineData("1.5")]
    public void GetInt32_RejectsAValueOutOfRange(string text)
    {
        Assert.Throws<ContentBuildException>(() => Parse("--quality", text).GetInt32("quality", 8, 1, 10));
    }

    [Fact]
    public void GetInt32_UsesTheDefaultWhenMissing()
    {
        Assert.Equal(8, Parse().GetInt32("quality", 8, 1, 10));
    }

    [Fact]
    public void GetChoice_IgnoresCase_AndReturnsTheChoicesOwnSpelling()
    {
        Assert.Equal("Lossy", Parse("--compression", "lossy").GetChoice("compression", null, "Lossless", "Lossy"));
    }

    [Fact]
    public void GetChoice_FailsOnAnUnknownValue_AndOnARequiredOneMissing()
    {
        Assert.Throws<ContentBuildException>(() => Parse("--compression", "zip").GetChoice("compression", null, "Lossless", "Lossy"));
        Assert.Throws<ContentBuildException>(() => Parse().GetChoice("compression", null, "Lossless", "Lossy"));
    }

    [Fact]
    public void GetSwitch_ReadsPresenceOrAnExplicitValue()
    {
        Assert.False(Parse().GetSwitch("square"));
        Assert.True(Parse("--square").GetSwitch("square"));
        Assert.True(Parse("--square", "true").GetSwitch("square"));
        Assert.False(Parse("--square", "false").GetSwitch("square"));
        Assert.Throws<ContentBuildException>(() => Parse("--square", "maybe").GetSwitch("square"));
    }

    [Fact]
    public void ThrowIfUnrecognized_NamesAnOptionNoCommandRead()
    {
        CommandLineOptions options = Parse("--input", "a", "--inptu", "b");
        options.GetRequired("input");

        var error = Assert.Throws<ContentBuildException>(options.ThrowIfUnrecognized);

        Assert.Contains("--inptu", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfUnrecognized_AcceptsAnOptionThatWasReadButIsAbsent()
    {
        CommandLineOptions options = Parse("--input", "a");
        options.GetRequired("input");
        options.GetString("other");

        options.ThrowIfUnrecognized();
    }
}
