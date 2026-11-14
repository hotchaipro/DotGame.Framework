// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Tasks;

public sealed class WebPageTaskTests
{
    private static string Generate(
        TestDirectory directory,
        string? sourcePage = null,
        bool hasLaunchScreen = false,
        string[]? preload = null,
        string? manifest = null)
    {
        string? source = null;
        if (sourcePage is not null)
        {
            source = directory.Path_("wwwroot", "index.html");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, sourcePage);
        }

        string output = directory.Path_("out", "index.html");
        var engine = new RecordingBuildEngine();
        var task = new _GenerateWebPage
        {
            BuildEngine = engine,
            SourcePage = source,
            OutputPage = output,
            Title = "My Game",
            ManifestName = manifest,
            MainModule = "_dotgame/main.js",
            StartupModules = [new TaskItem("_dotgame/main.js"), new TaskItem("_dotgame/game.js")],
            PreloadContent = (preload ?? []).Select(stem => (ITaskItem)new TaskItem(stem)).ToArray(),
            ContentOutputDir = directory.Path_("content") + Path.DirectorySeparatorChar,
            HasLaunchScreen = hasLaunchScreen,
        };

        Assert.True(task.Execute(), string.Join(" ", engine.Errors));
        return File.ReadAllText(output);
    }

    private static void WriteContent(
        TestDirectory directory,
        params string[] files)
    {
        foreach (string file in files)
        {
            string path = directory.Path_("content", "wwwroot", "Content", file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    [Fact]
    public void NoPage_GetsAWholeOne()
    {
        using var directory = new TestDirectory();

        string page = Generate(directory);

        Assert.Contains("<title>My Game</title>", page, StringComparison.Ordinal);
        Assert.Contains("name=\"viewport\"", page, StringComparison.Ordinal);
        Assert.Contains("<canvas id=\"canvas\"></canvas>", page, StringComparison.Ordinal);
        Assert.Contains("<script type=\"module\" src=\"_dotgame/main#[.{fingerprint}].js\"></script>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportMap_ComesBeforeTheModulePreloads()
    {
        using var directory = new TestDirectory();

        string page = Generate(directory);

        int map = page.IndexOf("type=\"importmap\"", StringComparison.Ordinal);
        int preload = page.IndexOf("rel=\"modulepreload\"", StringComparison.Ordinal);
        Assert.InRange(map, 0, preload);
    }

    [Fact]
    public void ImportMap_InTheGamesPage_ComesBeforeTheGeneratedPreloads()
    {
        using var directory = new TestDirectory();

        string page = Generate(directory, "<html><head><script type=\"importmap\"></script></head><body></body></html>");

        Assert.Equal(1, page.Split("type=\"importmap\"").Length - 1);
        int map = page.IndexOf("type=\"importmap\"", StringComparison.Ordinal);
        int preload = page.IndexOf("rel=\"modulepreload\"", StringComparison.Ordinal);
        Assert.InRange(map, 0, preload);
    }

    [Fact]
    public void TheGamesOwnTags_Win()
    {
        using var directory = new TestDirectory();

        string page = Generate(directory, """
            <html><head><title>Theirs</title><meta name="viewport" content="theirs">
            <link rel="modulepreload" href="_dotgame/game#[.{fingerprint}].js"></head>
            <body><canvas id="mine"></canvas></body></html>
            """);

        Assert.DoesNotContain("My Game", page, StringComparison.Ordinal);
        Assert.Equal(1, page.Split("name=\"viewport\"").Length - 1);
        Assert.Equal(1, page.Split("_dotgame/game#").Length - 1);
        Assert.DoesNotContain("id=\"canvas\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedStyle_ComesBeforeTheGames_WhichCanChangeIt()
    {
        using var directory = new TestDirectory();

        string page = Generate(directory, "<html><head><style>canvas { outline: red; }</style></head><body></body></html>");

        Assert.InRange(page.IndexOf("touch-action", StringComparison.Ordinal), 0, page.IndexOf("outline: red", StringComparison.Ordinal));
    }

    [Fact]
    public void LaunchScreen_GetsItsPlaceholders_AtTheStart()
    {
        using var directory = new TestDirectory();

        string page = Generate(directory, hasLaunchScreen: true);

        Assert.InRange(page.IndexOf("<!-- launch-screen:head -->", StringComparison.Ordinal), 0, page.IndexOf("<meta", StringComparison.Ordinal));
        Assert.Contains("<!-- launch-screen:body -->", page, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLaunchScreen_LeavesNoPlaceholders()
    {
        using var directory = new TestDirectory();

        Assert.DoesNotContain("launch-screen", Generate(directory), StringComparison.Ordinal);
    }

    [Fact]
    public void Preload_FindsAStemsFileAndPages_AndNotItsNeighbours()
    {
        using var directory = new TestDirectory();
        WriteContent(directory, "Fonts/base.spritefont", "Fonts/base_0.webp", "Fonts/base_1.webp", "Fonts/baseline.spritefont", "Shaders/glow.frag.glsl");

        string page = Generate(directory, preload: ["wwwroot\\Content\\Fonts\\base", "wwwroot\\Content\\Shaders\\glow"]);

        Assert.Contains("href=\"Content/Fonts/base#[.{fingerprint}].spritefont\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"Content/Fonts/base_0#[.{fingerprint}].webp\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"Content/Fonts/base_1#[.{fingerprint}].webp\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"Content/Shaders/glow.frag#[.{fingerprint}].glsl\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("baseline", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_IsLinked_OnlyIfTheGameHasOne_AndHasntLinkedIt()
    {
        using var directory = new TestDirectory();

        Assert.DoesNotContain("rel=\"manifest\"", Generate(directory), StringComparison.Ordinal);
        Assert.Contains("href=\"app#[.{fingerprint}].webmanifest\"", Generate(directory, manifest: "app.webmanifest"), StringComparison.Ordinal);
    }
}
