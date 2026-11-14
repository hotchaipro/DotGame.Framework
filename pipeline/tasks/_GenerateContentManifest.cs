// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _GenerateContentManifest – emits a `ContentManifest.g.cs` source file
// whose `[ModuleInitializer]` constructs a `ContentManifest` with a
// stable identity, adds every content-key → relative-path mapping
// produced by the build, and registers the manifest with
// `ContentManifest.Registry`.  Loaders call ResolveContentPath(key) to
// look up the actual relative path produced by the build.
//
// Inputs:
//   Entries          ITaskItem[] – ItemSpec = key, %(RelativePath) = value.
//                                  Use for single-output content types
//                                  (textures, sounds, data, etc.).
//   ShaderEntries    ITaskItem[] – Shader items, one per .slang source.
//                                  ItemSpec = key (no extension);
//                                  %(OutputRelativeDir) and %(Filename)
//                                  are used to compose sub-key paths.
//   ShaderSubKeys    string      – Semicolon-separated list of
//                                  `subKey=suffix` pairs (e.g.
//                                  "vert.spv=.vert.spv;frag.msl=.frag.msl").
//                                  When set, ShaderEntries are expanded
//                                  into one entry per sub-key keyed as
//                                  "<key>:<subKey>". When empty, callers
//                                  should pre-add a single primary
//                                  entry per shader to Entries.
//   BasePath         string      – Prefix stripped from key and value
//                                  so the manifest is independent of
//                                  the platform's content-root prefix.
//                                  A leading "wwwroot/" segment (the
//                                  static web assets SDK's marker for
//                                  the static web root, which never
//                                  appears in served paths) is
//                                  stripped first, so web projects
//                                  that link content under
//                                  "wwwroot\Content\..." share the
//                                  same "Content/" base path as
//                                  non-web platforms.
//   OutputCsFile     string      – Path of the .g.cs to write.
//   PropertyNames   string      – Semicolon-separated list of
//                                  metadata names to copy from each
//                                  entry in Entries into the
//                                  manifest entry's property bag.
//                                  Empty metadata values are skipped
//                                  (so the bag is omitted entirely
//                                  when no metadata is present on a
//                                  given row).  Property values are
//                                  emitted verbatim; the consuming
//                                  content type's helper class is
//                                  responsible for invariant-culture
//                                  parsing.
public sealed class _GenerateContentManifest : Task
{
    public ITaskItem[] Entries { get; set; }

    public ITaskItem[] ShaderEntries { get; set; }

    public string ShaderSubKeys { get; set; }

    [Required]
    public string OutputCsFile { get; set; }

    public string BasePath { get; set; }

    [Required]
    public string ManifestIdentity { get; set; }

    public string PropertyNames { get; set; }

    public override bool Execute()
    {
        var basePrefix = (this.BasePath ?? string.Empty).Replace('\\', '/');
        if (basePrefix.Length > 0 && !basePrefix.EndsWith("/"))
        {
            basePrefix += "/";
        }

        string StripBase(string s)
        {
            var v = (s ?? string.Empty).Replace('\\', '/');
            // A leading "wwwroot/" segment follows the static web assets
            // SDK convention: it marks the static web root and never
            // appears in served (runtime) paths.  Content in web projects
            // is linked under "wwwroot\Content\...", so drop the segment
            // before stripping the content root.  Non-web platforms don't
            // link content under wwwroot, making this a no-op for them.
            const string webRoot = "wwwroot/";
            if (v.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase))
            {
                v = v.Substring(webRoot.Length);
            }

            if (basePrefix.Length > 0 && v.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            {
                v = v.Substring(basePrefix.Length);
            }

            return v;
        }

        var map = new SortedDictionary<string, (string Key, string SubKey, string Path, List<KeyValuePair<string, string>> Properties)>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void AddRow(string key, string subKey, string path, List<KeyValuePair<string, string>> properties, string source = "")
        {
            var sortKey = subKey.Length == 0 ? key : key + ":" + subKey;

            // NOTE: The last would silently win, and the game would load the wrong file
            if (map.TryGetValue(sortKey, out var existing))
            {
                if (!string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase) || (source.Length > 0))
                {
                    sources.TryGetValue(sortKey, out string otherSource);
                    this.Log.LogError(
                        "Two content items have the key '{0}': '{1}' ({2}) and '{3}' ({4}). A key is a path without its extension: rename one, or give it Link metadata.",
                        key, otherSource, existing.Path, source, path);
                }
            }

            sources[sortKey] = source;
            map[sortKey] = (key, subKey, path, properties);
        }

