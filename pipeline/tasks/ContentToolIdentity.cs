// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DotGame.Framework.ContentPipeline.Tasks;

// What the content cache's key says about the encoders.
internal static class ContentToolIdentity
{
    private static readonly Dictionary<string, string> s_hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // The template with each tool's path replaced by its bare name, which leaves machine-specific
    // install locations out of the key, and with no .exe, so one tool on two systems has one name
    internal static string NormalizeTemplate(string commandTemplate, string toolPaths, out string toolHashes)
    {
        var normalized = commandTemplate ?? "";
        var hashes = new StringBuilder();
        if (!string.IsNullOrEmpty(toolPaths))
        {
            foreach (var raw in toolPaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var toolPath = raw.Trim().Trim('"');
                if (toolPath.Length == 0)
                {
                    continue;
                }

                var toolName = Path.GetFileName(toolPath);
                if (toolName.Length == 0)
                {
                    continue;
                }

                var bareName = toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? toolName.Substring(0, toolName.Length - 4)
                    : toolName;
                normalized = normalized.Replace(toolPath, bareName);

                // NOTE: The build tool's own version is its own key, and it isn't an encoder
                if (toolName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || bareName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hash = HashOf(toolPath);
                if (hash.Length > 0)
                {
                    hashes.Append('\n').Append(bareName).Append('=').Append(hash);
                }
            }
        }

        toolHashes = hashes.ToString();
        return normalized;
    }

    // The tool's contents, so an upgrade with a bug fix isn't served from entries made before it.
    // Once per process, per file as it stands.
    private static string HashOf(string toolPath)
    {
        string resolved = Resolve(toolPath);
        if (resolved == null)
        {
            return "";
        }

        var info = new FileInfo(resolved);
        string cacheKey = resolved + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        lock (s_hashes)
        {
            if (!s_hashes.TryGetValue(cacheKey, out string hash))
            {
                using (var stream = File.OpenRead(resolved))
                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(stream);
                    var text = new StringBuilder(bytes.Length * 2);
                    foreach (var b in bytes)
                    {
                        text.Append(b.ToString("x2"));
                    }

                    hash = text.ToString();
                }

                s_hashes[cacheKey] = hash;
            }

            return hash;
        }
    }

    private static string Resolve(string toolPath)
    {
        try
        {
            if (Path.IsPathRooted(toolPath))
            {
                return File.Exists(toolPath) ? toolPath : null;
            }

            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                foreach (var suffix in new[] { "", ".exe", ".cmd", ".bat" })
                {
                    if (directory.Length > 0)
                    {
                        var candidate = Path.Combine(directory, toolPath + suffix);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
        }
        catch (ArgumentException)
        {
            // A path with characters this system doesn't allow isn't a file
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    // Written once, and never again, so a project can change what git does with the folder
    internal static void SeedIgnoreFile(string folder)
    {
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, ".gitignore");
        if (File.Exists(path))
        {
            return;
        }

        var lines = new[]
        {
            "# Written once by the content pipeline, and not rewritten: everything here can be",
            "# regenerated, so nothing is committed unless you change this file.",
            "*"
        };

        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(string.Join("\n", lines) + "\n");
            }
        }
        catch (IOException)
        {
            // Another build wrote it first
        }
    }

    // An entry records its size when it's written, so one that isn't what was written is a miss,
    // whatever happened to it: cut short, emptied, or replaced by something that stores files for
    // git, which leaves a small text file in place of each one until they're fetched.
    private const string EntrySizeSuffix = ".size";

    private const string GroupSizesFile = ".sizes";

    private const int MaxRecordedSizeLength = 32;

    private const int MaxGroupSizesLength = 64 * 1024;

    internal static void RecordEntrySize(string entryPath, long size)
    {
        File.WriteAllText(entryPath + EntrySizeSuffix, size.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
    }

    internal static bool IsEntryIntact(string entryPath)
    {
        try
        {
            var entry = new FileInfo(entryPath);
            var recorded = new FileInfo(entryPath + EntrySizeSuffix);
            if (!entry.Exists || (entry.Length <= 0) || !recorded.Exists || (recorded.Length > MaxRecordedSizeLength))
            {
                return false;
            }

            return long.TryParse(File.ReadAllText(recorded.FullName), NumberStyles.None, CultureInfo.InvariantCulture, out long size)
                && (size == entry.Length);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Lists a group entry's members in its folder, which is staged and moved into place whole
    internal static void RecordGroupSizes(string entryDir, IEnumerable<string> memberPaths)
    {
        var lines = new List<string>();
        foreach (var member in memberPaths)
        {
            lines.Add(new FileInfo(member).Length.ToString(CultureInfo.InvariantCulture) + " " + Path.GetFileName(member));
        }

        File.WriteAllLines(Path.Combine(entryDir, GroupSizesFile), lines, new UTF8Encoding(false));
    }

    // The members of an intact group entry, or null if it's anything else
    internal static List<string> ReadGroupEntry(string entryDir)
    {
        try
        {
            var recorded = new FileInfo(Path.Combine(entryDir, GroupSizesFile));
            if (!recorded.Exists || (recorded.Length > MaxGroupSizesLength))
            {
                return null;
            }

            var members = new List<string>();
            foreach (var line in File.ReadAllLines(recorded.FullName))
            {
                int separator = line.IndexOf(' ');
                if ((separator <= 0)
                    || !long.TryParse(line.Substring(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long size))
                {
                    return null;
                }

                var name = line.Substring(separator + 1);
                var member = new FileInfo(Path.Combine(entryDir, name));
                if ((name.Length == 0) || (Path.GetFileName(name) != name) || !member.Exists || (member.Length != size))
                {
                    return null;
                }

                members.Add(member.FullName);
            }

            return (members.Count > 0) ? members : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
