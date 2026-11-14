// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

public sealed class _GenerateContentFingerprints : Task
{
    [Required]
    public string ContentRoot { get; set; }

    [Required]
    public string BasePath { get; set; }

    [Required]
    public string OutputCsFile { get; set; }

    // What each file hashed to, by path, length and write time, so an unchanged file isn't read
    // again: a no-op build's cost otherwise grows with all the game's content bytes
    public string CacheFile { get; set; }

    [Output]
    public ITaskItem[] Fingerprints { get; set; }

    private Dictionary<string, (long Length, long Ticks, string Hash)> ReadHashCache()
    {
        var cache = new Dictionary<string, (long, long, string)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(this.CacheFile) || !File.Exists(this.CacheFile))
        {
            return cache;
        }

        foreach (string line in File.ReadAllLines(this.CacheFile))
        {
            string[] parts = line.Split('\t');
            if ((parts.Length == 4) && long.TryParse(parts[1], out long length) && long.TryParse(parts[2], out long ticks))
            {
                cache[parts[0]] = (length, ticks, parts[3]);
            }
        }

        return cache;
    }

    private void WriteHashCache(List<string> lines)
    {
        if (string.IsNullOrEmpty(this.CacheFile))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(this.CacheFile)));
        File.WriteAllLines(this.CacheFile, lines);
    }

    public override bool Execute()
    {
        var rootFull = Path.GetFullPath(this.ContentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var basePrefix = (this.BasePath ?? string.Empty).Replace('\\', '/');
        if (basePrefix.Length > 0 && !basePrefix.EndsWith("/"))
        {
            basePrefix += "/";
        }

        var files = new List<string>();
        if (Directory.Exists(rootFull))
        {
            files.AddRange(Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories));
        }

        var hashCache = this.ReadHashCache();
        var hashLines = new List<string>(files.Count);
        var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resultItems = new List<Microsoft.Build.Utilities.TaskItem>();

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            string hashHex;
            if (hashCache.TryGetValue(file, out var cached) && (cached.Length == info.Length) && (cached.Ticks == info.LastWriteTimeUtc.Ticks))
            {
                hashHex = cached.Hash;
            }
            else
            {
                byte[] hash = null;
                // The fingerprint pass runs immediately AfterTargets="BuildContent".
                // On Windows, AV scanners (and occasionally the encoder subprocess
                // itself, during handle teardown) may briefly hold the just-written
                // content file open, which causes File.OpenRead to fail with
                // "being used by another process". Open with FileShare.ReadWrite |
                // Delete and retry on IOException to ride through the transient
                // lock instead of failing the build.
                const int maxAttempts = 10;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var sha = SHA256.Create())
                        {
                            hash = sha.ComputeHash(stream);
                        }
                        break;
                    }
                    catch (IOException) when (attempt < maxAttempts)
                    {
                        System.Threading.Thread.Sleep(100 * attempt);
                    }
                }

                hashHex = BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
            }

            hashLines.Add(file + "\t" + info.Length + "\t" + info.LastWriteTimeUtc.Ticks + "\t" + hashHex);

            var rel = file.Substring(rootFull.Length).Replace('\\', '/');

            int lastDot = rel.LastIndexOf('.');
            int lastSlash = rel.LastIndexOf('/');
            string stem = (lastDot > lastSlash && lastDot >= 0) ? rel.Substring(0, lastDot) : rel;
            string suffix = (lastDot > lastSlash && lastDot >= 0) ? rel.Substring(lastDot) : string.Empty;

            // The literal fingerprinted URL, which is what the generated
            // ContentFingerprints map hands to the runtime loaders.
            string fingerprintedRel = stem + "." + hashHex + suffix;

            // The same URL expressed as a static web asset fingerprint
            // expression. The SDK resolves the '#[.{fingerprint}]' token that
            // index.html (and any other asset-referencing file) writes, but
            // only for assets whose RelativePath declares the token -- an
            // asset carrying a pre-baked literal hash has nothing to bind to,
            // so those references were emitted unfingerprinted and 404'd.
            // The '=' form supplies the pipeline's own content hash instead of
            // letting the SDK compute one, so the URL stays identical to the
            // one in the map above.
            string tokenizedRel = stem + "#[.{fingerprint=" + hashHex + "}]!" + suffix;

            var item = new Microsoft.Build.Utilities.TaskItem(file);
            item.SetMetadata("RelativePath", tokenizedRel);
            item.SetMetadata("OriginalRelativePath", rel);
            resultItems.Add(item);

            if (basePrefix.Length > 0 && rel.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var keyRel = rel.Substring(basePrefix.Length);
                var fingerprintedKeyRel = fingerprintedRel.Substring(basePrefix.Length);
                entries[keyRel] = fingerprintedKeyRel;
            }
        }

        this.WriteHashCache(hashLines);

        this.Fingerprints = resultItems.ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        sb.AppendLine("namespace DotGame.Framework.Generated;");
        sb.AppendLine();
        sb.AppendLine("internal static class ContentFingerprints");
        sb.AppendLine("{");
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.AppendLine("        var manifest = new global::DotGame.Framework.Content.ContentManifest(\"DotGame.ContentFingerprints\");");
        foreach (var kvp in entries)
        {
            sb.Append("        manifest.Add(\"").Append(EscapeCs(kvp.Key)).Append("\", \"").Append(EscapeCs(kvp.Value)).AppendLine("\");");
        }

        sb.AppendLine("        global::DotGame.Framework.Content.ContentManifest.Register(manifest);");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        var outputCsFull = Path.GetFullPath(this.OutputCsFile);
        var outputDir = Path.GetDirectoryName(outputCsFull);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var newContent = sb.ToString();
        var existing = File.Exists(outputCsFull) ? File.ReadAllText(outputCsFull, Encoding.UTF8) : null;
        if (!string.Equals(existing, newContent, StringComparison.Ordinal))
        {
            File.WriteAllText(outputCsFull, newContent, new UTF8Encoding(false));
        }

        static string EscapeCs(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        return !this.Log.HasLoggedErrors;
    }
}
