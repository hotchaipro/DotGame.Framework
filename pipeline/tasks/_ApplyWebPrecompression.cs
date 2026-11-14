// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _ApplyWebPrecompression - after a web publish whose files were precompressed (the static web
// assets SDK's .br or .gz copy of each), tells the page so: the loaders then fetch the copies and
// check how they arrived (docs/guide/platforms.md, "Hosting"). The page's meta names the format
// and the extensions of the files that have copies; its preloads point at the copies; and a host
// that takes a _headers file (Cloudflare, Netlify) gets the rule that sends a .br as Brotli.
public sealed class _ApplyWebPrecompression : Task
{
    // The folders the loaders fetch from: content, and the runtime's boot resources
    private static readonly string[] LoadedFolders = { "Content", "_framework" };

    private const string HeadersRule = "/*.br\n  Content-Encoding: br\n";

    [Required]
    public string WebRoot { get; set; }

    [Required]
    public string Page { get; set; }

    // br, or gz for the fallback, which decodes in the page where the host doesn't
    [Required]
    public string Format { get; set; }

    public override bool Execute()
    {
        if ((this.Format != "br") && (this.Format != "gz"))
        {
            this.Log.LogError("_ApplyWebPrecompression: Format is br or gz, not '{0}'.", this.Format);
            return false;
        }

        string suffix = "." + this.Format;
        var files = new List<string>();
        foreach (string folder in LoadedFolders)
        {
            string path = Path.Combine(this.WebRoot, folder);
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.GetFiles(path, "*", SearchOption.AllDirectories));
            }
        }

        if (!this.NameCopiesAfterTheirFiles(files, suffix))
        {
            return false;
        }

        // Every extension a copy was made for: the SDK compresses all of a type or none of it
        var copied = new HashSet<string>(StringComparer.Ordinal);
        var originals = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && originals.Contains(file.Substring(0, file.Length - suffix.Length)))
            {
                copied.Add(Path.GetExtension(file.Substring(0, file.Length - suffix.Length)).ToLowerInvariant());
            }
        }

        copied.Remove(string.Empty);
        if (copied.Count == 0)
        {
            this.Log.LogMessage(MessageImportance.Low, "_ApplyWebPrecompression: no {0} copies in '{1}', so the page fetches the files as they are.", suffix, this.WebRoot);
            return true;
        }

        // A file of one of those types without its copy would be fetched by a name that isn't there
        var missing = files
            .Where(file => !file.EndsWith(".br", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            .Where(file => copied.Contains(Path.GetExtension(file).ToLowerInvariant()) && !originals.Contains(file + suffix))
            .ToList();
        if (missing.Count > 0)
        {
            this.Log.LogError(
                "The web publish precompressed {0} files but not these, which the page would fetch by their {1} names: {2}",
                string.Join(" ", copied.OrderBy(e => e, StringComparer.Ordinal)), suffix,
                string.Join(", ", missing.Take(10).Select(file => this.RelativePath(file))));
            return false;
        }

        string pagePath = Path.Combine(this.WebRoot, this.Page);
        if (!File.Exists(pagePath))
        {
            this.Log.LogError("_ApplyWebPrecompression: the page '{0}' isn't in the publish.", pagePath);
            return false;
        }

        string extensions = string.Join(" ", copied.OrderBy(e => e, StringComparer.Ordinal));
        string page = File.ReadAllText(pagePath);
        page = Regex.Replace(page, @"\s*<meta\s+name=""dotgame-precompressed""[^>]*>", string.Empty);
        int head = page.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (head < 0)
        {
            this.Log.LogError("_ApplyWebPrecompression: the page '{0}' has no </head>.", pagePath);
            return false;
        }

        string meta = $"    <meta name=\"dotgame-precompressed\" content=\"{this.Format}\" data-extensions=\"{extensions}\">\n";
        page = page.Substring(0, head) + meta + page.Substring(head);

        // A preload fetches what the loader will: the copy
        page = Regex.Replace(page, @"(<link\b[^>]*\brel=""preload""[^>]*\bhref="")([^""]+)("")", match =>
        {
            string href = match.Groups[2].Value;
            string path = href.Split('?', '#')[0];
            bool loaded = LoadedFolders.Any(folder => path.StartsWith(folder + "/", StringComparison.Ordinal));
            return (loaded && copied.Contains(Path.GetExtension(path).ToLowerInvariant()) && !href.EndsWith(suffix, StringComparison.Ordinal))
                ? match.Groups[1].Value + href + suffix + match.Groups[3].Value
                : match.Value;
        });
        File.WriteAllText(pagePath, page, new UTF8Encoding(false));

        // The page's own copies are of the page before this, and a host that serves copies would send those
        foreach (string stale in new[] { pagePath + ".br", pagePath + ".gz" })
        {
            if (File.Exists(stale))
            {
                File.Delete(stale);
            }
        }

        // The game's _headers, or one of its own: a host that reads one sends a .br as Brotli
        if (this.Format == "br")
        {
            string headersPath = Path.Combine(this.WebRoot, "_headers");
            string headers = File.Exists(headersPath) ? File.ReadAllText(headersPath).Replace("\r\n", "\n") : string.Empty;
            if (!headers.Contains("/*.br\n"))
            {
                string rule = "\n# Precompressed files (DotGame's web publish), sent as they are, as Brotli\n" + HeadersRule;
                File.WriteAllText(headersPath, headers.TrimEnd('\n') + "\n" + rule, new UTF8Encoding(false));
            }
        }

        this.Log.LogMessage(MessageImportance.Normal, "The page fetches {0} copies of {1} files.", suffix, extensions);
        return true;
    }

    // The SDK names a copy by its own fingerprint of the file (name.<fingerprint>.ext.br), which is
    // the file's own name only where the file has that fingerprint too (the runtime's): content has
    // the content pipeline's (name.<hash>.ext). Each copy whose file isn't its name without the
    // suffix is renamed after the one file in its folder with its name and extension.
    private bool NameCopiesAfterTheirFiles(List<string> files, string suffix)
    {
        var names = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var sdkName = new Regex(@"^(?<stem>.+)\.(?<fingerprint>[a-z0-9]{10})(?<extension>\.[^.]+)" + Regex.Escape(suffix) + "$", RegexOptions.IgnoreCase);
        for (int index = 0; index < files.Count; index++)
        {
            string copy = files[index];
            if (!copy.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || names.Contains(copy.Substring(0, copy.Length - suffix.Length)))
            {
                continue;
            }

            var match = sdkName.Match(Path.GetFileName(copy));
            string folder = Path.GetDirectoryName(copy);
            var named = match.Success
                ? Directory.GetFiles(folder, match.Groups["stem"].Value + ".*" + match.Groups["extension"].Value)
                    .Where(file => Path.GetFileName(file).Length > match.Groups["stem"].Value.Length + match.Groups["extension"].Value.Length + 1)
                    .Where(file => Path.GetFileNameWithoutExtension(file).IndexOf('.', match.Groups["stem"].Value.Length + 1) < 0)
                    .ToList()
                : new List<string>();

            // The files without a copy of their own; or, publishing again into the same folder, the one
            // file whose copy an earlier publish renamed, which this copy replaces
            var candidates = named.Where(file => !names.Contains(file + suffix)).ToList();
            if ((candidates.Count == 0) && (named.Count == 1))
            {
                candidates = named;
            }
            if (candidates.Count != 1)
            {
                this.Log.LogError("The web publish's copy '{0}' isn't of exactly one file in its folder ({1} with its name and extension).", this.RelativePath(copy), candidates.Count);
                return false;
            }

            string renamed = candidates[0] + suffix;
            if (File.Exists(renamed))
            {
                File.Delete(renamed);
            }
            File.Move(copy, renamed);
            names.Remove(copy);
            names.Add(renamed);
            files[index] = renamed;
        }

        return true;
    }

    private string RelativePath(string file)
    {
        return file.Substring(this.WebRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
    }
}
