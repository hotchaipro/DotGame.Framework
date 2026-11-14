// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using DotGame.ContentPipeline.Testing;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Integration;

/// <summary>
/// Encoders that fail, and builds that are killed: the next build must not pass by accident, a
/// partial output must not reach the cache, and a lock left behind must not stall anyone.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class FailedBuildTests
{
    private const string Shader = "[shader(\"vertex\")] Out vs() {}\n[shader(\"pixel\")] float4 ps() {}\n";

    private static bool CacheHasEntries(ContentProject project) =>
        Directory.Exists(project.CacheDir)
        && Array.Exists(Directory.GetDirectories(project.CacheDir), partition => Directory.GetFileSystemEntries(partition, "*").Any(entry => !entry.EndsWith(".lock", StringComparison.Ordinal)));

    [Theory]
    [InlineData("TextureContent", ".png", "texture")]
    [InlineData("SoundContent", ".wav", "sound")]
    [InlineData("MusicContent", ".flac", "music")]
    [InlineData("ShaderContent", ".slang", "shader")]
    public void FailedEncoder_FailsTheBuild_AndTheNextBuildFailsAgain(string itemType, string extension, string kind)
    {
        using var project = new ContentProject();
        project.AddContent(itemType, "item", extension, Shader);
        project.SetToolConfig($"fail.{kind}", "exit");

        BuildResult first = project.Build().ShouldFail();
        Assert.Contains("FAKE0001", first.Output, StringComparison.Ordinal);
        project.Build().ShouldFail();

        Assert.False(CacheHasEntries(project), "A failed encode reached the cache.");
    }

    [Theory]
    [InlineData("TextureContent", ".png", "texture", "Content/item.ktx2")]
    [InlineData("SoundContent", ".wav", "sound", "Content/item.flac")]
    [InlineData("ShaderContent", ".slang", "shader", "Content/item.frag.spv")]
    public void FixedEncoder_BuildsAfterAFailure(string itemType, string extension, string kind, string output)
    {
        using var project = new ContentProject();
        project.AddContent(itemType, "item", extension, Shader);
        project.SetToolConfig($"fail.{kind}", "exit");
        project.Build().ShouldFail();

        project.SetToolConfig($"fail.{kind}", null);
        project.Build().ShouldSucceed();

        Assert.StartsWith("FAKE", project.ReadOutput(output), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownLosslessFormatOnASound_FailsTheBuild()
    {
        using var project = new ContentProject();
        project.AddContent("SoundContent", "click", ".wav").With("LosslessFormat", "OGG");

        BuildResult build = project.Build().ShouldFail();

        Assert.Contains("SND0002", build.Output, StringComparison.Ordinal);
        Assert.Contains("click", build.Output, StringComparison.Ordinal);
        Assert.Empty(build.Calls);
    }

    [Fact]
    public void FailedEncoder_FailsTheBuildBeforeTheOutputsAreStaged()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.SetToolConfig("fail.texture", "partial");

        project.Build().ShouldFail();

        Assert.False(File.Exists(Path.Combine(project.StageDir, "Content", "hero.ktx2")));
        Assert.False(CacheHasEntries(project), "A partial output reached the cache.");
    }

    [Fact]
    public void PartialOutputOfAFailedEncode_NeverReachesALaterCleanBuild()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.SetToolConfig("fail.texture", "partial");
        project.Build().ShouldFail();

        // A clean clone with the same cache: it has to encode again, and gets the whole output
        project.DeleteBuildOutputs();
        project.SetToolConfig("fail.texture", null);
        BuildResult clean = project.Build().ShouldSucceed();

        Assert.Equal("texture", Assert.Single(clean.Calls).Kind);
        Assert.StartsWith("FAKE", project.ReadOutput("Content/hero.ktx2"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TextureContent", ".png", "texture")]
    [InlineData("SoundContent", ".wav", "sound")]
    [InlineData("MusicContent", ".flac", "music")]
    public void EncoderFailingWithPartialOutput_FailsTheNextBuildToo(string itemType, string extension, string kind)
    {
        using var project = new ContentProject();
        project.AddContent(itemType, "item", extension);
        project.SetToolConfig($"fail.{kind}", "partial");

        project.Build().ShouldFail();

        project.Build().ShouldFail();
    }

    [Fact]
    public void ShaderFailingAfterItsPrimaryOutput_FailsTheNextBuildToo()
    {
        using var project = new ContentProject();
        project.AddContent("ShaderContent", "glow", ".slang", Shader);
        project.SetToolConfig("fail.shader", "after-primary");

        project.Build().ShouldFail();

        project.Build().ShouldFail();
    }

    [Fact]
    public void KilledBuild_DoesNotShipItsPartialOutput()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.SetToolConfig("fail.texture", "hang");
        KillInTheMiddleOfAnEncode(project);

        project.SetToolConfig("fail.texture", null);
        project.Build().ShouldSucceed();

        Assert.StartsWith("FAKE", project.ReadOutput("Content/hero.ktx2"), StringComparison.Ordinal);
    }

    [Fact]
    public void KilledBuild_LeavesNoPoisonedCacheEntryAndNoStall()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");

        // Far longer than the build's own limit, so a stall on the dead build's lock can't pass
        project.Properties["ContentCacheLockTimeoutMilliseconds"] = "3600000";
        project.SetToolConfig("fail.texture", "hang");
        KillInTheMiddleOfAnEncode(project);
        Assert.False(CacheHasEntries(project), "A killed encode reached the cache.");

        // The same source again, from a clean output folder
        project.DeleteBuildOutputs();
        project.SetToolConfig("fail.texture", null);
        project.Build().ShouldSucceed();

        Assert.StartsWith("FAKE", project.ReadOutput("Content/hero.ktx2"), StringComparison.Ordinal);
        Assert.True(CacheHasEntries(project));
    }

    [Fact]
    public void RetryInTheSameProcess_DoesNotWaitOnItsOwnFailedEncodesLock()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.Properties["ContentCacheLockTimeoutMilliseconds"] = "180000";
        project.SetToolConfig("fail.texture", "exit");

        // Two builds of the project in one MSBuild process, as a node or Visual Studio does
        string retry = project.WriteSource("Retry.proj", $"""
            <Project DefaultTargets="Retry">
              <Target Name="Retry">
                <MSBuild Projects="{Path.Combine(project.ProjectDir, "TestGame.proj")}" Properties="Attempt=1" ContinueOnError="true" />
                <MSBuild Projects="{Path.Combine(project.ProjectDir, "TestGame.proj")}" Properties="Attempt=2" ContinueOnError="true" />
              </Target>
            </Project>
            """);
        project.Build("_BuildContentPipelineTasks").ShouldSucceed();

        var clock = Stopwatch.StartNew();
        ProcessRunner.Run(Paths.DotNet, ["msbuild", retry, "-nologo", "-nodeReuse:false", "-maxCpuCount:1", "-terminalLogger:off"], project.ProjectDir);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(120), $"The retry took {clock.Elapsed}: it waited for the lock its own failed encode left.");
    }

    private static void KillInTheMiddleOfAnEncode(ContentProject project)
    {
        var output = new ConcurrentQueue<string>();
        using Process build = project.StartBuild(output);
        try
        {
            project.WaitForFile("hang.started", build, output);
        }
        finally
        {
            build.Kill(entireProcessTree: true);
            build.WaitForExit();
        }
    }
}
