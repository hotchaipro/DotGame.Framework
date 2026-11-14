// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.ContentPipeline.Testing;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Integration;

/// <summary>
/// Where outputs go: <c>Link</c>, pruning what's no longer produced, items that collide, and paths
/// with spaces and other characters.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class OutputLayoutTests
{
    private const string Shader = "[shader(\"vertex\")] Out vs() {}\n[shader(\"pixel\")] float4 ps() {}\n";

    [Fact]
    public void Link_ChoosesTheOutputFolderAndTheManifestKey()
    {
        using var project = new ContentProject();
        project.WriteSource("art/deep/hero.png", "source");
        project.AddItem("TextureContent", "art/deep/hero.png", "Content\\Sprites\\hero.png");

        project.Build().ShouldSucceed();

        Assert.Equal(["Content/Sprites/hero.ktx2"], project.OutputFiles());
        ManifestEntry entry = Assert.Single(project.Manifest());
        Assert.Equal("Sprites/hero", entry.Key);
        Assert.Equal("Sprites/hero.ktx2", entry.Path);
        Assert.True(File.Exists(Path.Combine(project.StageDir, "Content", "Sprites", "hero.ktx2")));
    }

    [Fact]
    public void Link_WithForwardSlashes_ChoosesTheSameFolder()
    {
        using var project = new ContentProject();
        project.WriteSource("hero.png", "source");
        project.AddItem("TextureContent", "hero.png", "Content/Sprites/hero.png");

        project.Build().ShouldSucceed();

        Assert.Equal("Sprites/hero", Assert.Single(project.Manifest()).Key);
    }

    [Fact]
    public void Link_UnderWwwroot_DropsItFromTheManifest()
    {
        using var project = new ContentProject();
        project.WriteSource("hero.png", "source");
        project.AddItem("TextureContent", "hero.png", "wwwroot\\Content\\hero.png");

        project.Build().ShouldSucceed();

        Assert.Equal(["wwwroot/Content/hero.ktx2"], project.OutputFiles());
        Assert.Equal("hero", Assert.Single(project.Manifest()).Key);
    }

    [Fact]
    public void NoLink_UsesThePathBelowTheProject()
    {
        using var project = new ContentProject();
        project.WriteSource("Content/hero.png", "source");
        project.AddItem("TextureContent", "Content/hero.png");

        project.Build().ShouldSucceed();

        Assert.Equal(["Content/hero.ktx2"], project.OutputFiles());
        Assert.Equal("hero", Assert.Single(project.Manifest()).Key);
    }

    [Fact]
    public void ChangedLinkFolder_MovesTheOutput()
    {
        using var project = new ContentProject();
        ContentItem texture = project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();

        texture.With("Link", "Content\\Moved\\hero.png");
        project.Build().ShouldSucceed();

        Assert.Equal(["Content/Moved/hero.ktx2"], project.OutputFiles());
        Assert.Equal("Moved/hero", Assert.Single(project.Manifest()).Key);
    }

    [Fact]
    public void Link_ChoosesTheOutputFileNameToo()
    {
        using var project = new ContentProject();
        project.WriteSource("hero.png", "source");
        project.AddItem("TextureContent", "hero.png", "Content\\renamed.png");

        project.Build().ShouldSucceed();

        Assert.Equal(["Content/renamed.ktx2"], project.OutputFiles());
    }

    [Fact]
    public void RemovedItem_LosesItsOutputAndManifestEntry()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        ContentItem villain = project.AddContent("TextureContent", "villain", ".png", "other");
        project.Build().ShouldSucceed();

        project.RemoveItem(villain);
        BuildResult build = project.Build().ShouldSucceed();

        Assert.Equal(["Content/hero.ktx2"], project.OutputFiles());
        Assert.Contains("Removed stale content output", build.Output, StringComparison.Ordinal);
        Assert.Equal("hero", Assert.Single(project.Manifest()).Key);
    }

    [Fact]
    public void RemovedShader_LosesEveryStageFile()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        ContentItem shader = project.AddContent("ShaderContent", "glow", ".slang", Shader);
        project.Build().ShouldSucceed();

        project.RemoveItem(shader);
        project.Build().ShouldSucceed();

        Assert.Equal(["Content/hero.ktx2"], project.OutputFiles());
    }

    [Fact]
    public void Prune_RemovesFoldersItEmptied()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.WriteSource("deep.png", "source");
        ContentItem deep = project.AddItem("TextureContent", "deep.png", "Content\\a\\b\\deep.png");
        project.Build().ShouldSucceed();

        project.RemoveItem(deep);
        project.Build().ShouldSucceed();

        Assert.False(Directory.Exists(project.OutputPath("Content/a")));
    }

    [Fact]
    public void CustomContentType_AsTheReadmeHasIt_IsKept_AndInTheManifest()
    {
        using var project = new ContentProject();
        project.WriteSource("src/first.lvl", "level");
        project.ExtraProjectXml = """
              <PropertyGroup>
                <BuildContentDependsOn>$(BuildContentDependsOn);BuildLevels</BuildContentDependsOn>
              </PropertyGroup>
              <ItemGroup>
                <LevelContent Include="src/first.lvl" />
                <ContentOutputFile Include="@(LevelContent->'Content\%(Filename).level')" />
              </ItemGroup>
              <Target Name="BuildLevels">
                <Copy SourceFiles="@(LevelContent)" DestinationFiles="@(LevelContent->'$(ContentOutputDir)Content\%(Filename).level')" />
              </Target>
              <Target Name="AddLevelManifestEntries" BeforeTargets="BuildContentManifest">
                <ItemGroup>
                  <ContentManifestEntry Include="@(LevelContent->'Content\%(Filename)')" RelativePath="Content\%(Filename).level" />
                </ItemGroup>
              </Target>
            """;

        project.Build().ShouldSucceed();
        project.Build().ShouldSucceed();

        Assert.Contains("Content/first.level", project.OutputFiles());
        Assert.Equal("first.level", Assert.Single(project.Manifest(), entry => entry.Key == "first").Path);
    }

    [Fact]
    public void Prune_KeepsWhatTheCurrentItemsProduce_WithNoRebuild()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.AddContent("ShaderContent", "glow", ".slang", Shader);
        project.AddContent("DataContent", "level", ".json");
        project.Build().ShouldSucceed();
        var outputs = project.SnapshotOutputs();

        File.WriteAllText(project.OutputPath("Content/leftover.br"), "from an older pipeline");
        BuildResult build = project.Build().ShouldSucceed();

        Assert.Empty(build.Calls);

        // Nothing lists the leftover as the pipeline's, so it stays: a prune only removes what it knows it wrote
        var after = project.SnapshotOutputs();
        Assert.True(after.Remove("Content/leftover.br"));
        Assert.Equal(outputs, after);
    }

    [Fact]
    public void Prune_NeverDeletesAFileThePipelineDidNotWrite()
    {
        using var project = new ContentProject();
        project.Properties["ContentOutputDir"] = "assets/";
        project.AddContent("TextureContent", "hero", ".png");
        string mine = project.WriteSource("assets/notes.txt", "a developer's own file");

        project.Build().ShouldSucceed();

        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void Prune_KeepsACustomTargetsOutput()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.ExtraProjectXml = """
              <PropertyGroup>
                <BuildContentDependsOn>$(BuildContentDependsOn);WriteCustomContent</BuildContentDependsOn>
              </PropertyGroup>
              <Target Name="WriteCustomContent" DependsOnTargets="_ValidateContentPipeline">
                <WriteLinesToFile File="$(ContentOutputDir)Content/custom.bin" Lines="mine" Overwrite="true" />
              </Target>
            """;

        project.Build().ShouldSucceed();

        Assert.Contains("Content/custom.bin", project.OutputFiles());
    }

    [Fact]
    public void LosslessFormat_PicksEachSoundsFormat_FlacByDefault()
    {
        using var project = new ContentProject();
        // Sources of their own, so the shared content cache can't answer for them
        project.AddContent("SoundContent", "click", ".wav", "flac by default: click");
        project.AddContent("SoundContent", "pop", ".wav", "flac by default: pop").With("LosslessFormat", "Default");
        project.AddContent("SoundContent", "tap", ".wav", "flac by default: tap").With("LosslessFormat", "WAV");

        BuildResult build = project.Build().ShouldSucceed();

        Assert.Equal(["Content/click.flac", "Content/pop.flac", "Content/tap.wav"], project.OutputFiles());
        Assert.DoesNotContain("--wav", Assert.Single(build.Calls, call => call.Input == "click.wav").Arguments, StringComparison.Ordinal);
        Assert.Contains("--wav", Assert.Single(build.Calls, call => call.Input == "tap.wav").Arguments, StringComparison.Ordinal);
        List<ManifestEntry> entries = project.Manifest();
        Assert.Equal("click.flac", Assert.Single(entries, entry => entry.Key == "click").Path);
        Assert.Equal("tap.wav", Assert.Single(entries, entry => entry.Key == "tap").Path);
    }

    [Fact]
    public void ChangedLosslessFormat_ReencodesThatSound_AndRemovesItsOldOutput()
    {
        using var project = new ContentProject();
        ContentItem click = project.AddContent("SoundContent", "click", ".wav");
        project.AddContent("SoundContent", "tap", ".wav");
        project.Build().ShouldSucceed();

        click.With("LosslessFormat", "WAV");
        BuildResult rebuild = project.Build().ShouldSucceed();

        Assert.Equal("click.wav", Assert.Single(rebuild.Calls).Input);
        Assert.Equal(["Content/click.wav", "Content/tap.flac"], project.OutputFiles());
    }

    [Theory]
    [InlineData("DataContent", "foo", ".json", "DataContent", "FOO", ".json")]
    [InlineData("TextureContent", "foo", ".png", "TextureContent", "foo", ".jpg")]
    [InlineData("TextureContent", "foo", ".png", "SoundContent", "FOO", ".wav")]
    public void ItemsWithOneManifestKey_FailTheBuild_NamingThem(string firstType, string firstName, string firstExtension, string secondType, string secondName, string secondExtension)
    {
        using var project = new ContentProject();
        project.AddContent(firstType, firstName, firstExtension);
        project.AddContent(secondType, secondName, secondExtension);

        BuildResult build = project.Build().ShouldFail();

        Assert.Contains(firstName + firstExtension, build.Output, StringComparison.Ordinal);
        Assert.Contains(secondName + secondExtension, build.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemAboveTheProject_WithNoLink_StaysInTheOutputFolder_OrFails()
    {
        using var project = new ContentProject();
        project.WriteSource("../shared/foo.png", "source");
        project.AddItem("TextureContent", "../shared/foo.png");

        BuildResult build = project.Build();

        if (build.Succeeded)
        {
            Assert.DoesNotContain(project.Manifest(), entry => entry.Key.StartsWith("..", StringComparison.Ordinal));
            Assert.Single(project.OutputFiles());
        }
        else
        {
            Assert.Contains("Link", build.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ItemWithAnAbsolutePath_WithNoLink_StaysInTheOutputFolder_OrFails()
    {
        using var project = new ContentProject();
        string source = project.WriteSource("foo.png", "source");
        project.AddItem("TextureContent", source);

        BuildResult build = project.Build();

        if (build.Succeeded)
        {
            Assert.Single(project.OutputFiles());
            Assert.DoesNotContain(project.Manifest(), entry => Path.IsPathRooted(entry.Path));
        }
        else
        {
            // Said so, rather than failing on an invalid path
            Assert.Contains("Link", build.Output, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string, string> AwkwardFolders => new()
    {
        { "spaces", "My Game Project" },
        { "non-ASCII", "Jeu été 日本語 Ωmega" },
        { "symbols", "R&D (v1.2) #3" },
    };

    [Theory]
    [MemberData(nameof(AwkwardFolders))]
    public void AwkwardProjectFolder_BuildsEveryType_AndRestoresFromTheCache(string description, string folder)
    {
        Assert.NotNull(description);
        using var project = new ContentProject(folder);
        project.AddContent("TextureContent", "hero", ".png");
        project.AddContent("ShaderContent", "glow", ".slang", Shader);
        project.AddContent("SoundContent", "click", ".wav");
        project.AddContent("MusicContent", "theme", ".flac");
        project.AddContent("DataContent", "level", ".json");

        project.Build().ShouldSucceed();

        Assert.Contains("Content/hero.ktx2", project.OutputFiles());
        Assert.Contains("Content/glow.vert.dxil", project.OutputFiles());
        Assert.Contains(project.Manifest(), entry => entry.Key == "theme" && entry.Properties.Contains("DurationSeconds", StringComparison.Ordinal));
        Assert.Empty(project.Build().ShouldSucceed().Calls);

        project.DeleteBuildOutputs();
        Assert.Empty(project.Build().ShouldSucceed().Calls);
    }

    [Theory]
    [MemberData(nameof(AwkwardFolders))]
    public void AwkwardItemNames_BuildAndKeepTheirNamesInTheManifest(string description, string name)
    {
        Assert.NotNull(description);
        using var project = new ContentProject();
        project.WriteSource($"art/{name}/{name}.png", "source");

        // '%', ';' and the like need escaping in an item; these don't, nor do they need it in Link
        project.AddItem("TextureContent", $"art/{name}/{name}.png", $"Content\\{name}\\{name}.png");

        project.Build().ShouldSucceed();

        Assert.Equal([$"Content/{name}/{name}.ktx2"], project.OutputFiles());
        Assert.Equal($"{name}/{name}", Assert.Single(project.Manifest()).Key);
    }

    [Fact]
    public void CacheFolderWithSpaces_Works()
    {
        using var project = new ContentProject();
        project.Properties["ContentCacheDir"] = Path.Combine(project.Root, "my cache é");
        project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();

        project.DeleteBuildOutputs();

        Assert.Empty(project.Build().ShouldSucceed().Calls);
    }

    [Fact]
    public void ApostropheInTheProjectFolder_Builds()
    {
        // Real home folders have them (C:\Users\O'Brien); P24 feared the targets' quoting breaks
        using var project = new ContentProject("O'Brien's Game");
        project.AddContent("TextureContent", "hero", ".png");
        project.AddContent("ShaderContent", "glow", ".slang", Shader);

        project.Build().ShouldSucceed();

        Assert.Contains("Content/hero.ktx2", project.OutputFiles());
    }

    [Theory]
    [InlineData("Compression", "Bogus", "invalid Compression")]
    [InlineData("PremultiplyAlpha", "maybe", "invalid PremultiplyAlpha")]
    [InlineData("GpuCompression", "maybe", "invalid GpuCompression")]
    [InlineData("LosslessQuality", "11", "invalid LosslessQuality")]
    [InlineData("LosslessQuality", "high", "invalid LosslessQuality")]
    [InlineData("LossyQuality", "6", "sets the other compression's quality")]
    public void InvalidTextureMetadata_FailsTheBuild(string name, string value, string message)
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png").With(name, value);

        BuildResult build = project.Build().ShouldFail();

        Assert.Contains(message, build.Output, StringComparison.Ordinal);
        Assert.Empty(build.Calls);
    }

    [Fact]
    public void LosslessQualityOfALossyTexture_FailsTheBuild()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png").With("Compression", "Lossy").With("LosslessQuality", "6");

        BuildResult build = project.Build().ShouldFail();

        Assert.Contains("sets the other compression's quality", build.Output, StringComparison.Ordinal);
        Assert.Empty(build.Calls);
    }

    // Below 10, a lossless texture goes through the encoder that may change colors a little, with its quality
    [Theory]
    [InlineData(null, "--compression Lossless --premultiply")]
    [InlineData("10", "--compression Lossless --premultiply")]
    [InlineData("6", "--compression Lossless --quality 6 --premultiply")]
    public void LosslessQuality_PicksTheLosslessEncoding(string? quality, string arguments)
    {
        using var project = new ContentProject();
        ContentItem texture = project.AddContent("TextureContent", "hero", ".png");
        if (quality is not null)
        {
            texture.With("LosslessQuality", quality);
        }

        BuildResult build = project.Build().ShouldSucceed();

        ToolCall call = Assert.Single(build.Calls);
        Assert.Contains(arguments, call.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuCompressionOfALosslessTexture_FailsTheBuild()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png").With("GpuCompression", "true").With("Compression", "Lossless");

        Assert.Contains("GPU compressed formats are lossy", project.Build().ShouldFail().Output, StringComparison.Ordinal);
    }

    [Fact]
    public void DataKeepsItsExtension_SoItSitsBesideATextureOfTheSameName()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "foo", ".png");
        project.AddContent("DataContent", "foo", ".json");

        project.Build().ShouldSucceed();

        List<ManifestEntry> entries = project.Manifest();
        Assert.Contains(entries, entry => entry.Key == "foo" && entry.Path == "foo.ktx2");
        Assert.Contains(entries, entry => entry.Key == "foo.json" && entry.Path == "foo.json");
    }

    [Fact]
    public void MissingSource_FailsTheBuild()
    {
        using var project = new ContentProject();
        project.AddItem("TextureContent", "missing.png", "Content\\missing.png");

        project.Build().ShouldFail();
    }
}
