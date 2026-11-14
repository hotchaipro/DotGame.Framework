// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.BuildTool;
using DotGame.Framework.ContentPipeline.BuildTool.Content;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

public sealed class ContentCachePruneCommandTests
{
    private static string Entry(TestDirectory directory, string relativePath, int ageDays)
    {
        string path = directory.WriteFile(Path.Combine("cache", relativePath), "entry");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    private static void Run(TestDirectory directory, params string[] options) =>
        ContentCachePruneCommand.Run(CommandLineOptions.Parse(["--cache-dir", directory.Path_("cache"), .. options]));

    [Fact]
    public void WithoutUnusedDays_OnlyReports()
    {
        using var directory = new TestDirectory();
        string old = Entry(directory, "p1/a.ktx2", 400);

        Run(directory);

        Assert.True(File.Exists(old));
    }

    [Fact]
    public void RemovesEntriesUnusedForThatLong_AndKeepsTheRest()
    {
        using var directory = new TestDirectory();
        string old = Entry(directory, "p1/a.ktx2", 100);
        string recent = Entry(directory, "p1/b.ktx2", 10);

        Run(directory, "--unused-days", "90");

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void RemovesAnEntrysRecordedSize_WithTheEntry_AndKeepsAKeptEntrys()
    {
        using var directory = new TestDirectory();
        Entry(directory, "p1/a.ktx2", 100);
        string oldSize = Entry(directory, "p1/a.ktx2.size", 100);
        Entry(directory, "p1/b.ktx2", 10);
        string keptSize = Entry(directory, "p1/b.ktx2.size", 100);

        Run(directory, "--unused-days", "90");

        Assert.False(File.Exists(oldSize));
        Assert.True(File.Exists(keptSize));
    }

    [Fact]
    public void RemovesARecordedSizeWithNoEntry()
    {
        using var directory = new TestDirectory();
        string size = Entry(directory, "p1/gone.ktx2.size", 2);

        Run(directory, "--unused-days", "90");

        Assert.False(File.Exists(size));
    }

    [Fact]
    public void RemovesAGroupEntryWhole_ByItsFoldersTime()
    {
        using var directory = new TestDirectory();
        Entry(directory, "p1/group/.json", 0);
        Entry(directory, "p1/group/_0.webp", 0);
        Directory.SetLastWriteTimeUtc(directory.Path_("cache", "p1", "group"), DateTime.UtcNow.AddDays(-100));

        Run(directory, "--unused-days", "90");

        Assert.False(Directory.Exists(directory.Path_("cache", "p1", "group")));
    }

    [Fact]
    public void RemovesPartitionsItEmptied()
    {
        using var directory = new TestDirectory();
        Entry(directory, "p1/a.ktx2", 100);

        Run(directory, "--unused-days", "90");

        Assert.False(Directory.Exists(directory.Path_("cache", "p1")));
    }

    [Fact]
    public void RemovesInterruptedBuildsLeftovers_OnlyOnceTheyreOld()
    {
        using var directory = new TestDirectory();
        string oldLock = Entry(directory, "p1/a.ktx2.lock", 2);
        string oldTemp = Entry(directory, "p1/a.ktx2.abc.tmp", 2);
        string liveLock = Entry(directory, "p1/b.ktx2.lock", 0);

        Run(directory, "--unused-days", "90");

        Assert.False(File.Exists(oldLock));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(liveLock));
    }

    [Fact]
    public void WhatIf_RemovesNothing()
    {
        using var directory = new TestDirectory();
        string old = Entry(directory, "p1/a.ktx2", 100);

        Run(directory, "--unused-days", "90", "--what-if");

        Assert.True(File.Exists(old));
    }

    [Fact]
    public void AMissingCache_IsNothingToDo()
    {
        using var directory = new TestDirectory();

        Run(directory, "--unused-days", "90");
    }

    [Fact]
    public void AFileAtTheCacheRoot_IsLeftAlone()
    {
        using var directory = new TestDirectory();
        string seeded = Entry(directory, "readme.txt", 1000);

        Run(directory, "--unused-days", "90");

        Assert.True(File.Exists(seeded));
    }
}
