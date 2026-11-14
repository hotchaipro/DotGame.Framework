// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _GenerateWebPage - writes a game's page: what's about the game from its own page, if it has one,
// and everything the same in every game from here. A tag the game wrote wins over a generated one.
public sealed class _GenerateWebPage : Task
{
    private const string Fingerprint = "#[.{fingerprint}]";

    private const string StandardStyle = @"<style>
        html, body {
            height: 100vh;
            height: 100dvh;
            margin: 0;
            padding: 0;
            /* No scrollbars or document scrolling, pull-to-refresh or rubber-banding */
            overflow: hidden;
            overscroll-behavior: none;
            /* All touch input goes to the game: no browser panning, pinching or double-tap zoom */
            touch-action: none;
            /* No text selection or long-press callouts */
            user-select: none;
            -webkit-user-select: none;
            -webkit-touch-callout: none;
            -webkit-tap-highlight-color: transparent;
        }

        @media (display-mode: standalone), (display-mode: fullscreen) {
            html, body {
                height: 100lvh;
            }
        }

        canvas {
            display: block;
            min-width: 0;
            min-height: 0;
            width: 100%;
            height: 100%;
            touch-action: none;
            user-select: none;
            -webkit-user-select: none;
            -webkit-touch-callout: none;
            -webkit-tap-highlight-color: transparent;
            outline: none;
            background-color: transparent;
        }
    </style>";

    // The name a page already has, or the tag to add when it hasn't
    private static readonly string[][] StandardMeta =
    {
        new[] { @"<meta\s+charset", "<meta charset=\"utf-8\">" },
        new[] { @"name=[""']viewport[""']", "<meta name=\"viewport\" content=\"initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover\">" },
        new[] { @"name=[""']mobile-web-app-capable[""']", "<meta name=\"mobile-web-app-capable\" content=\"yes\">" },
        new[] { @"name=[""']apple-mobile-web-app-capable[""']", "<meta name=\"apple-mobile-web-app-capable\" content=\"yes\">" },
        new[] { @"name=[""']apple-mobile-web-app-status-bar-style[""']", "<meta name=\"apple-mobile-web-app-status-bar-style\" content=\"black-translucent\">" },
        new[] { @"name=[""']color-scheme[""']", "<meta name=\"color-scheme\" content=\"light\">" },
    };

    // The game's page, if it has one
    public string SourcePage { get; set; }

    [Required]
    public string OutputPage { get; set; }

    [Required]
    public string Title { get; set; }

    // The web manifest's file name, if the game has one
    public string ManifestName { get; set; }

    // Paths under the web root, without a fingerprint, as _dotgame/main.js
    public ITaskItem[] StartupModules { get; set; }

    [Required]
    public string MainModule { get; set; }

    // Paths under the web root, as StartupModules: modules the page runs before MainModule, in
    // order (a package's code that must be running before the game starts, such as its crash
    // reporter). An item's Config metadata is the script's data-config attribute, for the module
    // to read
    public ITaskItem[] PageModules { get; set; }

    // Output stems, without an extension, as wwwroot\Content\Fonts\basefont: the stem's file and
    // pages. Files come from ContentOutputDir, so an atlas's pages needn't be known
    public ITaskItem[] PreloadContent { get; set; }

    public string ContentOutputDir { get; set; }

    // Whether the launch screen's step follows, which needs its placeholders
    public bool HasLaunchScreen { get; set; }

