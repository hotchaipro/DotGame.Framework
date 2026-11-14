// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _SaveToContentCache – stores a freshly encoded artefact into the shared
// content cache using the CachePath computed by _TryRestoreFromContentCache
// on the preceding miss.  Writes atomically (temp file + move) so a
// concurrent reader never observes a partial entry, tolerates the entry
// already existing (another build populated it first), and seeds the
// ignore file once.
//
// Any failure is swallowed to a low-importance log message: a cache
// populate is best-effort and must never fail an otherwise-successful
// encode.
public sealed class _SaveToContentCache : Task
{
    public string IgnoreFolder { get; set; }

    [Required]
    public string OutputFile { get; set; }

    [Required]
    public string SourceFile { get; set; }

    public string CachePath { get; set; }

    public string CacheDir { get; set; }

    public override bool Execute()
    {
        // Release the cross-process encode lock (if this builder took it in
        // _TryRestoreFromContentCache) AFTER the entry is written, so waiting
        // peers acquire it, re-check, and serve the hit instead of encoding.
        // Runs via finally so a blank/early-return save still frees the lock.
        var lockKey = string.IsNullOrEmpty(this.CachePath) ? null : "ContentCacheLock::" + this.CachePath;
        try
        {
            if (string.IsNullOrWhiteSpace(this.CacheDir) || string.IsNullOrWhiteSpace(this.CachePath) ||
                string.IsNullOrEmpty(this.OutputFile) || !File.Exists(this.OutputFile))
            {
                return true;
            }

            try
            {
                // Seed the ignore file once.
                ContentToolIdentity.SeedIgnoreFile(string.IsNullOrEmpty(this.IgnoreFolder) ? this.CacheDir : this.IgnoreFolder);

                var partition = Path.GetDirectoryName(this.CachePath);
                if (!string.IsNullOrEmpty(partition))
                {
                    Directory.CreateDirectory(partition);
                }

                var tempPath = this.CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                const int maxAttempts = 6;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Copy(this.OutputFile, tempPath, true);
                        ContentToolIdentity.RecordEntrySize(this.CachePath, new FileInfo(tempPath).Length);
                        try
                        {
                            // File.Move overwrite overload is unavailable in the
                            // netstandard2.0 the tasks build for, so delete
                            // any existing entry first then do a plain move.
                            if (File.Exists(this.CachePath))
                            {
                                File.Delete(this.CachePath);
                            }

                            File.Move(tempPath, this.CachePath);
                        }
                        catch
                        {
                            if (File.Exists(tempPath))
                            {
                                try { File.Delete(tempPath); } catch { }
                            }
                            // Another concurrent build already produced the entry:
                            // identical bytes for this key, so treat as success.
                            if (!File.Exists(this.CachePath))
                            {
                                throw;
                            }
                        }
                        break;
                    }
                    catch (IOException) when (attempt < maxAttempts)
                    {
                        Thread.Sleep(40 * (int)Math.Pow(2, attempt - 1));
                    }
                }

                this.Log.LogMessage(MessageImportance.High, "[cache write] {0} -> {1}", this.SourceFile, this.CachePath);
            }
            catch (Exception ex)
            {
                this.Log.LogMessage(MessageImportance.High, "Content cache populate skipped for '{0}': {1}", this.OutputFile, ex.Message);
            }
        }
        finally
        {
            if (lockKey != null)
            {
                var held = AppDomain.CurrentDomain.GetData(lockKey) as IDisposable;
                if (held != null)
                {
                    AppDomain.CurrentDomain.SetData(lockKey, null);
                    try { held.Dispose(); } catch { }
                }
            }
        }

        return !this.Log.HasLoggedErrors;
    }
}
