// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Diagnostics;
using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.Tasks;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Tasks;

/// <summary>
/// The cache key and the restore and save tasks, run directly. These say what must and mustn't fork
/// the key, not what the key's value is: its definition may change, at the cost of one re-encode.
/// </summary>
public sealed class ContentCacheTaskTests
{
    private sealed record Request(string Template = "enc {InputFile} {OutputFile}", string? ToolPaths = null, string? Deps = null, string? ExtraKey = null, string Output = "out.bin");

    private static _TryRestoreFromContentCache Restore(TestDirectory directory, string source, Request request, int lockTimeout = 2000)
    {
        var task = new _TryRestoreFromContentCache
        {
            BuildEngine = new RecordingBuildEngine(),
            SourceFile = source,
            OutputFile = directory.Path_("output", request.Output),
            CacheDir = directory.Path_("cache"),
            CommandTemplate = request.Template,
            ToolPaths = request.ToolPaths,
            ProcessorDeps = request.Deps,
            ExtraKey = request.ExtraKey,
            LockTimeoutMilliseconds = lockTimeout,
        };
        Assert.True(task.Execute());
        return task;
    }

    private static void Save(TestDirectory directory, string source, string cachePath, string output = "out.bin")
    {
        var task = new _SaveToContentCache
        {
            BuildEngine = new RecordingBuildEngine(),
            SourceFile = source,
            OutputFile = directory.Path_("output", output),
            CachePath = cachePath,
            CacheDir = directory.Path_("cache"),
        };
        Assert.True(task.Execute());
    }

    private static string KeyOf(TestDirectory directory, string source, Request request)
    {
        _TryRestoreFromContentCache task = Restore(directory, source, request);

        // Releases the lock the miss took
        Save(directory, source, task.CachePath, "none");
        return Path.GetRelativePath(directory.Path_("cache"), task.CachePath);
    }

    [Fact]
    public void Key_IsStable()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var request = new Request(ExtraKey: "q=8");

