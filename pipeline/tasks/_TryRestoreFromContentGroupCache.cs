// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _TryRestoreFromContentGroupCache – the MANY-to-one counterpart of
// _TryRestoreFromContentCache, for composite content types whose output
// is a function of an entire ordered input SET rather than of a single
// source file (texture atlases, sound banks).
//
// Everything about the parameters hash is identical to the single-file
// task (see its comment for the full definition), so an edit to an
// encoder script or a change to the encode command invalidates both
// kinds of entry the same way.  Only two things differ:
//
//   * Source hash — SHA256 over the ORDERED set, each member
//     contributing "filename:SHA256(bytes)\n".  Order is significant
//     because it determines atlas packing / bank concatenation order.
//     A missing member reports a MISS rather than hashing a partial
//     set, so an incomplete input list can never alias a complete one.
//
//   * Entry layout — a composite build emits a variable number of
//     artefacts ({base}.json plus one file per atlas page / bank), so
//     the entry is a DIRECTORY
//     {CacheDir}/{parametersHash}/{sourceHash}/ whose members are named
//     by their suffix relative to OutputBaseFile (".json", "_0.webp",
//     …).  Restoring copies every member back to OutputBaseFile+suffix,
//     which needs no knowledge of how many pages the build produced.
//     _SaveToContentGroupCache publishes the directory with a single
//     atomic Directory.Move, so a reader can only ever observe a
//     complete entry — the many-file analogue of the single-file
//     task's temp-file-plus-move.
//
// This is the fast path that matters most for composite content: the
// packer/concatenator is a multi-second process even when it ends
// up reproducing byte-identical output, and a hit replaces it with an
// in-process hash and a handful of file copies.
//
// Correctness is unchanged: caching disabled, a missing input, a
// missing/empty entry, or any I/O error all report a MISS, so the
// normal build runs and the output is always correct.
public sealed class _TryRestoreFromContentGroupCache : Task, ICancelableTask
{
    private const long LockWaitNoticeMilliseconds = 5000;
    private volatile bool _isCanceled;

    public void Cancel()
    {
        this._isCanceled = true;
    }

    [Required]
    public string SourceFiles { get; set; }

    [Required]
    public string OutputBaseFile { get; set; }

    public string CacheDir { get; set; }

    [Required]
    public string CommandTemplate { get; set; }

    public string ToolPaths { get; set; }

    public string ProcessorDeps { get; set; }

    public string ExtraKey { get; set; }

    public int LockTimeoutMilliseconds { get; set; }

    [Output]
    public string Restored { get; set; }

    [Output]
    public string CachePath { get; set; }

    public override bool Execute()
    {
        this.Restored = "false";
        this.CachePath = "";

        // Caching disabled or nothing to key on: report a miss so the build runs.
        if (string.IsNullOrWhiteSpace(this.CacheDir) ||
            string.IsNullOrWhiteSpace(this.SourceFiles) || string.IsNullOrEmpty(this.OutputBaseFile))
        {
            return true;
        }

        string HashHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        string HashFileHex(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                return HashHex(sha.ComputeHash(fs));
            }
        }

        // Parameters hash: identical scheme to _TryRestoreFromContentCache.
        // Tool paths are collapsed to bare filenames so the key reflects tool
        // identity without baking in machine-specific install locations.
        var normalizedTemplate = ContentToolIdentity.NormalizeTemplate(this.CommandTemplate, this.ToolPaths, out var toolHashes);

        var descriptor = new StringBuilder();
        descriptor.Append(normalizedTemplate);
        descriptor.Append(toolHashes);

        if (!string.IsNullOrEmpty(this.ProcessorDeps))
        {
            var deps = new List<string>(this.ProcessorDeps.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            deps.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var dep in deps)
            {
                var depPath = dep.Trim();
                if (depPath.Length == 0)
                {
                    continue;
                }

                descriptor.Append('\n').Append(Path.GetFileName(depPath)).Append(':');
                descriptor.Append(File.Exists(depPath) ? HashFileHex(depPath) : "missing");
            }
        }

        if (!string.IsNullOrEmpty(this.ExtraKey))
        {
            descriptor.Append('\n').Append(this.ExtraKey);
        }

        string parametersHash;
        using (var sha = SHA256.Create())
        {
            parametersHash = HashHex(sha.ComputeHash(Encoding.UTF8.GetBytes(descriptor.ToString()))).Substring(0, 16);
        }

