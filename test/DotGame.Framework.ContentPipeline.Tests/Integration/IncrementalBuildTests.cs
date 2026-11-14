// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.ContentPipeline.Testing;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Integration;

/// <summary>
/// The real targets on a temporary project with fake processors: a clean build, then a build with
/// nothing to do, then rebuilds. A rebuild's check is that the encoder ran for what changed and
/// for nothing else.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class IncrementalBuildTests
{
    internal const string VertexAndFragment = "[shader(\"vertex\")] Out vs() {}\n[shader(\"pixel\")] float4 ps() {}\n";

    internal static ContentProject EveryType()
    {
        var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.AddContent("ShaderContent", "glow", ".slang", VertexAndFragment);
        project.AddContent("SoundContent", "click", ".wav");
        project.AddContent("MusicContent", "theme", ".flac");
        project.AddContent("DataContent", "level", ".json");
        project.AddContent("DataContent", "blob", ".bin");
        return project;
    }

    [Fact]
    public void CleanBuild_WritesEveryOutputAndStagesIt()
    {
        using var project = EveryType();

        project.Build().ShouldSucceed();

        Assert.Equal(
            [
                "Content/blob.bin",
                "Content/click.flac",
                "Content/glow.frag.dxil",
                "Content/glow.frag.msl",
                "Content/glow.frag.spv",
                "Content/glow.vert.dxil",
                "Content/glow.vert.msl",
                "Content/glow.vert.spv",
                "Content/hero.ktx2",
                "Content/level.json",
                "Content/theme.opus",
            ],
            project.OutputFiles());
        foreach (string file in project.OutputFiles())
        {
            Assert.True(File.Exists(Path.Combine(project.StageDir, file.Replace('/', Path.DirectorySeparatorChar))), $"'{file}' wasn't copied to the output folder.");
        }

        Assert.Equal("source", project.ReadOutput("Content/level.json"));
        Assert.Equal("source", project.ReadOutput("Content/blob.bin"));
    }

    [Fact]
    public void CleanBuild_WritesTheManifest()
    {
        using var project = EveryType();

        project.Build().ShouldSucceed();

        List<ManifestEntry> entries = project.Manifest();
        ManifestEntry Single(string key, string subKey = "") => Assert.Single(entries, entry => (entry.Key == key) && (entry.SubKey == subKey));
        Assert.Equal("hero.ktx2", Single("hero").Path);
        Assert.Equal("click.flac", Single("click").Path);
        Assert.Equal("theme.opus", Single("theme").Path);
        Assert.Equal("level.json", Single("level.json").Path);
        Assert.Equal("blob.bin", Single("blob.bin").Path);
        Assert.Equal("blob.bin", Single("blob.bin").Path);
        Assert.Equal("glow.frag.spv", Single("glow", "frag.spv").Path);
        Assert.Equal("glow.vert.dxil", Single("glow", "vert.dxil").Path);
        Assert.Contains("\"PremultipliedAlpha\", \"true\"", Single("hero").Properties, StringComparison.Ordinal);
    }

    [Fact]
    public void Music_IsReadForTheManifest_ButSoundsAreNot()
    {
        // P14: a sound's format has no reader in the manifest. Music's is read from its encoded file,
        // in the build, with no tool
        using var project = EveryType();

        BuildResult build = project.Build().ShouldSucceed();

        Assert.Single(build.CallsOf("music"));
        List<ManifestEntry> entries = project.Manifest();
        Assert.Contains("\"SampleRate\", \"44100\"", Assert.Single(entries, entry => entry.Key == "theme").Properties, StringComparison.Ordinal);
        Assert.Contains("\"DurationSeconds\", \"1.5\"", Assert.Single(entries, entry => entry.Key == "theme").Properties, StringComparison.Ordinal);
        Assert.Equal("", Assert.Single(entries, entry => entry.Key == "click").Properties);
    }

    [Fact]
    public void NoOpBuild_RunsNothingAndTouchesNothing()
    {
        using var project = EveryType();
        project.Build().ShouldSucceed();
        var outputs = project.SnapshotOutputs();
        DateTime manifestTime = File.GetLastWriteTimeUtc(project.ManifestFile);

        BuildResult second = project.Build().ShouldSucceed();

        Assert.Empty(second.Calls);
        Assert.Equal(outputs, project.SnapshotOutputs());
        Assert.Equal(manifestTime, File.GetLastWriteTimeUtc(project.ManifestFile));
        Assert.DoesNotContain("Processing texture assets", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignTimeBuild_RunsNoEncoderAndKeepsTheManifest()
    {
        using var project = EveryType();
        project.Build().ShouldSucceed();
        string manifest = File.ReadAllText(project.ManifestFile);
        project.WriteSource("src/hero.png", "changed");

        BuildResult designTime = project.Run(["DesignTimeBuild=true"], "BuildContent").ShouldSucceed();

        Assert.Empty(designTime.Calls);
        Assert.Equal(manifest, File.ReadAllText(project.ManifestFile));
    }
}

/// <summary>
/// A changed source: the encoder runs for that item and for nothing else.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class ChangedSourceTests
{
    private const string VertexAndFragment = IncrementalBuildTests.VertexAndFragment;

    private static ContentProject EveryType() => IncrementalBuildTests.EveryType();

    public static TheoryData<string, string, string> ChangedSources => new()
    {
        { "texture", "src/hero.png", "Content/hero.ktx2" },
        { "shader", "src/glow.slang", "Content/glow.frag.spv" },
        { "sound", "src/click.wav", "Content/click.flac" },
        { "music", "src/theme.flac", "Content/theme.opus" },
    };

    [Theory]
    [MemberData(nameof(ChangedSources))]
    public void ChangedSource_ReencodesThatItemOnly(string kind, string source, string output)
    {
        using var project = EveryType();
        project.Build().ShouldSucceed();
        string before = project.ReadOutput(output);

        project.WriteSource(source, VertexAndFragment + "// changed");
        BuildResult rebuild = project.Build().ShouldSucceed();

        ToolCall call = Assert.Single(rebuild.Calls);
        Assert.Equal(kind, call.Kind);
        Assert.NotEqual(before, project.ReadOutput(output));
        Assert.Empty(project.Build().ShouldSucceed().Calls);
    }

    [Theory]
    [InlineData("src/level.json", "Content/level.json")]
    [InlineData("src/blob.bin", "Content/blob.bin")]
    public void ChangedDataOrRawSource_IsCopiedAgain(string source, string output)
    {
        using var project = EveryType();
        project.Build().ShouldSucceed();

        project.WriteSource(source, "changed");
        BuildResult rebuild = project.Build().ShouldSucceed();

        Assert.Empty(rebuild.Calls);
        Assert.Equal("changed", project.ReadOutput(output));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(project.StageDir, output.Replace('/', Path.DirectorySeparatorChar))));
    }
}

