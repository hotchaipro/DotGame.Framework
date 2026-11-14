// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Globalization;

namespace DotGame.Framework.ContentPipeline.BuildTool.Content;

/// <summary>
/// Removes content cache entries that no build has used recently.  Never run by
/// a build, so an interrupted or partial build cannot lose entries it still
/// needs; run it by hand or from a scheduled job.
/// </summary>
/// <remarks>
/// <para>
/// Each partition {CacheDir}/{ParametersHash} holds single-file entries
/// ({SourceHash}{OutputExtension}) and, for composite content such as texture
/// atlases, sound banks and sprite fonts, entry directories ({SetHash}).  A
/// cache hit refreshes the entry's last write time at most once a day (see
/// MarkCacheEntryUsed in ContentPipeline.targets), so the last write time is
/// when a build last used the entry, give or take a day.
/// </para>
/// <para>
/// With --unused-days N, entries not used for N days are removed, along with
/// the .tmp and .lock files of builds that were interrupted more than a day
/// ago and partitions left empty.  Without it the cache is only reported.
/// </para>
/// </remarks>
internal static class ContentCachePruneCommand
{
    public const string Name = "cache-prune";
    public const string ErrorCode = "CCH0001";

    private const string RecordedSizeSuffix = ".size";

    private static readonly TimeSpan LeftoverAge = TimeSpan.FromDays(1);

    public static void Run(CommandLineOptions options)
    {
        string cacheDir = options.GetPath("cache-dir");
        int unusedDays = options.GetInt32("unused-days", 0, 0, 36500);
        bool whatIf = options.GetSwitch("what-if");
        options.ThrowIfUnrecognized();

        if (!Directory.Exists(cacheDir))
        {
            ContentBuildOutput.Message($"Content cache directory does not exist (nothing to prune): '{cacheDir}'");
            return;
        }

        var now = DateTime.UtcNow;
        var entries = new List<CacheEntry>();
        var leftovers = new List<CacheEntry>();
        foreach (var partition in new DirectoryInfo(cacheDir).EnumerateDirectories())
        {
            foreach (var item in partition.EnumerateFileSystemInfos())
            {
                // An entry's recorded size goes with the entry, unless the entry has already gone
                if (IsRecordedSize(item) && File.Exists(item.FullName[..^RecordedSizeSuffix.Length]))
                {
                    continue;
                }

                var entry = new CacheEntry(item, now);
                (IsLeftover(item) ? leftovers : entries).Add(entry);
            }
        }

        ContentBuildOutput.Message(string.Create(CultureInfo.InvariantCulture,
            $"Content cache '{cacheDir}': {Entries(entries.Count)}, {Megabytes(entries.Sum(entry => entry.Size))} MB."));
        if (unusedDays == 0)
        {
            int[] thresholds = [30, 90, 365];
            foreach (int days in thresholds)
            {
                var unused = entries.Where(entry => entry.Unused.TotalDays >= days).ToList();
                ContentBuildOutput.Message(string.Create(CultureInfo.InvariantCulture,
                    $"  Unused for {days}+ days: {Entries(unused.Count)}, {Megabytes(unused.Sum(entry => entry.Size))} MB."));
            }
            return;
        }

        int removedCount = 0;
        long removedBytes = 0;
        var cutoff = TimeSpan.FromDays(unusedDays);
        foreach (var entry in entries.Where(entry => entry.Unused >= cutoff).OrderBy(entry => entry.Item.FullName, StringComparer.Ordinal))
        {
            string reason = string.Create(CultureInfo.InvariantCulture, $"unused for {(int)entry.Unused.TotalDays} days");
            if (Remove(cacheDir, entry, reason, whatIf))
            {
                removedCount++;
                removedBytes += entry.Size;
            }
        }
        foreach (var leftover in leftovers.Where(entry => entry.Unused >= LeftoverAge))
        {
            Remove(cacheDir, leftover, "left by an interrupted build", whatIf);
        }

        if (!whatIf)
        {
            foreach (var partition in new DirectoryInfo(cacheDir).EnumerateDirectories())
            {
                if (!partition.EnumerateFileSystemInfos().Any())
                {
                    TryDelete(partition, cacheDir);
                }
            }
        }

        ContentBuildOutput.Message(string.Create(CultureInfo.InvariantCulture,
            $"{(whatIf ? "Would remove" : "Removed")} {Entries(removedCount)} ({Megabytes(removedBytes)} MB) unused for {unusedDays}+ days."));
    }

    // Temporary files and directories of an interrupted save, and stampede lock files.
    private static bool IsLeftover(FileSystemInfo item) =>
        item.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || item.Name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        || IsRecordedSize(item);

    private static bool IsRecordedSize(FileSystemInfo item) =>
        item is FileInfo && item.Name.EndsWith(RecordedSizeSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool Remove(string cacheDir, CacheEntry entry, string reason, bool whatIf)
    {
        string relativePath = Path.GetRelativePath(cacheDir, entry.Item.FullName);
        if (whatIf)
        {
            ContentBuildOutput.Message($"[would remove] {relativePath} ({reason})");
            return true;
        }
        if (!TryDelete(entry.Item, cacheDir))
        {
            return false;
        }
        if (entry.Item is FileInfo)
        {
            TryDelete(new FileInfo(entry.Item.FullName + RecordedSizeSuffix), cacheDir);
        }
        ContentBuildOutput.Message($"[removed] {relativePath} ({reason})");
        return true;
    }

    // An entry can be in use by a concurrent build, so failures are reported and skipped.
    private static bool TryDelete(FileSystemInfo item, string cacheDir)
    {
        try
        {
            if (item is DirectoryInfo directory)
            {
                directory.Delete(recursive: true);
            }
            else
            {
                item.Delete();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ContentBuildOutput.Warning(null, ErrorCode, $"Could not remove '{Path.GetRelativePath(cacheDir, item.FullName)}': {ex.Message}");
            return false;
        }
    }

    private static string Entries(int count) => count == 1 ? "1 entry" : $"{count} entries";

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("N1", CultureInfo.InvariantCulture);

    private sealed class CacheEntry(FileSystemInfo item, DateTime now)
    {
        public FileSystemInfo Item { get; } = item;

        public TimeSpan Unused { get; } = now - item.LastWriteTimeUtc;

        public long Size { get; } = item switch
        {
            FileInfo file => file.Length,
            DirectoryInfo directory => directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length),
            _ => 0,
        };
    }
}