        // Source hash over the ordered input set.  Names are included as well
        // as bytes so that renaming a sprite (which changes the atlas region
        // keys) forks the key even though the pixels are unchanged.
        // NOTE: In name order, as the items come in glob order, which differs between systems
        var orderedSources = new List<string>(this.SourceFiles.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
        orderedSources.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a.Trim()), Path.GetFileName(b.Trim())));

        var setDescriptor = new StringBuilder();
        foreach (var raw in orderedSources)
        {
            var sourcePath = raw.Trim();
            if (sourcePath.Length == 0)
            {
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                return true; // Incomplete set: MISS.
            }

            setDescriptor.Append(Path.GetFileName(sourcePath)).Append(':')
                         .Append(HashFileHex(sourcePath)).Append('\n');
        }
        if (setDescriptor.Length == 0)
        {
            return true;
        }

        string sourceHash;
        using (var sha = SHA256.Create())
        {
            sourceHash = HashHex(sha.ComputeHash(Encoding.UTF8.GetBytes(setDescriptor.ToString())));
        }

        var partition = Path.Combine(this.CacheDir.TrimEnd('\\', '/'), parametersHash);
        var entryDir = Path.Combine(partition, sourceHash);
        this.CachePath = entryDir;

        // Records that a build used the entry, for 'cache-prune --unused-days'.
        // A hit leaves the entry's own timestamp alone otherwise, so its last
        // write time is the last time any build used it.  Refreshed at most
        // once a day to keep cache hits read-mostly; best effort.
        void MarkCacheEntryUsed(string entryPath, bool isDirectory)
        {
            try
            {
                var now = DateTime.UtcNow;
                var lastUsed = isDirectory ? Directory.GetLastWriteTimeUtc(entryPath) : File.GetLastWriteTimeUtc(entryPath);
                if (now - lastUsed < TimeSpan.FromDays(1))
                {
                    return;
                }

                if (isDirectory)
                {
                    Directory.SetLastWriteTimeUtc(entryPath, now);
                }
                else
                {
                    File.SetLastWriteTimeUtc(entryPath, now);
                }
            }
            catch { }
        }

        // Copy every member of an intact entry directory back to
        // OutputBaseFile + <member name>.  Returns false (a MISS) when the
        // entry is absent or not what was written so the build runs.  Tolerates
        // transient AV / concurrent-build file locks with a short exponential backoff.
        bool TryServeCacheEntry(string dir)
        {
            var members = ContentToolIdentity.ReadGroupEntry(dir);
            if (members == null)
            {
                return false;
            }

            var destDir = Path.GetDirectoryName(this.OutputBaseFile);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            const int maxCopyAttempts = 6;
            foreach (var member in members)
            {
                var destFile = this.OutputBaseFile + Path.GetFileName(member);
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Copy(member, destFile, true);
                        // File.Copy carries the entry's write time across, which
                        // is when the cache was populated and therefore older
                        // than the inputs.  Stamp the restored artefact as
                        // freshly written so the calling target's Inputs/Outputs
                        // check sees it as up to date and skips entirely next
                        // build, instead of re-running to serve the same hit.
                        try { File.SetLastWriteTimeUtc(destFile, DateTime.UtcNow); } catch { }
                        break;
                    }
                    catch (IOException) when (attempt < maxCopyAttempts)
                    {
                        Thread.Sleep(40 * (int)Math.Pow(2, attempt - 1));
                    }
                }
            }
            MarkCacheEntryUsed(dir, isDirectory: true);
            return true;
        }

        // Warm-cache fast path (no contention): serve the entry directly.
        if (TryServeCacheEntry(entryDir))
        {
            this.Restored = "true";
            this.Log.LogMessage(MessageImportance.High, "[cache hit] {0} -> {1} ({2})", entryDir, this.OutputBaseFile, parametersHash);
            return true;
        }

        // Cold miss: take the same cross-process encode lock the single-file
        // task uses, so under a multi-process (-m) build only ONE builder runs
        // the (expensive) composite build while peers wait and then serve the
        // freshly written entry.
        int timeoutMs = this.LockTimeoutMilliseconds > 0 ? this.LockTimeoutMilliseconds : 300000;
        var lockPath = entryDir + ".lock";
        FileStream lockStream = null;
        try
        {
            Directory.CreateDirectory(partition);

            // NOTE: This process's own earlier encode failed, so the save that would release its
            // lock never ran, and no wait would ever end: the encode is this build's again
            lockStream = AppDomain.CurrentDomain.GetData("ContentCacheLock::" + entryDir) as FileStream;
            AppDomain.CurrentDomain.SetData("ContentCacheLock::" + entryDir, null);

            var sw = Stopwatch.StartNew();
            int delayMs = 25;
            bool waitLogged = false;
            while (lockStream == null)
            {
                try
                {
                    lockStream = new FileStream(lockPath, FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                    break; // Acquired.
                }
                catch (IOException) { }               // Held by a peer: keep waiting.
                catch (UnauthorizedAccessException) { } // Transient AV / delete race: retry.

                if (!waitLogged && (sw.ElapsedMilliseconds >= LockWaitNoticeMilliseconds))
                {
                    waitLogged = true;
                    this.Log.LogMessage(MessageImportance.High, "Waiting for another build encoding {0}...", this.OutputBaseFile);
                }

                if (this._isCanceled)
                {
                    break;
                }

                if (sw.ElapsedMilliseconds >= timeoutMs)
                {
                    // Give up waiting so a stuck/crashed peer can never deadlock
                    // the build; degrade to an independent build below.
                    this.Log.LogMessage(MessageImportance.High,
                        "[cache lock timeout] {0} ({1}); building independently", this.OutputBaseFile, parametersHash);
                    break;
                }
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 200);
            }
        }
        catch
        {
            // Locking is strictly best-effort: any failure falls back to a
            // plain independent build, which is always correct.
            if (lockStream != null) { try { lockStream.Dispose(); } catch { } }
            lockStream = null;
        }

        if (lockStream != null)
        {
            // Re-check under the lock: a peer may have populated the entry
            // while we waited, in which case we serve it and skip the build.
            if (TryServeCacheEntry(entryDir))
            {
                try { lockStream.Dispose(); } catch { }
                this.Restored = "true";
                this.Log.LogMessage(MessageImportance.High, "[cache hit] {0} -> {1} ({2})", entryDir, this.OutputBaseFile, parametersHash);
                return true;
            }

            // Still a miss: we own the build.  Park the held lock so
            // _SaveToContentGroupCache releases it AFTER the entry is written.
            AppDomain.CurrentDomain.SetData("ContentCacheLock::" + entryDir, lockStream);
        }

        // Reported as a MISS (Restored stays "false"): the build runs next.

        return !this.Log.HasLoggedErrors;
    }
}