        Assert.Equal(KeyOf(directory, source, request), KeyOf(directory, source, request));
    }

    [Fact]
    public void Key_ForksOnTheSourcesBytes_NotItsPath()
    {
        using var directory = new TestDirectory();
        string first = directory.WriteFile("a.png", "bytes");
        string sameBytes = directory.WriteFile("x/b.png", "bytes");
        string otherBytes = directory.WriteFile("c.png", "other");
        var request = new Request();

        Assert.Equal(KeyOf(directory, first, request), KeyOf(directory, sameBytes, request));
        Assert.NotEqual(KeyOf(directory, first, request), KeyOf(directory, otherBytes, request));
    }

    [Theory]
    [InlineData("enc2 {InputFile} {OutputFile}", null)]
    [InlineData("enc {InputFile} {OutputFile}", "q=9")]
    public void Key_ForksOnTheCommandAndTheExtraKey(string template, string? extraKey)
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");

        Assert.NotEqual(KeyOf(directory, source, new Request(ExtraKey: "q=8")), KeyOf(directory, source, new Request(template, ExtraKey: extraKey)));
    }

    [Fact]
    public void Key_ForksOnTheOutputExtension()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");

        Assert.NotEqual(KeyOf(directory, source, new Request(Output: "a.webp")), KeyOf(directory, source, new Request(Output: "a.ktx2")));
    }

    [Fact]
    public void Key_ReducesToolPathsToFileNames_SoMachinesShareEntries()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        string oursRoot = Path.Combine("C:", "tools", "ours", "ffmpeg");
        string theirsRoot = Path.Combine("D:", "other place", "ffmpeg");

        string ours = KeyOf(directory, source, new Request($"\"{oursRoot}\" -i {{InputFile}}", ToolPaths: $"\"{oursRoot}\""));
        string theirs = KeyOf(directory, source, new Request($"\"{theirsRoot}\" -i {{InputFile}}", ToolPaths: $"\"{theirsRoot}\""));

        Assert.Equal(ours, theirs);
    }

    [Fact]
    public void Key_ForksOnADependencysBytes_NotItsFolder()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        string v1 = directory.WriteFile("v1/script.py", "one");
        string v1Elsewhere = directory.WriteFile("elsewhere/script.py", "one");
        string v2 = directory.WriteFile("v2/script.py", "two");

        Assert.Equal(KeyOf(directory, source, new Request(Deps: v1)), KeyOf(directory, source, new Request(Deps: v1Elsewhere)));
        Assert.NotEqual(KeyOf(directory, source, new Request(Deps: v1)), KeyOf(directory, source, new Request(Deps: v2)));
    }

    [Fact]
    public void Key_DoesNotDependOnTheOrderOfDependencies()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        string a = directory.WriteFile("a.py", "a");
        string b = directory.WriteFile("b.py", "b");

        Assert.Equal(KeyOf(directory, source, new Request(Deps: $"{a};{b}")), KeyOf(directory, source, new Request(Deps: $"{b};{a}")));
    }

    [Fact]
    public void Key_CountsAMissingDependency()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        string present = directory.WriteFile("dep.py", "a");

        Assert.NotEqual(KeyOf(directory, source, new Request(Deps: present)), KeyOf(directory, source, new Request(Deps: directory.Path_("other", "dep.py"))));
    }

    [Fact]
    public void Restore_WithNoCacheFolder_IsAMiss_WithNoLock()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var task = new _TryRestoreFromContentCache { BuildEngine = new RecordingBuildEngine(), SourceFile = source, OutputFile = directory.Path_("o.bin"), CacheDir = "", CommandTemplate = "enc" };

        Assert.True(task.Execute());

        Assert.Equal("false", task.Restored);
        Assert.Equal("", task.CachePath);
    }

    [Fact]
    public void Restore_OfAMissingSource_IsAMiss()
    {
        using var directory = new TestDirectory();

        _TryRestoreFromContentCache task = Restore(directory, directory.Path_("none.png"), new Request());

        Assert.Equal("false", task.Restored);
    }

    [Fact]
    public void SaveThenRestore_GivesBackTheBytes_WithAFreshWriteTime()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var request = new Request();
        _TryRestoreFromContentCache miss = Restore(directory, source, request);
        Assert.Equal("false", miss.Restored);
        string output = directory.WriteFile("output/out.bin", "encoded");
        Save(directory, source, miss.CachePath);
        File.SetLastWriteTimeUtc(miss.CachePath, DateTime.UtcNow.AddHours(-5));
        File.Delete(output);

        _TryRestoreFromContentCache hit = Restore(directory, source, request);

        Assert.Equal("true", hit.Restored);
        Assert.Equal("encoded", File.ReadAllText(output));
        Assert.True(File.GetLastWriteTimeUtc(output) > DateTime.UtcNow.AddMinutes(-5), "A restored output kept the entry's old time, so the next build would rerun.");
    }

    [Fact]
    public void Hit_RefreshesAnEntryNotUsedForADay_AndLeavesARecentOneAlone()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var request = new Request();
        _TryRestoreFromContentCache miss = Restore(directory, source, request);
        directory.WriteFile("output/out.bin", "encoded");
        Save(directory, source, miss.CachePath);

        DateTime recent = DateTime.UtcNow.AddHours(-5);
        File.SetLastWriteTimeUtc(miss.CachePath, recent);
        Restore(directory, source, request);
        Assert.Equal(recent, File.GetLastWriteTimeUtc(miss.CachePath));

        File.SetLastWriteTimeUtc(miss.CachePath, DateTime.UtcNow.AddDays(-30));
        Restore(directory, source, request);
        Assert.True(File.GetLastWriteTimeUtc(miss.CachePath) > DateTime.UtcNow.AddHours(-1));
    }

    [Fact]
    public void Restore_TreatsAnEmptyEntryAsAMiss()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var request = new Request();
        _TryRestoreFromContentCache miss = Restore(directory, source, request);
        directory.WriteFile("output/out.bin", "encoded");
        Save(directory, source, miss.CachePath);
        File.WriteAllBytes(miss.CachePath, []);

        _TryRestoreFromContentCache again = Restore(directory, source, request);
        Save(directory, source, again.CachePath);

        Assert.Equal("false", again.Restored);
        Assert.Equal("encoded", File.ReadAllText(miss.CachePath));
    }

    [Theory]
    [InlineData("version https://git-lfs.github.com/spec/v1")]
    [InlineData("encod")]
    [InlineData("encoded and more")]
    public void Restore_TreatsAnEntryThatIsntWhatWasWrittenAsAMiss_AndSavingRepairsIt(string replacement)
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var request = new Request();
        _TryRestoreFromContentCache miss = Restore(directory, source, request);
        directory.WriteFile("output/out.bin", "encoded");
        Save(directory, source, miss.CachePath);
        File.WriteAllText(miss.CachePath, replacement);

        _TryRestoreFromContentCache again = Restore(directory, source, request);
        Assert.Equal("false", again.Restored);
        Save(directory, source, again.CachePath);

        _TryRestoreFromContentCache repaired = Restore(directory, source, request);
        Assert.Equal("true", repaired.Restored);
        Assert.Equal("encoded", File.ReadAllText(directory.Path_("output", "out.bin")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("version https://git-lfs.github.com/spec/v1")]
    public void Restore_OfAnEntryWithNoUsableRecordedSize_IsAMiss(string? recorded)
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var request = new Request();
        _TryRestoreFromContentCache miss = Restore(directory, source, request);
        directory.WriteFile("output/out.bin", "encoded");
        Save(directory, source, miss.CachePath);
        File.Delete(miss.CachePath + ".size");
        if (recorded != null)
        {
            File.WriteAllText(miss.CachePath + ".size", recorded);
        }

        _TryRestoreFromContentCache again = Restore(directory, source, request);

        Assert.Equal("false", again.Restored);
        Save(directory, source, again.CachePath);
    }

    [Fact]
    public void Save_WritesTheIgnoreFileOnce_AndNeverOverAnEditedOne()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var request = new Request();
        _TryRestoreFromContentCache miss = Restore(directory, source, request);
        directory.WriteFile("output/out.bin", "encoded");
        string ignoreFile = directory.Path_("cache", ".gitignore");

        Save(directory, source, miss.CachePath);
        Assert.Contains("\n*\n", File.ReadAllText(ignoreFile), StringComparison.Ordinal);

        File.WriteAllText(ignoreFile, "edited");
        Save(directory, source, miss.CachePath);
        Assert.Equal("edited", File.ReadAllText(ignoreFile));
    }

    [Fact]
    public void Save_WithNoOutput_WritesNoEntry_AndReleasesTheLock()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var request = new Request();
        _TryRestoreFromContentCache miss = Restore(directory, source, request);

        Save(directory, source, miss.CachePath);

        Assert.False(File.Exists(miss.CachePath));
        var clock = Stopwatch.StartNew();
        _TryRestoreFromContentCache next = Restore(directory, source, request, lockTimeout: 120000);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60));
        Save(directory, source, next.CachePath);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFiles()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        _TryRestoreFromContentCache miss = Restore(directory, source, new Request());
        directory.WriteFile("output/out.bin", "encoded");

        Save(directory, source, miss.CachePath);

        Assert.Equal([Path.GetFileName(miss.CachePath), Path.GetFileName(miss.CachePath) + ".size"], Directory.GetFiles(Path.GetDirectoryName(miss.CachePath)!).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Retry_AfterAFailedEncodeInTheSameProcess_DoesNotWait()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var request = new Request();

        // The encode fails, so MSBuild never reaches the save
        Restore(directory, source, request);

        var clock = Stopwatch.StartNew();
        Restore(directory, source, request, lockTimeout: 120000);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"The retry waited {clock.Elapsed} on its own lock.");
    }

    [Fact]
    public void Lock_HeldByAnotherBuild_TimesOutIntoAnIndependentEncode()
    {
        using var directory = new TestDirectory();
        string source = directory.WriteFile("a.png", "bytes");
        var request = new Request(ExtraKey: "unique");

        // Finds where the lock goes, then holds it as a peer process would
        _TryRestoreFromContentCache probe = Restore(directory, source, request);
        Save(directory, source, probe.CachePath);
        using var peer = new FileStream(probe.CachePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        _TryRestoreFromContentCache task = Restore(directory, source, request, lockTimeout: 200);

        Assert.Equal("false", task.Restored);
        Assert.Equal(probe.CachePath, task.CachePath);
    }

    private static _TryRestoreFromContentGroupCache RestoreGroup(TestDirectory directory, string sources, string outputBase = "atlas", string template = "pack", string? extraKey = null)
    {
        var task = new _TryRestoreFromContentGroupCache
        {
            BuildEngine = new RecordingBuildEngine(),
            SourceFiles = sources,
            OutputBaseFile = directory.Path_("output", outputBase),
            CacheDir = directory.Path_("cache"),
            CommandTemplate = template,
            ExtraKey = extraKey,
            LockTimeoutMilliseconds = 2000,
        };
        Assert.True(task.Execute());
        return task;
    }

    private static void SaveGroup(TestDirectory directory, string cachePath, string outputBase = "atlas", string suffixes = ".json", string pageExtension = ".webp")
    {
        var task = new _SaveToContentGroupCache
        {
            BuildEngine = new RecordingBuildEngine(),
            OutputBaseFile = directory.Path_("output", outputBase),
            Suffixes = suffixes,
            PageExtension = pageExtension,
            CachePath = cachePath,
            CacheDir = directory.Path_("cache"),
        };
        Assert.True(task.Execute());
    }

    private static string GroupKey(TestDirectory directory, string sources, string template = "pack", string? extraKey = null)
    {
        _TryRestoreFromContentGroupCache task = RestoreGroup(directory, sources, template: template, extraKey: extraKey);
        SaveGroup(directory, task.CachePath, "none");
        return task.CachePath;
    }

    [Fact]
    public void GroupKey_ForksOnAnyMembersBytesOrName_AndOnTheCommand()
    {
        using var directory = new TestDirectory();
        string a = directory.WriteFile("a.png", "a");
        string b = directory.WriteFile("b.png", "b");
        string renamed = directory.WriteFile("c.png", "b");
        string changed = directory.WriteFile("changed/b.png", "changed");
        string baseKey = GroupKey(directory, $"{a};{b}");

        Assert.Equal(baseKey, GroupKey(directory, $"{a};{b}"));
        Assert.NotEqual(baseKey, GroupKey(directory, $"{a};{renamed}"));
        Assert.NotEqual(baseKey, GroupKey(directory, $"{a};{changed}"));
        Assert.NotEqual(baseKey, GroupKey(directory, a));
        Assert.NotEqual(baseKey, GroupKey(directory, $"{a};{b}", template: "pack2"));
        Assert.NotEqual(baseKey, GroupKey(directory, $"{a};{b}", extraKey: "pad=1"));
    }

    [Fact]
    public void GroupKey_DoesNotDependOnItemOrder()
    {
        using var directory = new TestDirectory();
        string a = directory.WriteFile("a.png", "a");
        string b = directory.WriteFile("b.png", "b");

        Assert.Equal(GroupKey(directory, $"{a};{b}"), GroupKey(directory, $"{b};{a}"));
    }

    [Fact]
    public void GroupRestore_WithAMissingMember_IsAMiss()
    {
        using var directory = new TestDirectory();
        string a = directory.WriteFile("a.png", "a");

        _TryRestoreFromContentGroupCache task = RestoreGroup(directory, $"{a};{directory.Path_("none.png")}");

        Assert.Equal("false", task.Restored);
        Assert.Equal("", task.CachePath);
    }

    [Fact]
    public void GroupSaveThenRestore_GivesBackEveryMember()
    {
        using var directory = new TestDirectory();
        string a = directory.WriteFile("a.png", "a");
        _TryRestoreFromContentGroupCache miss = RestoreGroup(directory, a);
        directory.WriteFile("output/atlas.json", "descriptor");
        directory.WriteFile("output/atlas_0.webp", "page 0");
        directory.WriteFile("output/atlas_1.webp", "page 1");
        SaveGroup(directory, miss.CachePath);
        Directory.Delete(directory.Path_("output"), recursive: true);

        _TryRestoreFromContentGroupCache hit = RestoreGroup(directory, a);

        Assert.Equal("true", hit.Restored);
        Assert.Equal("descriptor", File.ReadAllText(directory.Path_("output", "atlas.json")));
        Assert.Equal("page 0", File.ReadAllText(directory.Path_("output", "atlas_0.webp")));
        Assert.Equal("page 1", File.ReadAllText(directory.Path_("output", "atlas_1.webp")));
    }

    [Fact]
    public void GroupSave_CollectsOnlyItsOwnMembers()
    {
        // A plain texture "atlas.webp" beside an atlas named "atlas", and "atlas_extra.webp"
        using var directory = new TestDirectory();
        string a = directory.WriteFile("a.png", "a");
        _TryRestoreFromContentGroupCache miss = RestoreGroup(directory, a);
        directory.WriteFile("output/atlas.json", "descriptor");
        directory.WriteFile("output/atlas_0.webp", "page");
        directory.WriteFile("output/atlas.webp", "someone else's");
        directory.WriteFile("output/atlas_extra.webp", "someone else's");

        SaveGroup(directory, miss.CachePath);

        Assert.Equal([".json", ".sizes", "_0.webp"], Directory.GetFiles(miss.CachePath).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void GroupSave_WithNothingBuilt_WritesNoEntry()
    {
        using var directory = new TestDirectory();
        string a = directory.WriteFile("a.png", "a");
        _TryRestoreFromContentGroupCache miss = RestoreGroup(directory, a);

        SaveGroup(directory, miss.CachePath);

        Assert.False(Directory.Exists(miss.CachePath));
    }

    [Fact]
    public void GroupSave_LeavesAnExistingEntryAlone()
    {
        using var directory = new TestDirectory();
        string a = directory.WriteFile("a.png", "a");
        _TryRestoreFromContentGroupCache miss = RestoreGroup(directory, a);
        directory.WriteFile("output/atlas.json", "first");
        SaveGroup(directory, miss.CachePath);

        directory.WriteFile("output/atlas.json", "second");
        SaveGroup(directory, miss.CachePath);

        Assert.Equal("first", File.ReadAllText(Path.Combine(miss.CachePath, ".json")));
    }

    [Theory]
    [InlineData(".json", "version https://git-lfs.github.com/spec/v1")]
    [InlineData("_0.webp", "pag")]
    [InlineData(".sizes", "version https://git-lfs.github.com/spec/v1")]
    public void GroupRestore_OfAnEntryThatIsntWhatWasWritten_IsAMiss_AndSavingRepairsIt(string member, string replacement)
    {
        using var directory = new TestDirectory();
        string a = directory.WriteFile("a.png", "a");
        _TryRestoreFromContentGroupCache miss = RestoreGroup(directory, a);
        directory.WriteFile("output/atlas.json", "descriptor");
        directory.WriteFile("output/atlas_0.webp", "page");
        SaveGroup(directory, miss.CachePath);
        File.WriteAllText(Path.Combine(miss.CachePath, member), replacement);

        _TryRestoreFromContentGroupCache again = RestoreGroup(directory, a);
        Assert.Equal("false", again.Restored);
        SaveGroup(directory, again.CachePath);

        _TryRestoreFromContentGroupCache repaired = RestoreGroup(directory, a);
        Assert.Equal("true", repaired.Restored);
        Assert.Equal("descriptor", File.ReadAllText(directory.Path_("output", "atlas.json")));
        Assert.False(File.Exists(directory.Path_("output", "atlas.sizes")));
    }

    [Fact]
    public void GroupRestore_WithAnEntrysMemberGone_IsAMiss()
    {
        using var directory = new TestDirectory();
        string a = directory.WriteFile("a.png", "a");
        _TryRestoreFromContentGroupCache miss = RestoreGroup(directory, a);
        directory.WriteFile("output/atlas.json", "descriptor");
        directory.WriteFile("output/atlas_0.webp", "page");
        SaveGroup(directory, miss.CachePath);
        File.Delete(Path.Combine(miss.CachePath, "_0.webp"));

        _TryRestoreFromContentGroupCache again = RestoreGroup(directory, a);

        Assert.Equal("false", again.Restored);
        SaveGroup(directory, again.CachePath);
    }

    [Fact]
    public void GroupRestore_TreatsAnEmptyEntryAsAMiss()
    {
        using var directory = new TestDirectory();
        string a = directory.WriteFile("a.png", "a");
        _TryRestoreFromContentGroupCache miss = RestoreGroup(directory, a);
        SaveGroup(directory, miss.CachePath, "none");
        Directory.CreateDirectory(miss.CachePath);

        _TryRestoreFromContentGroupCache again = RestoreGroup(directory, a);

        Assert.Equal("false", again.Restored);
        SaveGroup(directory, again.CachePath, "none");
    }
}
