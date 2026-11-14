// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.BuildTool;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

public sealed class ContentPathsTests
{
    [Fact]
    public void Normalize_StripsQuotesAndSpace()
    {
        Assert.Equal("tool.exe", ContentPaths.Normalize("  \"tool.exe\" "));
    }

    [Fact]
    public void Normalize_ConvertsBackslashes_ExceptOnWindows()
    {
        string expected = OperatingSystem.IsWindows() ? @"Content\Sprites\a.png" : "Content/Sprites/a.png";

        Assert.Equal(expected, ContentPaths.Normalize(@"Content\Sprites\a.png"));
    }

    [Fact]
    public void ReadPathList_SplitsOnSemicolonsAndLines_AndNormalizes()
    {
        using var directory = new TestDirectory();
        string list = directory.WriteFile("list.txt", "a.png;\"b c.png\"\r\n d.png \n\n;e é.png;");

        Assert.Equal(["a.png", "b c.png", "d.png", "e é.png"], ContentPaths.ReadPathList(list));
    }

    [Fact]
    public void ReadPathList_FailsOnAMissingFile()
    {
        Assert.Throws<ContentBuildException>(() => ContentPaths.ReadPathList(Path.Combine(Path.GetTempPath(), "no-such-list.txt")));
    }

    [Fact]
    public void ReadCommandTemplate_TrimsTheFile()
    {
        using var directory = new TestDirectory();

        Assert.Equal("encode {InputFile}", ContentPaths.ReadCommandTemplate(directory.WriteFile("t.cmd", "  encode {InputFile}\r\n")));
    }

    [Fact]
    public void ReadCommandTemplate_FailsOnAMissingOrEmptyFile()
    {
        using var directory = new TestDirectory();

        Assert.Throws<ContentBuildException>(() => ContentPaths.ReadCommandTemplate(directory.Path_("none.cmd")));
        Assert.Throws<ContentBuildException>(() => ContentPaths.ReadCommandTemplate(directory.WriteFile("empty.cmd", " \r\n")));
    }

    [Fact]
    public void NameComparer_IgnoresCaseThenBreaksTiesByCase()
    {
        List<string> names = ["b", "B", "a", "C", "A"];

        names.Sort(ContentPaths.NameComparer);

        Assert.Equal(["A", "a", "B", "b", "C"], names);
    }

    [Fact]
    public void NameComparer_DoesNotDependOnCulture()
    {
        // A linguistic comparison ignores '-' and sorts these otherwise; upper-cased ordinals are "A-B" < "AB" < "A_B"
        List<string> names = ["a_b", "ab", "a-b"];

        names.Sort(ContentPaths.NameComparer);

        Assert.Equal(["a-b", "ab", "a_b"], names);
    }

    [Fact]
    public void ThrowIfDuplicateNames_ComparesFileNamesWithoutCaseOrExtension()
    {
        var error = Assert.Throws<ContentBuildException>(() => ContentPaths.ThrowIfDuplicateNames(["x/Hero.png", "y/hero.jpg"], "region"));

        Assert.Contains("x/Hero.png", error.Message, StringComparison.Ordinal);
        Assert.Contains("y/hero.jpg", error.Message, StringComparison.Ordinal);
        Assert.Contains("region", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfDuplicateNames_AllowsDistinctNames()
    {
        ContentPaths.ThrowIfDuplicateNames(["hero.png", "hero2.png", "x/villain.png"], "region");
    }

    [Fact]
    public void CreateParentDirectory_MakesTheFolders_AndLeavesABareNameAlone()
    {
        using var directory = new TestDirectory();

        ContentPaths.CreateParentDirectory(directory.Path_("a", "b", "c.txt"));
        ContentPaths.CreateParentDirectory("bare-name.txt");

        Assert.True(Directory.Exists(directory.Path_("a", "b")));
    }

    [Fact]
    public void RetryWhileLocked_PassesOtherFailuresThrough()
    {
        int attempts = 0;

        Assert.Throws<FileNotFoundException>(() => ContentPaths.RetryWhileLocked<int>(
            () =>
            {
                attempts++;
                throw new FileNotFoundException();
            },
            "reading"));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void RetryWhileLocked_RetriesASharingViolationUntilItClears()
    {
        const int SharingViolation = unchecked((int)0x80070020);
        int attempts = 0;

        int result = ContentPaths.RetryWhileLocked(
            () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new IOException("locked", SharingViolation);
                }

                return 42;
            },
            "reading");

        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
    }
}
