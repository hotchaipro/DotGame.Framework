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

// _TryRestoreFromContentCache – generic, content-type-agnostic in-process
// (no child process) content-cache fast path.
//
// This is the single source of truth for the cache key, on-disk layout,
// and restore logic; textures, shaders, sound, and music all route through it, so
// there is no per-type hash code to keep in sync.  A cache hit only needs
// to hash the source and copy the matching cached artefact, which is pure
// file/hash work; doing it inside the already-running MSBuild process
// avoids the process start-up (tool host, JIT, external encoder) that
// the per-item Exec otherwise pays just to perform a ~5ms file copy.
// Only a genuine miss falls through to the encode.
//
// Cache key (single definition):
//   parametersHash = SHA256(
//       normalizedCommandTemplate            # the PRE-substitution template with
//                                            # {InputFile}/{OutputFile}/{IntermediateDir}/
//                                            # {CacheDir}/{Quality} left literal; each
//                                            # absolute tool path in ToolPaths replaced by
//                                            # its bare filename so the hash stays stable
//                                            # across machines (cross-machine cache sharing)
//                                            # while still reflecting tool identity.
//     + "\n" + for each ProcessorDeps file:  filename + ":" + SHA256(file bytes)
//                                            # full-byte hash of every processor tool, so
//                                            # ANY change to an encoder tool invalidates.
//     + "\n" + ExtraKey )                    # optional per-item non-template values
//                                            # (e.g. resolved Quality for lossy encodes).
//   truncated to 16 hex chars.
//
// Layout: {CacheDir}/{parametersHash}/{sourceHash}{OutputExtension}.
//
// Correctness: caching disabled (blank CacheDir), a missing source, a
// missing/empty cache entry, or any I/O error all report a MISS, so the
// normal encode runs and the output is always correct — the cache only
// ever affects performance.
public sealed class _TryRestoreFromContentCache : Task, ICancelableTask
{
    private const long LockWaitNoticeMilliseconds = 5000;
    private volatile bool _isCanceled;

    public void Cancel()
    {
        this._isCanceled = true;
    }

    [Required]
    public string SourceFile { get; set; }

    [Required]
    public string OutputFile { get; set; }

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

        // Caching disabled or source missing: report a miss so the encode runs.
        if (string.IsNullOrWhiteSpace(this.CacheDir) ||
            string.IsNullOrEmpty(this.SourceFile) || !File.Exists(this.SourceFile))
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

        // Normalize the command template: replace each absolute tool path
        // with its bare filename so the hash reflects tool identity without
        // baking in machine-specific install locations (keeps the cache
        // shareable across machines / CI).
        var normalizedTemplate = ContentToolIdentity.NormalizeTemplate(this.CommandTemplate, this.ToolPaths, out var toolHashes);

        var descriptor = new StringBuilder();
        descriptor.Append(normalizedTemplate);
        descriptor.Append(toolHashes);

        // Full-byte hash of every processor dependency script.  Any change
        // to an encoder script (even a comment) forks the partition once.
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
                if (File.Exists(depPath))
                {
                    using (var sha = SHA256.Create())
                    using (var fs = File.OpenRead(depPath))
                    {
                        descriptor.Append(HashHex(sha.ComputeHash(fs)));
                    }
                }
                else
                {
                    // A missing dep is itself part of the identity; record it
                    // rather than silently ignoring so the key stays stable.
                    descriptor.Append("missing");
                }
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

