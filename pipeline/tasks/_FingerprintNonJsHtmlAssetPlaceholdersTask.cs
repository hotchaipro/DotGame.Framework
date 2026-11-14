// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

public sealed class _FingerprintNonJsHtmlAssetPlaceholdersTask : Task
{
    [Required]
    public ITaskItem[] HtmlFiles { get; set; }

    [Required]
    public ITaskItem[] Endpoints { get; set; }

    [Required]
    public string OutputPath { get; set; }

    [Output]
    public ITaskItem[] RewrittenHtmlFiles { get; set; }

    public override bool Execute()
    {
        var assetRegex = new Regex("\"(?<fileName>[^\"]+)#\\[\\.\\{fingerprint\\}\\](?<fileExtension>[^\"]+)\"");
        var labelRegex = new Regex("\\{\\s*\"Name\"\\s*:\\s*\"label\"\\s*,\\s*\"Value\"\\s*:\\s*\"(?<value>[^\"]*)\"\\s*\\}", RegexOptions.IgnoreCase);
        var integrityRegex = new Regex("\\{\\s*\"Name\"\\s*:\\s*\"integrity\"\\s*,\\s*\"Value\"\\s*:\\s*\"(?<value>[^\"]*)\"\\s*\\}", RegexOptions.IgnoreCase);
        var modulePreloadRegex = new Regex("<link\\b[^>]*\\brel\\s*=\\s*(\"modulepreload\"|'modulepreload'|modulepreload)[^>]*>", RegexOptions.IgnoreCase);
        var hrefRegex = new Regex("\\bhref\\s*=\\s*\"(?<href>[^\"]*)\"", RegexOptions.IgnoreCase);
        var jsonUnicodeEscapeRegex = new Regex("\\\\u(?<code>[0-9a-fA-F]{4})");

        // Endpoint metadata reaches MSBuild as JSON, so characters such as '+'
        // arrive escaped (e.g. "\u002B"). Decode them before emitting into HTML.
        Func<string, string> decodeJson = value =>
            jsonUnicodeEscapeRegex.Replace(value ?? string.Empty,
                m => ((char)Convert.ToInt32(m.Groups["code"].Value, 16)).ToString());

        // Build label -> route and route -> integrity maps. JS/MJS endpoints are
        // tracked separately so the non-JS rewrite below can keep ignoring them
        // while the modulepreload rewrite can still resolve them.
        var labelToRoute = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var jsLabelToRoute = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var routeToIntegrity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in this.Endpoints)
        {
            var assetFile = endpoint.GetMetadata("AssetFile") ?? string.Empty;
            var isJs = assetFile.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                       assetFile.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase);

            // Skip endpoints with selectors (alternative representations like .gz/.br).
            var selectorsJson = endpoint.GetMetadata("Selectors");
            if (!string.IsNullOrEmpty(selectorsJson) && selectorsJson.Trim() != "[]")
            {
                continue;
            }

            var propertiesJson = endpoint.GetMetadata("EndpointProperties");
            if (string.IsNullOrEmpty(propertiesJson))
            {
                continue;
            }

            var route = endpoint.ItemSpec;

            var integrityMatch = integrityRegex.Match(propertiesJson);
            if (integrityMatch.Success && !routeToIntegrity.ContainsKey(route))
            {
                routeToIntegrity[route] = decodeJson(integrityMatch.Groups["value"].Value);
            }

            var labelMatch = labelRegex.Match(propertiesJson);
            if (!labelMatch.Success)
            {
                continue;
            }

            var label = labelMatch.Groups["value"].Value;
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            var map = isJs ? jsLabelToRoute : labelToRoute;
            if (map.TryGetValue(label, out var existing))
            {
                if (!string.Equals(existing, route, StringComparison.Ordinal))
                {
                    this.Log.LogMessage(MessageImportance.Low,
                        "Ambiguous fingerprinted endpoint for label '{0}': '{1}' and '{2}'; keeping first.",
                        label, existing, route);
                }
                continue;
            }

