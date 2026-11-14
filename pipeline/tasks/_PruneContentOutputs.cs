// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _PruneContentOutputs – deletes every file under OutputDir that no current
// content item produces, so content that was removed, renamed, re-encoded
// to another extension, or left by an older pipeline (such as the .br/.gz
// files the hand-rolled compression targets used to write here) doesn't
// linger in the output, where it would be staged into bin\, registered as
// a static web asset, and published.
//
// What's kept is exact, never a pattern over a name:
//   Files          exact output paths
//   SuffixGroups   a base path with each of the extensions in its Suffixes
//                  metadata, which may also be manifest sub-key entries
//                  ("vert.spv=.vert.spv"): a shader's per-stage outputs
//   PageGroups     a base path followed by "_<digits>" and the extension in
//                  its PageExtension metadata (atlas and font pages, which their
//                  processors renumber from 0 on every rebuild)
// All paths are relative to OutputDir and compared in full and
// case-insensitively, so separators, "..", and case never cause a miss.
public sealed class _PruneContentOutputs : Task
{
    // Where the outputs are listed from one build to the next
    [Required]
    public string ListFile { get; set; }

    [Required]
    public string OutputDir { get; set; }

    public ITaskItem[] Files { get; set; }

    public ITaskItem[] SuffixGroups { get; set; }

    public ITaskItem[] PageGroups { get; set; }

    public override bool Execute()
    {
        string root = Path.GetFullPath(this.OutputDir);
        if (!Directory.Exists(root))
        {
            return true;
        }

        string Full(string relativePath)
        {
            return Path.GetFullPath(Path.Combine(root, relativePath));
        }

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in this.Files ?? Array.Empty<ITaskItem>())
        {
            expected.Add(Full(item.ItemSpec));
        }

        foreach (var item in this.SuffixGroups ?? Array.Empty<ITaskItem>())
        {
            string basePath = Full(item.ItemSpec);
            foreach (string entry in item.GetMetadata("Suffixes").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // An entry is a suffix, or a manifest sub-key and its suffix ("vert.spv=.vert.spv")
                string suffix = entry.Trim();
                int separator = suffix.IndexOf('=');
                if (separator >= 0)
                {
                    suffix = suffix.Substring(separator + 1).Trim();
                }

                expected.Add(basePath + suffix);
            }
        }

        // Pages: by folder, the pattern each page group's files must match
        // (An atlas declares its pages once for each of its images, so they're deduplicated.)
        var pagePatterns = new List<KeyValuePair<string, Regex>>();
        var seenPageGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in this.PageGroups ?? Array.Empty<ITaskItem>())
        {
            string basePath = Full(item.ItemSpec);
            string extension = item.GetMetadata("PageExtension");
            if (!seenPageGroups.Add(basePath + "|" + extension))
            {
                continue;
            }

            string pattern = "^" + Regex.Escape(Path.GetFileName(basePath)) + "_[0-9]+" + Regex.Escape(extension) + "$";
            pagePatterns.Add(new KeyValuePair<string, Regex>(Path.GetDirectoryName(basePath), new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
        }

        bool IsProduced(string file)
        {
            if (expected.Contains(file))
            {
                return true;
            }

            string dir = Path.GetDirectoryName(file);
            string name = Path.GetFileName(file);
            foreach (var page in pagePatterns)
            {
                if (string.Equals(page.Key, dir, StringComparison.OrdinalIgnoreCase) && page.Value.IsMatch(name))
                {
                    return true;
                }
            }

            return false;
        }

        // What this build produced, including whichever numbered pages exist
        var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            if (IsProduced(file))
            {
                produced.Add(file);
            }
        }

        // NOTE: Only what the pipeline wrote before, and no longer writes, ever goes. The output
        // folder is a public property, and a custom target can write there too: a file the pipeline
        // doesn't recognise isn't its to delete.
        if (File.Exists(this.ListFile))
        {
            foreach (string line in File.ReadAllLines(this.ListFile))
            {
                if ((line.Length > 0) && !produced.Contains(line) && File.Exists(line))
                {
                    File.Delete(line);
                    this.Log.LogMessage(MessageImportance.High, "Removed stale content output '{0}'", line.Substring(root.Length).TrimStart('\\', '/'));
                }
            }
        }

        var list = new List<string>(produced);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(this.ListFile));
        File.WriteAllLines(this.ListFile, list);

        // Remove the folders that pruning emptied, deepest first
        var dirs = new List<string>(Directory.GetDirectories(root, "*", SearchOption.AllDirectories));
        dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (string dir in dirs)
        {
            if (Directory.GetFileSystemEntries(dir).Length == 0)
            {
                Directory.Delete(dir);
            }
        }

        return !this.Log.HasLoggedErrors;
    }
}