        var propertyNames = new List<string>();
        foreach (var token in (this.PropertyNames ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim();
            if (trimmed.Length > 0)
            {
                propertyNames.Add(trimmed);
            }
        }

        foreach (var item in this.Entries ?? Array.Empty<Microsoft.Build.Framework.ITaskItem>())
        {
            var key = StripBase(item.ItemSpec);
            var value = StripBase(item.GetMetadata("RelativePath"));
            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            List<KeyValuePair<string, string>> props = null;
            foreach (var name in propertyNames)
            {
                var meta = item.GetMetadata(name);
                if (string.IsNullOrEmpty(meta))
                {
                    continue;
                }

                if (props == null)
                {
                    props = new List<KeyValuePair<string, string>>();
                }

                props.Add(new KeyValuePair<string, string>(name, meta));
            }
            AddRow(key, string.Empty, value, props, item.GetMetadata("Source"));
        }

        // Expand shader sub-keys, if configured. Each shader entry
        // becomes one manifest record per sub-key, registered via the
        // manifest's (key, path, subKey) API.
        var subKeys = new List<KeyValuePair<string, string>>();
        foreach (var token in (this.ShaderSubKeys ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1)
            {
                continue;
            }

            var subKey = token.Substring(0, eq).Trim();
            var suffix = token.Substring(eq + 1).Trim();
            if (subKey.Length == 0 || suffix.Length == 0)
            {
                continue;
            }

            subKeys.Add(new KeyValuePair<string, string>(subKey, suffix));
        }

        if (subKeys.Count > 0)
        {
            foreach (var item in this.ShaderEntries ?? Array.Empty<Microsoft.Build.Framework.ITaskItem>())
            {
                var dir = (item.GetMetadata("OutputRelativeDir") ?? string.Empty).Replace('\\', '/');
                var filename = item.GetMetadata("Filename") ?? string.Empty;
                if (filename.Length == 0)
                {
                    continue;
                }

                var baseKey = StripBase(dir + filename);
                foreach (var sk in subKeys)
                {
                    var value = StripBase(dir + filename + sk.Value);
                    AddRow(baseKey, sk.Key, value, null);
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        // A namespace of its own, not the game's: one named after the project would clash with a
        // class of that name in the global namespace
        sb.AppendLine("namespace DotGame.Framework.Generated;");
        sb.AppendLine();
        sb.AppendLine("internal static class ContentManifest");
        sb.AppendLine("{");
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.Append("        var manifest = new global::DotGame.Framework.Content.ContentManifest(\"").Append(EscapeCs(this.ManifestIdentity)).Append("\", \"").Append(EscapeCs(basePrefix)).AppendLine("\");");
        foreach (var kvp in map)
        {
            var row = kvp.Value;
            sb.Append("        manifest.Add(\"").Append(EscapeCs(row.Key)).Append("\", \"").Append(EscapeCs(row.Path)).Append("\"");
            if (row.SubKey.Length > 0)
            {
                sb.Append(", subKey: \"").Append(EscapeCs(row.SubKey)).Append("\"");
            }
            if (row.Properties != null && row.Properties.Count > 0)
            {
                sb.Append(", properties: new global::System.Collections.Generic.KeyValuePair<string, string>[] { ");
                for (int i = 0; i < row.Properties.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    var p = row.Properties[i];
                    sb.Append("new(\"").Append(EscapeCs(p.Key)).Append("\", \"").Append(EscapeCs(p.Value)).Append("\")");
                }
                sb.Append(" }");
            }
            sb.AppendLine(");");
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
