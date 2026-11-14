// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.ContentPipeline.Testing;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Integration;

/// <summary>
/// The content cache in real builds: a clean build restores what an earlier build encoded, byte
/// for byte, with no encoder. Where the cache lives by default isn't tested: these set it.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class CacheBuildTests
{
    private const string Shader = "[shader(\"vertex\")] Out vs() {}\n[shader(\"pixel\")] float4 ps() {}\n";

    private static ContentProject Project()
    {
        var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.AddContent("ShaderContent", "glow", ".slang", Shader);
        project.AddContent("SoundContent", "click", ".wav");
        project.AddContent("MusicContent", "theme", ".flac");
        return project;
    }

    [Fact]
    public void CleanBuild_RestoresFromTheCache_ByteIdentical_WithNoEncoder()
    {
        using var project = Project();
        project.Build().ShouldSucceed();
        Dictionary<string, string> hashes = project.SnapshotOutputs().ToDictionary(file => file.Key, file => file.Value.Hash);

        project.DeleteBuildOutputs();
        BuildResult restored = project.Build().ShouldSucceed();

        Assert.Empty(restored.Calls);
        Assert.Equal(hashes, project.SnapshotOutputs().ToDictionary(file => file.Key, file => file.Value.Hash));
    }

    [Fact]
    public void RestoredBuild_IsAlreadyUpToDate()
    {
        using var project = Project();
        project.Build().ShouldSucceed();
        project.DeleteBuildOutputs();
        project.Build().ShouldSucceed();

        BuildResult noOp = project.Build().ShouldSucceed();

        Assert.Empty(noOp.Calls);
        Assert.DoesNotContain("[cache hit]", noOp.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACacheIsSharedByProjects_WhateverTheirPaths()
    {
        using var first = new ContentProject();
        first.AddContent("TextureContent", "hero", ".png");
        first.Build().ShouldSucceed();
        string encoded = first.ReadOutput("Content/hero.ktx2");

        using var second = new ContentProject("elsewhere");
        second.Properties["ContentCacheDir"] = first.CacheDir;

        // Its own command text differs only in the fake tool's path, which is the same
        second.AddContent("TextureContent", "hero", ".png");
        BuildResult restored = second.Build().ShouldSucceed();

        Assert.Empty(restored.Calls);
        Assert.Equal(encoded, second.ReadOutput("Content/hero.ktx2"));
    }

    [Fact]
    public void RevertedSource_IsServedFromTheCache()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png", "first");
        project.Build().ShouldSucceed();
        string first = project.ReadOutput("Content/hero.ktx2");
        project.WriteSource("src/hero.png", "second");
        project.Build().ShouldSucceed();

        project.WriteSource("src/hero.png", "first");
        BuildResult reverted = project.Build().ShouldSucceed();

        Assert.Empty(reverted.Calls);
        Assert.Equal(first, project.ReadOutput("Content/hero.ktx2"));
    }

    [Fact]
    public void DifferentQuality_DoesNotHitTheOtherEntry()
    {
        using var project = new ContentProject();
        ContentItem texture = project.AddContent("TextureContent", "hero", ".png").With("Compression", "Lossy").With("LossyQuality", "5");
        project.Build().ShouldSucceed();
        string five = project.ReadOutput("Content/hero.ktx2");

        texture.With("LossyQuality", "6");
        project.Build().ShouldSucceed();
        string six = project.ReadOutput("Content/hero.ktx2");
        Assert.NotEqual(five, six);

        texture.With("LossyQuality", "5");
        BuildResult back = project.Build().ShouldSucceed();
        Assert.Empty(back.Calls);
        Assert.Equal(five, project.ReadOutput("Content/hero.ktx2"));
    }

    [Fact]
    public void EmptyCacheEntry_IsAMiss_AndEncodesAgain()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();
        string entry = Assert.Single(Directory.GetFiles(project.CacheDir, "*.ktx2", SearchOption.AllDirectories));
        File.WriteAllBytes(entry, []);

        project.DeleteBuildOutputs();
        BuildResult rebuilt = project.Build().ShouldSucceed();

        Assert.Equal("texture", Assert.Single(rebuilt.Calls).Kind);
        Assert.StartsWith("FAKE", project.ReadOutput("Content/hero.ktx2"), StringComparison.Ordinal);
        Assert.NotEqual(0, new FileInfo(entry).Length);
    }

    [Fact]
    public void DefaultCache_IsInTheDotGameFolder_WhichIgnoresItself_AndAnEditedRuleStays()
    {
        using var project = new ContentProject { UseDefaultCacheFolder = true };
        Directory.CreateDirectory(Path.Combine(project.ProjectDir, ".git"));
        project.AddContent("TextureContent", "hero", ".png");

        project.Build().ShouldSucceed();

        string folder = Path.Combine(project.ProjectDir, ".dotgame");
        string ignoreFile = Path.Combine(folder, ".gitignore");
        Assert.Single(Directory.GetFiles(Path.Combine(folder, "contentcache"), "*.ktx2", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(folder, "contentcache", ".gitignore")));
        Assert.Contains("\n*\n", File.ReadAllText(ignoreFile), StringComparison.Ordinal);

        File.WriteAllText(ignoreFile, "edited");
        project.Build("PurgeContentCache").ShouldSucceed();
        project.DeleteBuildOutputs();
        project.Build().ShouldSucceed();
        Assert.Equal("edited", File.ReadAllText(ignoreFile));
    }

    [Fact]
    public void ShaderThatImports_IsNotCached()
    {
        // Its key would cover only its own source, so a change to the import would be served stale
        using var project = new ContentProject();
        project.WriteSource("src/common.slang", "// shared");
        project.AddContent("ShaderContent", "glow", ".slang", "import common;\n" + Shader);
        project.Build().ShouldSucceed();

        project.DeleteBuildOutputs();

        Assert.Equal("shader", Assert.Single(project.Build().ShouldSucceed().Calls).Kind);
    }

    [Fact]
    public void DisabledCache_EncodesEveryCleanBuild_AndWritesNoCache()
    {
        using var project = new ContentProject();
        project.Properties["ContentCacheEnabled"] = "false";
        project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();

        project.DeleteBuildOutputs();
        BuildResult second = project.Build().ShouldSucceed();

        Assert.Equal("texture", Assert.Single(second.Calls).Kind);
        Assert.False(Directory.Exists(project.CacheDir));
    }

    [Fact]
    public void Purge_RemovesTheCache()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();

        project.Build("PurgeContentCache").ShouldSucceed();

        Assert.False(Directory.Exists(project.CacheDir));
    }

    [Fact]
    public void CleanTarget_RemovesOutputsAndStagedContent_AndKeepsTheCache()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();
        Assert.True(File.Exists(Path.Combine(project.StageDir, "Content", "hero.ktx2")));

        project.Build("Clean").ShouldSucceed();

        Assert.Empty(project.OutputFiles());
        Assert.False(File.Exists(Path.Combine(project.StageDir, "Content", "hero.ktx2")));
        Assert.False(Directory.Exists(project.ContentIntermediateDir));
        Assert.True(Directory.Exists(project.CacheDir));
        Assert.Empty(project.Build().ShouldSucceed().Calls);
    }
}