    public override bool Execute()
    {
        string page = !string.IsNullOrEmpty(this.SourcePage) && File.Exists(this.SourcePage)
            ? File.ReadAllText(this.SourcePage)
            : "<!DOCTYPE html>\n<html>\n<head>\n</head>\n<body>\n</body>\n</html>\n";

        var head = new StringBuilder();
        void AddHead(string tag) => head.Append("    ").Append(tag).Append('\n');

        if (this.HasLaunchScreen && !page.Contains("<!-- launch-screen:head -->"))
        {
            AddHead("<!-- launch-screen:head -->");
        }

        if (!Regex.IsMatch(page, @"<title[\s>]", RegexOptions.IgnoreCase))
        {
            AddHead("<title>" + System.Net.WebUtility.HtmlEncode(this.Title) + "</title>");
        }

        foreach (var meta in StandardMeta)
        {
            if (!Regex.IsMatch(page, meta[0], RegexOptions.IgnoreCase))
            {
                AddHead(meta[1]);
            }
        }

        // NOTE: Before any style of the game's, which can then change it
        AddHead(StandardStyle);

        if (!string.IsNullOrEmpty(this.ManifestName) && !Regex.IsMatch(page, @"rel=[""']manifest[""']", RegexOptions.IgnoreCase))
        {
            AddHead("<link rel=\"manifest\" href=\"" + WithFingerprint(this.ManifestName) + "\">");
        }

        // NOTE: The import map comes before the module preloads. After them, the browser has
        // already resolved their whole graph, dotnet.js too, and drops the map's rule for it
        var preloads = new StringBuilder();
        bool hasImportMap = Regex.IsMatch(page, @"type=[""']importmap[""']", RegexOptions.IgnoreCase);
        if (!hasImportMap)
        {
            preloads.Append("    <script type=\"importmap\"></script>\n");
        }

        foreach (var module in this.StartupModules ?? Array.Empty<ITaskItem>())
        {
            string href = WithFingerprint(module.ItemSpec.Replace('\\', '/'));
            if (!page.Contains(href))
            {
                preloads.Append("    <link rel=\"modulepreload\" href=\"").Append(href).Append("\">\n");
            }
        }

        foreach (string file in this.FindPreloadedFiles())
        {
            string href = WithFingerprint(file);
            if (!page.Contains(href))
            {
                preloads.Append("    <link rel=\"preload\" as=\"fetch\" crossorigin=\"anonymous\" href=\"").Append(href).Append("\">\n");
            }
        }

        // NOTE: Module scripts run in the page's order, so the page's modules run before main.js
        var scripts = new StringBuilder();
        foreach (var module in this.PageModules ?? Array.Empty<ITaskItem>())
        {
            string href = WithFingerprint(module.ItemSpec.Replace('\\', '/'));
            if (Regex.IsMatch(page, @"<script[^>]*\bsrc=[""'][^""']*" + Regex.Escape(href), RegexOptions.IgnoreCase))
            {
                continue;
            }
            scripts.Append("    <script type=\"module\" src=\"").Append(href).Append('"');
            string config = module.GetMetadata("Config");
            if (!string.IsNullOrEmpty(config))
            {
                scripts.Append(" data-config=\"").Append(System.Net.WebUtility.HtmlEncode(config)).Append('"');
            }
            scripts.Append("></script>\n");
        }

        string mainHref = WithFingerprint(this.MainModule);
        var mainScript = new Regex(@"[ \t]*<script[^>]*\bsrc=[""'][^""']*" + Regex.Escape(mainHref), RegexOptions.IgnoreCase);
        bool pageHasMain = mainScript.IsMatch(page);
        if (!pageHasMain)
        {
            scripts.Append("    <script type=\"module\" src=\"").Append(mainHref).Append("\"></script>\n");
        }
        string script = scripts.ToString();

        // In the game's head: these first, so its own tags can follow and change them. The
        // preloads go with them, or follow a game's own import map
        if (!hasImportMap)
        {
            head.Append(preloads);
        }

        page = this.InsertInto(page, "head", head.ToString(), atEnd: false);

        if (hasImportMap)
        {
            var importMap = Regex.Match(page, @"<script[^>]*type=[""']importmap[""'][^>]*>\s*</script>[ \t]*\r?\n?", RegexOptions.IgnoreCase);
            page = page.Insert(importMap.Index + importMap.Length, preloads.ToString());
        }

        // A page with its own main.js script runs the page's modules just before it
        if (pageHasMain)
        {
            page = page.Insert(mainScript.Match(page).Index, script);
        }
        else
        {
            page = this.InsertInto(page, "head", script, atEnd: true);
        }

        var body = new StringBuilder();
        if (this.HasLaunchScreen && !page.Contains("<!-- launch-screen:body -->"))
        {
            body.Append("    <!-- launch-screen:body -->\n");
        }

        if (!Regex.IsMatch(page, @"<canvas[\s>]", RegexOptions.IgnoreCase))
        {
            body.Append("    <canvas id=\"canvas\"></canvas>\n");
        }

        page = this.InsertInto(page, "body", body.ToString(), atEnd: false);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(this.OutputPage)));
        if (!File.Exists(this.OutputPage) || (File.ReadAllText(this.OutputPage) != page))
        {
            File.WriteAllText(this.OutputPage, page);
        }

        return !this.Log.HasLoggedErrors;
    }

    private string InsertInto(string page, string element, string text, bool atEnd)
    {
        if (text.Length == 0)
        {
            return page;
        }

        var match = atEnd
            ? Regex.Match(page, @"[ \t]*</" + element + ">", RegexOptions.IgnoreCase)
            : Regex.Match(page, @"<" + element + @"(\s[^>]*)?>\s*", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            this.Log.LogError("The page '{0}' has no <{1}> element.", this.SourcePage, element);
            return page;
        }

        return page.Insert(atEnd ? match.Index : match.Index + match.Length, text);
    }

    // a/b.c.js -> a/b.c#[.{fingerprint}].js, the fingerprint before the last extension
    private static string WithFingerprint(string path)
    {
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        return (dot > slash) ? path.Substring(0, dot) + Fingerprint + path.Substring(dot) : path + Fingerprint;
    }

    private IEnumerable<string> FindPreloadedFiles()
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        string webRoot = Path.Combine(this.ContentOutputDir ?? "", "wwwroot");
        foreach (var item in this.PreloadContent ?? Array.Empty<ITaskItem>())
        {
            string stem = item.ItemSpec.Replace('\\', '/');
            const string WebRootPrefix = "wwwroot/";
            if (stem.StartsWith(WebRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem.Substring(WebRootPrefix.Length);
            }

            string directory = Path.Combine(webRoot, Path.GetDirectoryName(stem) ?? "");
            if (!Directory.Exists(directory))
            {
                continue;
            }

            // The file itself, an atlas's or font's numbered pages, and a shader's stages
            var name = new Regex("^" + Regex.Escape(Path.GetFileName(stem)) + @"(_\d+)?(\.[^.]+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (name.IsMatch(Path.GetFileName(file)))
                {
                    files.Add(file.Substring(webRoot.Length).Replace('\\', '/').TrimStart('/'));
                }
            }
        }

        return files;
    }
}
