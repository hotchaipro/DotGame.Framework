// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.Tasks;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Tasks;

public sealed class WebPrecompressionTaskTests
{
    private const string Page = """
        <!DOCTYPE html>
        <html>
        <head>
            <link rel="preload" as="fetch" crossorigin="anonymous" href="Content/Data/levels.1a2b3c4d5e6f7a8b.pbon">
            <link rel="preload" as="image" href="Content/Textures/logo.0f1e2d3c4b5a6978.webp">
        </head>
        <body></body>
        </html>
        """;

    private static (bool Succeeded, RecordingBuildEngine Engine) Apply(
        TestDirectory directory,
        string format = "br")
    {
        var engine = new RecordingBuildEngine();
        var task = new _ApplyWebPrecompression
        {
            BuildEngine = engine,
            WebRoot = directory.Path_("wwwroot"),
            Page = "index.html",
            Format = format,
        };
        return (task.Execute(), engine);
    }

    private static void Write(
        TestDirectory directory,
        params string[] files)
    {
        foreach (string file in files)
        {
            directory.WriteFile(Path.Combine("wwwroot", file), "x");
        }
    }

    [Fact]
    public void ThePageNamesTheCopies_AndItsPreloadsFetchThem()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("wwwroot/index.html", Page);
        Write(directory,
            "Content/Data/levels.1a2b3c4d5e6f7a8b.pbon", "Content/Data/levels.1a2b3c4d5e.pbon.br",
            "Content/Textures/logo.0f1e2d3c4b5a6978.webp",
            "_framework/dotnet.native.abc123defg.wasm", "_framework/dotnet.native.abc123defg.wasm.br");

        var (succeeded, engine) = Apply(directory);

        Assert.True(succeeded, string.Join(" ", engine.Errors));
        string page = File.ReadAllText(directory.Path_("wwwroot", "index.html"));
        Assert.Contains("<meta name=\"dotgame-precompressed\" content=\"br\" data-extensions=\".pbon .wasm\">", page, StringComparison.Ordinal);
        Assert.Contains("href=\"Content/Data/levels.1a2b3c4d5e6f7a8b.pbon.br\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"Content/Textures/logo.0f1e2d3c4b5a6978.webp\"", page, StringComparison.Ordinal);

        // The SDK's copy, named by its own fingerprint, is renamed after its file
        Assert.True(File.Exists(directory.Path_("wwwroot", "Content", "Data", "levels.1a2b3c4d5e6f7a8b.pbon.br")));
        Assert.False(File.Exists(directory.Path_("wwwroot", "Content", "Data", "levels.1a2b3c4d5e.pbon.br")));
    }

    [Fact]
    public void PublishingAgain_TheSdksNewCopyReplacesTheOneRenamedBefore()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("wwwroot/index.html", Page);
        Write(directory,
            "Content/Shaders/wave.93c2dd7ebfebdc64.glsl", "Content/Shaders/wave.bmva0xifmj.glsl.br",
            "Content/Data/levels.1a2b3c4d5e6f7a8b.pbon", "Content/Data/levels.1a2b3c4d5e.pbon.br");
        Assert.True(Apply(directory).Succeeded);

        // The next publish into the same folder: the SDK writes its copy again, beside the renamed one
        directory.WriteFile("wwwroot/index.html", Page);
        directory.WriteFile(Path.Combine("wwwroot", "Content", "Shaders", "wave.bmva0xifmj.glsl.br"), "new");
        var (succeeded, engine) = Apply(directory);

        Assert.True(succeeded, string.Join(" ", engine.Errors));
        Assert.Equal("new", File.ReadAllText(directory.Path_("wwwroot", "Content", "Shaders", "wave.93c2dd7ebfebdc64.glsl.br")));
        Assert.False(File.Exists(directory.Path_("wwwroot", "Content", "Shaders", "wave.bmva0xifmj.glsl.br")));
    }

    [Fact]
    public void AHostThatReadsHeaders_GetsTheBrotliRule_Once()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("wwwroot/index.html", Page);
        directory.WriteFile("wwwroot/_headers", "/Content/*\n  Cache-Control: public, max-age=31536000, immutable\n");
        Write(directory, "_framework/a.abc123defg.wasm", "_framework/a.abc123defg.wasm.br");

        Assert.True(Apply(directory).Succeeded);
        Assert.True(Apply(directory).Succeeded);

        string headers = File.ReadAllText(directory.Path_("wwwroot", "_headers"));
        Assert.StartsWith("/Content/*\n", headers, StringComparison.Ordinal);
        Assert.Equal(2, headers.Split("/*.br\n  Content-Encoding: br\n").Length);
        Assert.Single(File.ReadAllText(directory.Path_("wwwroot", "index.html")).Split("dotgame-precompressed").Skip(1));
    }

    [Fact]
    public void TheFallback_HasNoHeaderRule()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("wwwroot/index.html", Page);
        Write(directory, "_framework/a.abc123defg.wasm", "_framework/a.abc123defg.wasm.gz");

        Assert.True(Apply(directory, "gz").Succeeded);

        Assert.Contains("content=\"gz\"", File.ReadAllText(directory.Path_("wwwroot", "index.html")), StringComparison.Ordinal);
        Assert.False(File.Exists(directory.Path_("wwwroot", "_headers")));
    }

    [Fact]
    public void AFileOfACompressedTypeWithoutACopy_FailsThePublish()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("wwwroot/index.html", Page);
        Write(directory, "_framework/a.abc123defg.wasm", "_framework/a.abc123defg.wasm.br", "_framework/b.hij456klmn.wasm");

        var (succeeded, engine) = Apply(directory);

        Assert.False(succeeded);
        Assert.Contains(engine.Errors, e => e.Contains("_framework/b.hij456klmn.wasm", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutCopies_ThePageIsLeftAsItIs()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("wwwroot/index.html", Page);
        Write(directory, "_framework/a.abc123defg.wasm");

        Assert.True(Apply(directory).Succeeded);

        Assert.Equal(Page, File.ReadAllText(directory.Path_("wwwroot", "index.html")));
        Assert.False(File.Exists(directory.Path_("wwwroot", "_headers")));
    }
}