            map[label] = route;
        }

        if (!Directory.Exists(this.OutputPath))
        {
            Directory.CreateDirectory(this.OutputPath);
        }

        var rewritten = new List<ITaskItem>();
        foreach (var html in this.HtmlFiles)
        {
            var sourcePath = html.GetMetadata("FullPath");
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                rewritten.Add(html);
                continue;
            }

            var originalContent = File.ReadAllText(sourcePath);
            var replacedAny = false;
            var newContent = assetRegex.Replace(originalContent, match =>
            {
                var fileName = match.Groups["fileName"].Value;
                var fileExtension = match.Groups["fileExtension"].Value;
                var assetPath = fileName + fileExtension;

                // Leave the marker in place for JS/MJS so the SDK handles it.
                if (fileExtension.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                    fileExtension.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                if (labelToRoute.TryGetValue(assetPath, out var fingerprinted))
                {
                    replacedAny = true;
                    this.Log.LogMessage(MessageImportance.Low,
                        "Replacing HTML asset '{0}' with fingerprinted route '{1}'.",
                        assetPath, fingerprinted);
                    return "\"" + fingerprinted + "\"";
                }

                this.Log.LogMessage(MessageImportance.Low,
                    "No fingerprinted endpoint found for HTML asset '{0}'; leaving marker for SDK.",
                    assetPath);
                return match.Value;
            });

            // Stamp integrity on modulepreload links so the preloaded response can
            // be reused by the import-map-driven module fetch.
            newContent = modulePreloadRegex.Replace(newContent, match =>
            {
                var link = match.Value;
                if (link.IndexOf("integrity", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return link;
                }

                var hrefMatch = hrefRegex.Match(link);
                if (!hrefMatch.Success)
                {
                    return link;
                }

                var href = hrefMatch.Groups["href"].Value;

                // The href may still carry an unresolved fingerprint marker; resolve
                // it against the JS label map to find the real route.
                var markerMatch = assetRegex.Match("\"" + href + "\"");
                var lookup = href;
                if (markerMatch.Success)
                {
                    lookup = markerMatch.Groups["fileName"].Value + markerMatch.Groups["fileExtension"].Value;
                }

                string route;
                if (!jsLabelToRoute.TryGetValue(lookup, out route))
                {
                    route = lookup;
                }

                string integrity;
                if (!routeToIntegrity.TryGetValue(route, out integrity) &&
                    !routeToIntegrity.TryGetValue(lookup, out integrity))
                {
                    this.Log.LogMessage(MessageImportance.Low,
                        "No integrity found for modulepreload '{0}'.", href);
                    return link;
                }

                replacedAny = true;
                var head = link.Substring(0, link.Length - 1).TrimEnd();
                var selfClosing = head.EndsWith("/", StringComparison.Ordinal);
                if (selfClosing)
                {
                    head = head.Substring(0, head.Length - 1).TrimEnd();
                }

                return head + " integrity=\"" + integrity + "\"" + (selfClosing ? " />" : ">");
            });

            if (!replacedAny)
            {
                rewritten.Add(html);
                continue;
            }

            byte[] hashBytes;
            using (var sha = SHA256.Create())
            {
                hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sourcePath));
            }
            var nameHash = BitConverter.ToString(hashBytes, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
            var stagedPath = Path.Combine(this.OutputPath, nameHash + Path.GetExtension(sourcePath));

            if (!File.Exists(stagedPath) || File.ReadAllText(stagedPath) != newContent)
            {
                File.WriteAllText(stagedPath, newContent, new UTF8Encoding(false));
            }

            var stagedItem = new TaskItem(stagedPath, html.CloneCustomMetadata());
            stagedItem.SetMetadata("OriginalItemSpec", html.ItemSpec);
            rewritten.Add(stagedItem);
        }

        this.RewrittenHtmlFiles = rewritten.ToArray();

        return !this.Log.HasLoggedErrors;
    }
}