/// <summary>
/// A changed setting: the encoder runs again, with the new setting.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class ChangedSettingTests
{
    [Fact]
    public void ChangedCompression_ReencodesTheTexture()
    {
        using var project = new ContentProject();
        ContentItem texture = project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();
        string before = project.ReadOutput("Content/hero.ktx2");

        texture.With("Compression", "Lossy");
        BuildResult rebuild = project.Build().ShouldSucceed();

        Assert.Equal("texture", Assert.Single(rebuild.Calls).Kind);
        Assert.Contains("--compression Lossy", Assert.Single(rebuild.Calls).Arguments, StringComparison.Ordinal);
        Assert.NotEqual(before, project.ReadOutput("Content/hero.ktx2"));
    }

    [Fact]
    public void ChangedQuality_ReencodesALossyTexture()
    {
        using var project = new ContentProject();
        ContentItem texture = project.AddContent("TextureContent", "hero", ".png").With("Compression", "Lossy").With("LossyQuality", "5");
        project.Build().ShouldSucceed();

        texture.With("LossyQuality", "6");
        BuildResult rebuild = project.Build().ShouldSucceed();

        Assert.Contains("--quality 6", Assert.Single(rebuild.Calls).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedPremultiplyAlpha_ReencodesTheTextureAndUpdatesTheManifest()
    {
        using var project = new ContentProject();
        ContentItem texture = project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();

        texture.With("PremultiplyAlpha", "false");
        BuildResult rebuild = project.Build().ShouldSucceed();

        Assert.Contains("--premultiply false", Assert.Single(rebuild.Calls).Arguments, StringComparison.Ordinal);
        Assert.Contains("\"PremultipliedAlpha\", \"false\"", Assert.Single(project.Manifest()).Properties, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedGpuCompression_MovesTheTextureToItsFormat()
    {
        using var project = new ContentProject();
        project.Properties["TextureContentGpuProcessorCommand"] = ContentProject.Command("texture") + " --gpu";
        project.Properties["TextureContentGpuProcessorOutputExtension"] = ".astc.ktx2";
        ContentItem texture = project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();

        texture.With("GpuCompression", "true");
        BuildResult rebuild = project.Build().ShouldSucceed();

        Assert.Contains("--gpu", Assert.Single(rebuild.Calls).Arguments, StringComparison.Ordinal);
        Assert.Equal(["Content/hero.astc.ktx2"], project.OutputFiles());
    }

    [Fact]
    public void ChangedTextureCommand_ReencodesTheTexture()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();

        project.Properties["TextureContentLosslessProcessorCommand"] += " --new-setting";
        Assert.Contains("--new-setting", Assert.Single(project.Build().ShouldSucceed().Calls).Arguments, StringComparison.Ordinal);
    }
}

/// <summary>
/// Changed processor dependencies, content versions and build tools.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class ChangedToolTests
{
    public static TheoryData<string, string, string, string> ProcessorDeps => new()
    {
        { "TextureContent", "TextureContentLosslessProcessorDeps", ".png", "texture" },
        { "SoundContent", "SoundContentLosslessProcessorDeps", ".wav", "sound" },
        { "MusicContent", "MusicContentProcessorDeps", ".flac", "music" },
        { "ShaderContent", "ShaderContentProcessorDeps", ".slang", "shader" },
    };

    [Theory]
    [MemberData(nameof(ProcessorDeps))]
    public void ChangedProcessorDep_Reencodes_SkippingTheOldCacheEntry(string itemType, string property, string extension, string kind)
    {
        using var project = new ContentProject();
        project.AddContent(itemType, "item", extension);
        project.Properties[property] = project.WriteSource("encoder-script.txt", "v1");
        project.Build().ShouldSucceed();
        Assert.Empty(project.Build().ShouldSucceed().Calls);

        project.WriteSource("encoder-script.txt", "v2");

        Assert.Contains(project.Build().ShouldSucceed().Calls, call => call.Kind == kind);
    }

    public static TheoryData<string, string, string, string> VersionedTypes => new()
    {
        { "TextureContent", ".png", "ContentPipelineTextureVersion", "texture" },
        { "ShaderContent", ".slang", "ContentPipelineShaderVersion", "shader" },
        { "SoundContent", ".wav", "ContentPipelineSoundVersion", "sound" },
        { "MusicContent", ".flac", "ContentPipelineMusicVersion", "music" },
    };

    [Theory]
    [MemberData(nameof(VersionedTypes))]
    public void BumpedContentVersion_ReencodesThatType_NotServedFromTheCache(string itemType, string extension, string versionProperty, string kind)
    {
        using var project = new ContentProject();
        project.AddContent(itemType, "item", extension);
        project.Build().ShouldSucceed();

        string versions = File.ReadAllText(project.VersionsFile);
        File.WriteAllText(project.VersionsFile, System.Text.RegularExpressions.Regex.Replace(versions, $"<{versionProperty}>\\d+<", $"<{versionProperty}>999<"));

        Assert.Contains(project.Build().ShouldSucceed().Calls, call => call.Kind == kind);
    }

    [Fact]
    public void ChangedBuildTool_RerunsTheTargets_ButIsServedFromTheCache()
    {
        // The tool's bytes aren't in the key (ContentVersions.props are), so unrelated tool
        // changes keep the cache
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();

        File.WriteAllText(project.ToolFile, "a new build of the tool");
        BuildResult rebuild = project.Build().ShouldSucceed();

        Assert.Empty(rebuild.Calls);
        Assert.Contains("[cache hit]", rebuild.Output, StringComparison.Ordinal);
        Assert.Empty(project.Build().ShouldSucceed().Calls);
    }

    [Fact]
    public void AddedItem_EncodesThatItemOnly()
    {
        using var project = new ContentProject();
        project.AddContent("TextureContent", "hero", ".png");
        project.Build().ShouldSucceed();

        project.AddContent("TextureContent", "villain", ".png", "other");
        ToolCall call = Assert.Single(project.Build().ShouldSucceed().Calls);

        Assert.Equal("villain.png", call.Input);
        Assert.Equal(2, project.Manifest().Count);
    }
}

/// <summary>
/// Changes that leave stale output with no message: review finding P2.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class StaleOutputKnownIssueTests
{
    private const string VertexAndFragment = IncrementalBuildTests.VertexAndFragment;

    [Fact]
    public void ChangedShaderImport_RebuildsTheShader()
    {
        using var project = new ContentProject();
        project.WriteSource("src/common.slang", "// shared");
        project.AddContent("ShaderContent", "glow", ".slang", "import common;\n" + VertexAndFragment);
        project.Build().ShouldSucceed();

        project.WriteSource("src/common.slang", "// changed");

        Assert.Equal("shader", Assert.Single(project.Build().ShouldSucceed().Calls).Kind);
    }

    [Fact]
    public void ChangedMusicQuality_ReencodesTheTrack()
    {
        using var project = new ContentProject();
        ContentItem music = project.AddContent("MusicContent", "theme", ".flac").With("LossyQuality", "5");
        project.Build().ShouldSucceed();

        music.With("LossyQuality", "9");

        Assert.Contains("--quality 9", Assert.Single(project.Build().ShouldSucceed().CallsOf("music")).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedMusicCommand_ReencodesTheTrack()
    {
        using var project = new ContentProject();
        project.AddContent("MusicContent", "theme", ".flac");
        project.Build().ShouldSucceed();

        project.Properties["MusicContentProcessorCommand"] += " --new-setting";

        Assert.Contains("--new-setting", Assert.Single(project.Build().ShouldSucceed().CallsOf("music")).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedSoundSampleRate_ReencodesInThatBuild()
    {
        using var project = new ContentProject();
        project.AddContent("SoundContent", "click", ".wav");
        project.Build().ShouldSucceed();

        project.Properties["SoundContentSampleRate"] = "22050";

        Assert.Contains("--rate 22050", Assert.Single(project.Build().ShouldSucceed().Calls).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedShaderCommand_Recompiles()
    {
        using var project = new ContentProject();
        project.AddContent("ShaderContent", "glow", ".slang", VertexAndFragment);
        project.Build().ShouldSucceed();

        project.Properties["ShaderContentProcessorCommand"] += " --new-setting";

        Assert.Contains("--new-setting", Assert.Single(project.Build().ShouldSucceed().Calls).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradedTool_Reencodes()
    {
        // A processor command that names an external tool (ContentExtraToolPaths) re-encodes when it changes
        using var project = new ContentProject();
        string tool = project.WriteSource("tools/encoder.exe", "version 1");
        project.Properties["ContentExtraToolPaths"] = $"\"{tool}\"";
        project.Properties["SoundContentFlacProcessorCommand"] += $" --encoder \"{tool}\"";
        project.AddContent("SoundContent", "click", ".wav");
        project.Build().ShouldSucceed();

        project.WriteSource("tools/encoder.exe", "version 2");

        Assert.Equal("sound", Assert.Single(project.Build().ShouldSucceed().Calls).Kind);
    }

    [Fact]
    public void ShaderLosingItsVertexStage_ToACacheHit_LeavesNoStaleStage()
    {
        using var project = new ContentProject();
        const string FragmentOnly = "[shader(\"pixel\")] float4 ps() {}\n";

        // Puts the fragment-only form in the cache
        project.AddContent("ShaderContent", "glow", ".slang", FragmentOnly);
        project.Build().ShouldSucceed();
        project.WriteSource("src/glow.slang", VertexAndFragment);
        project.Build().ShouldSucceed();
        Assert.True(File.Exists(project.OutputPath("Content/glow.vert.spv")));

        project.WriteSource("src/glow.slang", FragmentOnly);
        project.Build().ShouldSucceed();

        Assert.False(File.Exists(project.OutputPath("Content/glow.vert.spv")));
    }
}
