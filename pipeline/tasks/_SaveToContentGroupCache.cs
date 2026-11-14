// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _SaveToContentGroupCache – stores the artefacts of a freshly built
// composite output into the entry directory computed by
// _TryRestoreFromContentGroupCache on the preceding miss.
//
// The member set is described declaratively rather than discovered by
// globbing the output directory, because sibling content can legitimately
// share a base name (a plain TextureContent "spritesheet.png" encoded
// beside a "spritesheet" atlas, say) and must not be swept into the entry:
//
//   Suffixes       fixed, always-present artefacts (e.g. ".json").
//   PageExtension  optional numbered series; "_0", "_1", … are collected
//                  until the first gap, covering the variable page /
//                  part count without the caller having to know it.
//
// Members are staged in a temp directory and published with a single
// Directory.Move so a concurrent reader never observes a partial entry.
// An entry that already exists (another build won the race) is left
// alone — identical bytes for this key — and any failure is swallowed to
// a log message, since a cache populate must never fail an otherwise
// successful build.
public sealed class _SaveToContentGroupCache : Task
{
    public string IgnoreFolder { get; set; }

    [Required]
    public string OutputBaseFile { get; set; }

    public string Suffixes { get; set; }

    public string PageExtension { get; set; }

    public string CachePath { get; set; }

    public string CacheDir { get; set; }

    public override bool Execute()
    {
        // Release the cross-process build lock (if this builder took it in
        // _TryRestoreFromContentGroupCache) AFTER the entry is written, so
        // waiting peers acquire it, re-check, and serve the hit.  Runs via
        // finally so a blank/early-return save still frees the lock.
        var lockKey = string.IsNullOrEmpty(this.CachePath) ? null : "ContentCacheLock::" + this.CachePath;
        try
        {
            if (string.IsNullOrWhiteSpace(this.CacheDir) || string.IsNullOrWhiteSpace(this.CachePath) ||
                string.IsNullOrEmpty(this.OutputBaseFile) || (ContentToolIdentity.ReadGroupEntry(this.CachePath) != null))
            {
                return true;
            }

            try
            {
                // Collect the artefacts that make up this entry.
                var members = new List<string>();
                if (!string.IsNullOrEmpty(this.Suffixes))
                {
                    foreach (var raw in this.Suffixes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var memberPath = this.OutputBaseFile + raw.Trim();
                        if (File.Exists(memberPath))
                        {
                            members.Add(memberPath);
                        }
                    }
                }
                if (!string.IsNullOrEmpty(this.PageExtension))
                {
                    for (int page = 0; ; page++)
                    {
                        var memberPath = this.OutputBaseFile + "_" + page + this.PageExtension;
                        if (!File.Exists(memberPath))
                        {
                            break;
                        }

                        members.Add(memberPath);
                    }
                }
                if (members.Count == 0)
                {
                    return true;
                }

                // Seed the ignore file once.
                ContentToolIdentity.SeedIgnoreFile(string.IsNullOrEmpty(this.IgnoreFolder) ? this.CacheDir : this.IgnoreFolder);

                var partition = Path.GetDirectoryName(this.CachePath);
                if (!string.IsNullOrEmpty(partition))
                {
                    Directory.CreateDirectory(partition);
                }

                // Stage into a temp directory, then publish atomically.  Members
                // are stored under their suffix relative to the output base name
                // so a restore can rebuild the full paths without knowing them.
                var baseName = Path.GetFileName(this.OutputBaseFile);
                var tempDir = this.CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(tempDir);
                    foreach (var member in members)
                    {
                        var suffix = Path.GetFileName(member).Substring(baseName.Length);
                        File.Copy(member, Path.Combine(tempDir, suffix), true);
                    }
                    ContentToolIdentity.RecordGroupSizes(tempDir, Directory.GetFiles(tempDir));

                    // An entry that isn't intact never restores, so it has to make way
                    if (Directory.Exists(this.CachePath) && (ContentToolIdentity.ReadGroupEntry(this.CachePath) == null))
                    {
                        Directory.Delete(this.CachePath, true);
                    }

                    try
                    {
                        Directory.Move(tempDir, this.CachePath);
                    }
                    catch
                    {
                        // Another concurrent build already published the entry:
                        // identical bytes for this key, so treat as success.
                        if (!Directory.Exists(this.CachePath))
                        {
                            throw;
                        }
                    }
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }

                this.Log.LogMessage(MessageImportance.High, "[cache write] {0} -> {1}", this.OutputBaseFile, this.CachePath);
            }
            catch (Exception ex)
            {
                this.Log.LogMessage(MessageImportance.High, "Content cache populate skipped for '{0}': {1}", this.OutputBaseFile, ex.Message);
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
