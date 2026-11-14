// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Tasks;

/// <summary>
/// The other tasks, run directly: the manifest, pruning, Link's folder, tokens and media probes.
/// </summary>
public sealed class PipelineTaskTests
{
    private const string SubKeys = "vert.spv=.vert.spv;frag.spv=.frag.spv";

    private static string Manifest(TestDirectory directory, ITaskItem[]? entries = null, ITaskItem[]? shaders = null, string? subKeys = null, string basePath = "Content/", string? properties = null)
    {
        string output = directory.Path_("Manifest.g.cs");
        var task = new _GenerateContentManifest
        {
            BuildEngine = new RecordingBuildEngine(),
            Entries = entries,
            ShaderEntries = shaders,
            ShaderSubKeys = subKeys,
            OutputCsFile = output,
            BasePath = basePath,
            ManifestIdentity = "Game",
            PropertyNames = properties,
        };
        Assert.True(task.Execute());
        return File.ReadAllText(output);
    }

    private static TaskItem Entry(string key, string path, params (string, string)[] metadata)
    {
        TaskItem item = TaskItems.Item(key, metadata);
        item.SetMetadata("RelativePath", path);
        return item;
    }

    [Fact]
    public void Manifest_StripsTheBasePath_FromKeysAndValues()
    {
        using var directory = new TestDirectory();

        string manifest = Manifest(directory, [Entry("Content\\Sprites\\hero", "Content\\Sprites\\hero.ktx2")]);

        Assert.Contains("manifest.Add(\"Sprites/hero\", \"Sprites/hero.ktx2\");", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_StripsAWebRootFirst()
    {
        using var directory = new TestDirectory();

        string manifest = Manifest(directory, [Entry("wwwroot\\Content\\hero", "wwwroot/Content/hero.webp")]);

        Assert.Contains("manifest.Add(\"hero\", \"hero.webp\");", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_LeavesAKeyOutsideTheBasePathWhole()
    {
        using var directory = new TestDirectory();

        string manifest = Manifest(directory, [Entry("Other\\hero", "Other\\hero.webp")]);

        Assert.Contains("manifest.Add(\"Other/hero\", \"Other/hero.webp\");", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_ExpandsShadersIntoASubKeyPerOutput()
    {
        using var directory = new TestDirectory();
        TaskItem shader = TaskItems.Item("src/glow.slang", ("OutputRelativeDir", "Content\\Effects\\"));

        string manifest = Manifest(directory, shaders: [shader], subKeys: SubKeys);

        Assert.Contains("manifest.Add(\"Effects/glow\", \"Effects/glow.frag.spv\", subKey: \"frag.spv\");", manifest, StringComparison.Ordinal);
        Assert.Contains("manifest.Add(\"Effects/glow\", \"Effects/glow.vert.spv\", subKey: \"vert.spv\");", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_OnSingleOutputPlatforms_HasNoSubKeys()
    {
        using var directory = new TestDirectory();

        string manifest = Manifest(directory, [Entry("Content\\glow", "Content\\glow.glsl")], [TaskItems.Item("src/glow.slang", ("OutputRelativeDir", "Content\\"))]);

        Assert.DoesNotContain("subKey", manifest, StringComparison.Ordinal);
        Assert.Contains("manifest.Add(\"glow\", \"glow.glsl\");", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_CopiesOnlyNamedNonEmptyProperties()
    {
        using var directory = new TestDirectory();

        string manifest = Manifest(
            directory,
            [Entry("Content\\a", "Content\\a.ogg", ("SampleRate", "44100"), ("Channels", ""), ("Other", "x"))],
            properties: "SampleRate;Channels");

        Assert.Contains("new(\"SampleRate\", \"44100\")", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("Channels", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("Other", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_OmitsThePropertiesOfAnEntryWithNone()
    {
        using var directory = new TestDirectory();

        Assert.DoesNotContain("properties:", Manifest(directory, [Entry("Content\\a", "Content\\a.ogg")], properties: "SampleRate"), StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_SortsItsEntries_SoItsTextIsStable()
    {
        using var directory = new TestDirectory();
        string forwards = Manifest(directory, [Entry("Content\\a", "Content\\a.x"), Entry("Content\\B", "Content\\B.x"), Entry("Content\\c", "Content\\c.x")]);

        string backwards = Manifest(directory, [Entry("Content\\c", "Content\\c.x"), Entry("Content\\B", "Content\\B.x"), Entry("Content\\a", "Content\\a.x")]);

        Assert.Equal(forwards, backwards);
    }

    [Fact]
    public void Manifest_EscapesItsStrings()
    {
        using var directory = new TestDirectory();

        string manifest = Manifest(directory, [Entry("a\"b", "a\"b.x")]);

        Assert.Contains("manifest.Add(\"a\\\"b\", \"a\\\"b.x\");", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_KeepsNonAsciiNames()
    {
        using var directory = new TestDirectory();

        Assert.Contains("manifest.Add(\"été/日本\", \"été/日本.x\");", Manifest(directory, [Entry("Content\\été\\日本", "Content\\été\\日本.x")]), StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_RegistersItselfWithTheRegistry_AsAModuleInitializer()
    {
        using var directory = new TestDirectory();

        string manifest = Manifest(directory);

        Assert.Contains("[ModuleInitializer]", manifest, StringComparison.Ordinal);
        Assert.Contains("ContentManifest(\"Game\", \"Content/\")", manifest, StringComparison.Ordinal);
        Assert.Contains("ContentManifest.Register(manifest)", manifest, StringComparison.Ordinal);
        Assert.StartsWith("// <auto-generated/>", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_DoesNotRewriteAnUnchangedFile()
    {
        // An unrelated build mustn't recompile the game
        using var directory = new TestDirectory();
        Manifest(directory, [Entry("Content\\a", "Content\\a.x")]);
        DateTime old = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(directory.Path_("Manifest.g.cs"), old);

        Manifest(directory, [Entry("Content\\a", "Content\\a.x")]);

        Assert.Equal(old, File.GetLastWriteTimeUtc(directory.Path_("Manifest.g.cs")));
    }

    [Fact]
    public void Manifest_FailsOnADuplicateKey_ComparedWithoutCase()
    {
        using var directory = new TestDirectory();
        var engine = new RecordingBuildEngine();
        var task = new _GenerateContentManifest
        {
            BuildEngine = engine,
            Entries = [Entry("Content\\foo", "Content\\foo.ktx2"), Entry("Content\\FOO", "Content\\FOO.json")],
            OutputCsFile = directory.Path_("Manifest.g.cs"),
            BasePath = "Content/",
            ManifestIdentity = "Game",
        };

        Assert.False(task.Execute());

        Assert.Contains(engine.Errors, error => error.Contains("foo.ktx2", StringComparison.Ordinal) && error.Contains("FOO.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Encode_FailsOnItemsWithOneOutput_ComparedWithoutCase_BeforeEncodingAny()
    {
        using var directory = new TestDirectory();
        string output = directory.Path_("out", "foo.ktx2");
        string input = directory.WriteFile("foo.png", "source");
        (string, string)[] metadata =
        [
            ("_StagedFile", directory.Path_("staged", "foo.ktx2")),
            ("_IntermediateDir", directory.Path_("intermediate")),
            ("_CommandTemplate", ContentProject.Command("texture")),
        ];
        var engine = new RecordingBuildEngine();
        var task = new _EncodeContentItems
        {
            BuildEngine = engine,
            Items =
            [
                TaskItems.Item(input, [("_OutputFile", output), .. metadata]),
                TaskItems.Item(directory.WriteFile("FOO.jpg", "source"), [("_OutputFile", directory.Path_("out", "FOO.ktx2")), .. metadata]),
            ],
        };

        Assert.False(task.Execute());

        Assert.Contains(engine.Errors, error => error.Contains("foo.png", StringComparison.Ordinal) && error.Contains("FOO.jpg", StringComparison.Ordinal));
        Assert.Empty(directory.ToolCalls());
    }

    private static List<string> Prune(TestDirectory directory, ITaskItem[]? files = null, ITaskItem[]? suffixGroups = null, ITaskItem[]? pageGroups = null, bool listEverything = true)
    {
        string root = directory.Path_("out");
        // As a last build would have left it: everything there now was the pipeline's
        string listFile = directory.Path_("outputs.list");
        if (listEverything && Directory.Exists(directory.Path_("out")))
        {
            File.WriteAllLines(listFile, Directory.GetFiles(directory.Path_("out"), "*", SearchOption.AllDirectories));
        }

        var task = new _PruneContentOutputs
        {
            ListFile = listFile,
            BuildEngine = new RecordingBuildEngine(),
            OutputDir = root,
            Files = files,
            SuffixGroups = suffixGroups,
            PageGroups = pageGroups,
        };
        Assert.True(task.Execute());
        return Directory.Exists(root)
            ? [.. Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(root, file).Replace('\\', '/')).Order(StringComparer.Ordinal)]
            : [];
    }

    [Fact]
    public void Prune_KeepsExactFiles_InAnyCaseOrSeparator()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("out/Content/Hero.ktx2", "x");
        directory.WriteFile("out/Content/stale.ktx2", "x");

        Assert.Equal(["Content/Hero.ktx2"], Prune(directory, TaskItems.Items("content\\hero.KTX2")));
    }

    [Fact]
    public void Prune_KeepsEachSuffix_AndSubKeyEntries()
    {
        using var directory = new TestDirectory();
        foreach (string name in new[] { "glow.frag.spv", "glow.vert.spv", "glow.frag.dxil", "glow.vert.msl", "other.frag.spv" })
        {
            directory.WriteFile($"out/{name}", "x");
        }

        List<string> kept = Prune(directory, suffixGroups: [TaskItems.Item("glow", ("Suffixes", ".frag.spv;vert.spv=.vert.spv;frag.dxil=.frag.dxil"))]);

        Assert.Equal(["glow.frag.dxil", "glow.frag.spv", "glow.vert.spv"], kept);
    }

    [Fact]
    public void Prune_KeepsNumberedPagesOfTheirGroup_AndNothingLikeThem()
    {
        using var directory = new TestDirectory();
        foreach (string name in new[] { "atlas_0.webp", "atlas_12.webp", "atlas_x.webp", "atlas_1.ktx2", "atlas2_0.webp", "a/atlas_0.webp" })
        {
            directory.WriteFile($"out/{name}", "x");
        }

        Assert.Equal(["atlas_0.webp", "atlas_12.webp"], Prune(directory, pageGroups: [TaskItems.Item("atlas", ("PageExtension", ".webp"))]));
    }

    [Fact]
    public void Prune_WithNothingDeclared_RemovesWhatTheLastBuildWrote()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("out/a.bin", "x");

        Assert.Empty(Prune(directory));
    }

    [Fact]
    public void Prune_NeverRemovesAFileTheLastBuildDidNotWrite()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("out/mine.txt", "x");

        Assert.Equal(["mine.txt"], Prune(directory, TaskItems.Items("a.bin"), listEverything: false));
    }

    [Fact]
    public void Prune_ListsWhatItProduced_ForTheNextBuild()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("out/a.bin", "x");
        directory.WriteFile("out/mine.txt", "x");

        Prune(directory, TaskItems.Items("a.bin"), listEverything: false);
        Assert.Equal(["a.bin", "mine.txt"], Prune(directory, TaskItems.Items("mine.txt"), listEverything: false).Prepend("a.bin").Distinct().Order());

        Assert.False(File.Exists(directory.Path_("out", "a.bin")));
    }

    [Fact]
    public void Prune_OfAMissingFolder_DoesNothing()
    {
        using var directory = new TestDirectory();

        Assert.Empty(Prune(directory, TaskItems.Items("a.bin")));
    }

    [Fact]
    public void Prune_RemovesFoldersItEmptied_DeepestFirst()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("out/keep.bin", "x");
        directory.WriteFile("out/a/b/c/stale.bin", "x");

        Prune(directory, TaskItems.Items("keep.bin"));

        Assert.False(Directory.Exists(directory.Path_("out", "a")));
    }

    private static ITaskItem Indexed(params (string Name, string Value)[] metadata)
    {
        return Indexed("hero.png", metadata);
    }

    private static ITaskItem Indexed(string spec, params (string Name, string Value)[] metadata)
    {
        var task = new _AddIndexMetadata { BuildEngine = new RecordingBuildEngine(), InputItems = [TaskItems.Item(spec, metadata)] };
        Assert.True(task.Execute());
        return task.OutputItems[0];
    }

    [Theory]
    [InlineData("Content\\Sprites\\hero.png", "Content\\Sprites\\")]
    [InlineData("Content/Sprites/hero.png", "Content/Sprites/")]
    [InlineData("hero.png", "")]
    public void AddIndexMetadata_TakesTheFolderFromLink(string link, string folder)
    {
        Assert.Equal(folder, Indexed(("Link", link)).GetMetadata("OutputRelativeDir"));
    }

    [Fact]
    public void AddIndexMetadata_WithNoLink_UsesTheItemsFolder()
    {
        Assert.Equal(Path.Combine("art", "deep") + Path.DirectorySeparatorChar, Indexed(Path.Combine("art", "deep", "hero.png")).GetMetadata("OutputRelativeDir"));
    }

    [Fact]
    public void AddIndexMetadata_NumbersTheItems()
    {
        var task = new _AddIndexMetadata { BuildEngine = new RecordingBuildEngine(), InputItems = TaskItems.Items("a", "b", "c") };

        Assert.True(task.Execute());

        Assert.Equal("2", task.OutputItems[1].GetMetadata("Index"));
        Assert.Equal("3", task.OutputItems[1].GetMetadata("Total"));
    }

    [Fact]
    public void ResolveProcessorCommand_ReplacesEveryToken()
    {
        var task = new _ResolveProcessorCommand
        {
            BuildEngine = new RecordingBuildEngine(),
            Template = "{InputFile}|{OutputFile}|{IntermediateDir}|{Quality}|{PremultiplyAlpha}",
            InputFile = "in",
            OutputFile = "out",
            IntermediateDir = "dir" + Path.DirectorySeparatorChar,
            Quality = "7",
            PremultiplyAlpha = "false",
        };

        Assert.True(task.Execute());

        Assert.Equal("in|out|dir|7|false", task.ResolvedCommand);
    }

    [Fact]
    public void ResolveProcessorCommand_DefaultsPremultiplyToTrue()
    {
        var task = new _ResolveProcessorCommand { BuildEngine = new RecordingBuildEngine(), Template = "{PremultiplyAlpha}{Quality}", InputFile = "in", OutputFile = "out" };

        Assert.True(task.Execute());

        Assert.Equal("true", task.ResolvedCommand);
    }

    private static _ProbeMediaDurations Probe(TestDirectory directory, RecordingBuildEngine engine, params string[] files)
    {
        var task = new _ProbeMediaDurations
        {
            BuildEngine = engine,
            InputItems = [.. files.Select(file => (ITaskItem)TaskItems.Item("item", ("_ProbeFile", file)))],
            CacheFile = directory.Path_("probes.cache"),
            ProbeFileMetadata = "_ProbeFile",
        };
        Assert.True(task.Execute());
        return task;
    }

    private static string WriteAudio(TestDirectory directory, string name, byte[] bytes)
    {
        string path = directory.Path_(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static TheoryData<string, byte[], string, string, string> AudioFormats => new()
    {
        { "theme.wav", AudioFiles.Wav(44100, 2, 44100 * 3), "3", "44100", "2" },
        { "theme.wav", AudioFiles.Wav(22050, 1, 11025, listChunk: true), "0.5", "22050", "1" },
        { "theme.flac", AudioFiles.Flac(44100, 2, 540225), "12.25", "44100", "2" },
        { "theme.flac", AudioFiles.Flac(48000, 1, 1), "2.1E-05", "48000", "1" },
        { "theme.ogg", AudioFiles.OggVorbis(44100, 2, 88200), "2", "44100", "2" },
        { "theme.ogg", AudioFiles.OggVorbis(32000, 1, 16001), "0.500031", "32000", "1" },
        // Opus is 48 kHz whatever rate its header records, and the pre-skip isn't played
        { "theme.opus", AudioFiles.OggOpus(2, 312, 48312), "1", "48000", "2" },
        { "theme.opus", AudioFiles.OggOpus(1, 0, 24000), "0.5", "48000", "1" },
        // The edit list's length, in the movie's timescale: the priming and padding aren't played
        { "theme.m4a", AudioFiles.M4a(1000, 2100, 44100, 92160, 44100, 2, [(2000, 2112)]), "2", "44100", "2" },
        { "theme.m4a", AudioFiles.M4a(1000, 2100, 48000, 96000, 48000, 1, null), "2", "48000", "1" },
        { "theme.m4a", AudioFiles.M4a(600, 1200, 44100, 92160, 44100, 2, [(300, -1), (900, 0)]), "1.5", "44100", "2" },
        // Apple's writer: the length in iTunes' gapless tag, and 2 in the sample entry whatever the
        // channels, which are in the AAC configuration
        { "theme.m4a", AudioFiles.M4a(44100, 39936, 44100, 39936, 44100, 2, null, " 00000000 00000840 00000153 000000000000926D 00000000 00000000", configChannels: 1), "0.85", "44100", "1" },
    };

    [Theory]
    [MemberData(nameof(AudioFormats))]
    public void Probe_ReadsEachFormatThePipelineWrites(string name, byte[] bytes, string duration, string sampleRate, string channels)
    {
        using var directory = new TestDirectory();
        string track = WriteAudio(directory, name, bytes);
        var engine = new RecordingBuildEngine();

        ITaskItem item = Probe(directory, engine, track).OutputItems[0];

        Assert.Empty(engine.Warnings);
        Assert.Equal(duration, item.GetMetadata("_DurationSeconds"));
        Assert.Equal(sampleRate, item.GetMetadata("_SampleRate"));
        Assert.Equal(channels, item.GetMetadata("_Channels"));
    }

    [Fact]
    public void Probe_MemoisesWhatItRead()
    {
        using var directory = new TestDirectory();
        string track = WriteAudio(directory, "theme.flac", AudioFiles.Flac(44100, 2, 540225));
        Assert.Equal("12.25", Probe(directory, new RecordingBuildEngine(), track).OutputItems[0].GetMetadata("_DurationSeconds"));

        // The same time, so the cache answers, and another length it would read
        DateTime written = File.GetLastWriteTimeUtc(track);
        File.WriteAllBytes(track, AudioFiles.Flac(44100, 2, 44100));
        File.SetLastWriteTimeUtc(track, written);

        Assert.Equal("12.25", Probe(directory, new RecordingBuildEngine(), track).OutputItems[0].GetMetadata("_DurationSeconds"));
    }

    [Fact]
    public void Probe_ReadsAgainWhenTheFileChanges()
    {
        using var directory = new TestDirectory();
        string track = WriteAudio(directory, "theme.flac", AudioFiles.Flac(44100, 2, 540225));
        Probe(directory, new RecordingBuildEngine(), track);

        File.WriteAllBytes(track, AudioFiles.Flac(44100, 2, 44100));
        File.SetLastWriteTimeUtc(track, DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal("1", Probe(directory, new RecordingBuildEngine(), track).OutputItems[0].GetMetadata("_DurationSeconds"));
    }

    [Fact]
    public void Probe_OfAMissingFile_WarnsAndLeavesTheMetadataBlank()
    {
        using var directory = new TestDirectory();
        var engine = new RecordingBuildEngine();

        _ProbeMediaDurations task = Probe(directory, engine, directory.Path_("theme.ogg"));

        Assert.Single(engine.Warnings);
        Assert.Equal("", task.OutputItems[0].GetMetadata("_DurationSeconds"));
    }

    [Theory]
    [InlineData("theme.ogg", "x")]
    [InlineData("theme.mp3", "ID3 and then some MP3 frames")]
    [InlineData("theme.ogg", "OggS but no more")]
    public void Probe_OfAFileItCantRead_WarnsWithoutFailingTheBuild(string name, string text)
    {
        using var directory = new TestDirectory();
        string track = directory.WriteFile(name, text);
        var engine = new RecordingBuildEngine();

        _ProbeMediaDurations task = Probe(directory, engine, track);

        Assert.Contains(engine.Warnings, warning => warning.Contains(name, StringComparison.Ordinal));
        Assert.Equal("", task.OutputItems[0].GetMetadata("_SampleRate"));
    }

    [Fact]
    public void Probe_OfADamagedFile_Warns()
    {
        using var directory = new TestDirectory();
        byte[] whole = AudioFiles.M4a(1000, 2100, 44100, 92160, 44100, 2, [(2000, 2112)]);
        string track = WriteAudio(directory, "theme.m4a", whole[..(whole.Length - 40)]);
        var engine = new RecordingBuildEngine();

        _ProbeMediaDurations task = Probe(directory, engine, track);

        Assert.Single(engine.Warnings);
        Assert.Equal("", task.OutputItems[0].GetMetadata("_Channels"));
    }
}