        string sourceHash;
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(this.SourceFile))
        {
            sourceHash = HashHex(sha.ComputeHash(fs));
        }

        var extension = Path.GetExtension(this.OutputFile);
        var partition = Path.Combine(this.CacheDir.TrimEnd('\\', '/'), parametersHash);
        var cachePath = Path.Combine(partition, sourceHash + extension);
        this.CachePath = cachePath;

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

        // Copy an intact cache entry to the output.  Returns false (a MISS)
        // when the entry is absent or not what was written so the encode runs.
        // Tolerates transient AV / concurrent-build file locks with a short
        // exponential backoff.
        bool TryServeCacheEntry(string entryPath, string destFile)
        {
            if (!ContentToolIdentity.IsEntryIntact(entryPath))
            {
                return false;
            }

            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            const int maxCopyAttempts = 6;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(entryPath, destFile, true);
                    // File.Copy carries the entry's write time across, which
                    // is when the cache was populated and therefore older than
                    // the source.  Stamp the restored artefact as freshly
                    // written so the calling target's Inputs/Outputs check
                    // sees it as up to date and skips entirely next build,
                    // instead of re-running to serve the same hit forever.
                    try { File.SetLastWriteTimeUtc(destFile, DateTime.UtcNow); } catch { }
                    MarkCacheEntryUsed(entryPath, isDirectory: false);
                    return true;
                }
                catch (IOException) when (attempt < maxCopyAttempts)
                {
                    Thread.Sleep(40 * (int)Math.Pow(2, attempt - 1));
                }
            }
        }

        // Warm-cache fast path (no contention): serve the entry directly.
        if (TryServeCacheEntry(cachePath, this.OutputFile))
        {
            this.Restored = "true";
            this.Log.LogMessage(MessageImportance.High, "[cache hit] {0} -> {1} ({2})", cachePath, this.OutputFile, parametersHash);
            return true;
        }

        // Cold miss.  Under a multi-process (-m) build several projects can
        // reach the same asset at the same instant; without coordination they
        // would all observe this empty-cache miss and redundantly launch the
        // external encoder.  Acquire a cross-process lock (a DeleteOnClose
        // lock file beside the cache entry, held with FileShare.None) so only
        // ONE builder encodes while the rest wait and then serve the freshly
        // written entry.  The lock is thread-independent (a file handle, not a
        // Mutex) so it can safely be handed from this task to _SaveToContentCache
        // even if MSBuild runs them on different threads.
        int timeoutMs = this.LockTimeoutMilliseconds > 0 ? this.LockTimeoutMilliseconds : 300000;
        var lockPath = cachePath + ".lock";
        FileStream lockStream = null;
        try
        {
            var partitionDir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(partitionDir))
            {
                Directory.CreateDirectory(partitionDir);
            }

            // NOTE: This process's own earlier encode failed, so the save that would release its
            // lock never ran, and no wait would ever end: the encode is this build's again
            lockStream = AppDomain.CurrentDomain.GetData("ContentCacheLock::" + cachePath) as FileStream;
            AppDomain.CurrentDomain.SetData("ContentCacheLock::" + cachePath, null);

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
                    this.Log.LogMessage(MessageImportance.High, "Waiting for another build encoding {0}...", this.SourceFile);
                }

                if (this._isCanceled)
                {
                    break;
                }

                if (sw.ElapsedMilliseconds >= timeoutMs)
                {
                    // Give up waiting so a stuck/crashed peer can never deadlock
                    // the build; degrade to an independent encode below.
                    this.Log.LogMessage(MessageImportance.High,
                        "[cache lock timeout] {0} ({1}); encoding independently", this.SourceFile, parametersHash);
                    break;
                }
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 200);
            }
        }
        catch
        {
            // Locking is strictly best-effort: any failure falls back to a
            // plain independent encode, which is always correct.
            if (lockStream != null) { try { lockStream.Dispose(); } catch { } }
            lockStream = null;
        }

        if (lockStream != null)
        {
            // Re-check under the lock: a peer may have populated the entry
            // while we waited, in which case we serve it and skip the encode.
            if (TryServeCacheEntry(cachePath, this.OutputFile))
            {
                try { lockStream.Dispose(); } catch { }
                this.Restored = "true";
                this.Log.LogMessage(MessageImportance.High, "[cache hit] {0} -> {1} ({2})", cachePath, this.OutputFile, parametersHash);
                return true;
            }

            // Still a miss: we own the encode.  Park the held lock so
            // _SaveToContentCache releases it AFTER the entry is written,
            // at which point waiting peers acquire it, re-check, and hit.
            AppDomain.CurrentDomain.SetData("ContentCacheLock::" + cachePath, lockStream);
        }

        // Reported as a MISS (Restored stays "false"): the encode runs next.

        return !this.Log.HasLoggedErrors;
    }
}
