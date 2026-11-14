// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace DotGame.ContentPipeline.Testing;

internal sealed class ContentItem(string type, string include)
{
    public string Type { get; } = type;

    public string Include { get; } = include;

    public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal);

    public ContentItem With(string name, string value)
    {
        this.Metadata[name] = value;
        return this;
    }
}

/// <summary>
/// What a run of MSBuild did.
/// </summary>
internal sealed record BuildResult(int ExitCode, string Output, List<ToolCall> Calls)
{
    public bool Succeeded => this.ExitCode == 0;

    public BuildResult ShouldSucceed()
    {
        Assert.True(this.Succeeded, $"The build failed:\n{this.Output}");
        return this;
    }

    public BuildResult ShouldFail()
    {
        Assert.False(this.Succeeded, $"The build succeeded:\n{this.Output}");
        return this;
    }

    public List<ToolCall> CallsOf(string kind) => this.Calls.FindAll(call => call.Kind == kind);
}

internal sealed record ManifestEntry(string Key, string Path, string SubKey, string Properties);

/// <summary>
/// A temporary project that imports the real content pipeline targets and runs them with the real
/// MSBuild. It isn't an SDK project, so it has none of an SDK build's cost: its Build target is
/// just what the pipeline hooks onto. Processor commands and tools point at the fake tool, unless a
/// test sets its own.
/// </summary>
internal sealed partial class ContentProject : IDisposable
{
    private static readonly Regex ManifestLine = ManifestRegex();
    private readonly TestDirectory _directory;
    private readonly List<ContentItem> _items = [];

    public ContentProject(string projectFolder = "proj", string suffix = "")
    {
        this._directory = new TestDirectory(suffix);
        this.ProjectDir = this._directory.Path_(projectFolder);
        Directory.CreateDirectory(this.ProjectDir);
        this.CacheDir = this._directory.Path_("cache");

        // The build tool's inputs, which the tests change in place of the real tool's
        this.ToolFile = this._directory.WriteFile("tool/tool.dll", "tool");
        this._directory.WriteFile("tool/Tool.csproj", "<Project />");
        this.VersionsFile = this._directory.Path_("tool", "ContentVersions.props");
        File.Copy(Path.Combine(Paths.FrameworkRoot, "pipeline", "tool", "ContentVersions.props"), this.VersionsFile);

        this.Properties["FFmpegExe"] = Paths.FakeTool;
        this.Properties["TextureContentLosslessProcessorCommand"] = Command("texture") + " --compression Lossless --premultiply {PremultiplyAlpha}";
        this.Properties["TextureContentNearLosslessProcessorCommand"] = Command("texture") + " --compression Lossless --quality {Quality} --premultiply {PremultiplyAlpha}";
        this.Properties["TextureContentLossyProcessorCommand"] = Command("texture") + " --compression Lossy --quality {Quality} --premultiply {PremultiplyAlpha}";
        this.Properties["ShaderContentProcessorCommand"] = Command("shader");

        // NOTE: Ahead of the commands that read it, as the project writes these in this order
        this.Properties["SoundContentSampleRate"] = "44100";
        this.Properties["SoundContentFlacProcessorCommand"] = Command("sound") + " --rate $(SoundContentSampleRate)";
        this.Properties["SoundContentWavProcessorCommand"] = Command("sound") + " --rate $(SoundContentSampleRate)" + " --wav";
        this.Properties["SoundContentLossyProcessorCommand"] = Command("sound") + " --lossy --quality {Quality} --rate $(SoundContentSampleRate)";
        this.Properties["MusicContentProcessorCommand"] = Command("music") + " --quality {Quality}";
    }

    public string Root => this._directory.Root;

    public string ProjectDir { get; }

    public string CacheDir { get; }

    public string ToolFile { get; }

    public string VersionsFile { get; }

    public string ContentOutputDir => Path.Combine(this.ProjectDir, "obj", "ContentPipeline", "Output");

    public string ContentIntermediateDir => Path.Combine(this.ProjectDir, "obj", "ContentPipeline", "Intermediate");

    public string StageDir => Path.Combine(this.ProjectDir, "bin");

    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Imports a library's content pipeline targets too (<see cref="Paths.LibraryContentTargetsOrNull"/>),
    /// with its real build tool beside these tests.
    /// </summary>
    public bool ImportLibrary { get; set; }

    /// <summary>Lets the targets find the cache's folder, rather than giving them one.</summary>
    public bool UseDefaultCacheFolder { get; set; }

    /// <summary>More project file text, after the imports.</summary>
    public string ExtraProjectXml { get; set; } = "";

    public static string Command(string kind) =>
        $"\"{Paths.FakeTool}\" {kind} --input \"{{InputFile}}\" --output \"{{OutputFile}}\"";

    public void SetToolConfig(string key, string? value) => this._directory.SetToolConfig(key, value);

    public List<ToolCall> AllToolCalls() => this._directory.ToolCalls();

    public string WriteSource(string relativePath, string text) => this._directory.WriteFile(Path.Combine(Path.GetFileName(this.ProjectDir), relativePath), text);

    /// <summary>A tenth of a second of noise as a WAV source, different for each <paramref name="seed"/>, for the commands that read sounds.</summary>
    public string WriteSound(string relativePath, int seed = 1)
    {
        string path = this.WriteSource(relativePath, "");
        return Tones.WriteWav(path, Tones.Noise(44100, 1, 4410, seed));
    }

    public ContentItem AddItem(string type, string include, string? link = null)
    {
        var item = new ContentItem(type, include);
        if (link is not null)
        {
            item.With("Link", link);
        }

        this._items.Add(item);
        return item;
    }

    public void RemoveItem(ContentItem item) => this._items.Remove(item);

    public ContentItem AddContent(string type, string name, string extension, string text = "source")
    {
        this.WriteSource($"src/{name}{extension}", text);
        return this.AddItem(type, $"src/{name}{extension}", $"Content\\{name}{extension}");
    }

    public string OutputPath(string relativePath) => Path.Combine(this.ContentOutputDir, relativePath.Replace('\\', Path.DirectorySeparatorChar));

    public string ReadOutput(string relativePath) => File.ReadAllText(this.OutputPath(relativePath));

    public IEnumerable<string> OutputFiles() =>
        Directory.Exists(this.ContentOutputDir)
            ? Directory.GetFiles(this.ContentOutputDir, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(this.ContentOutputDir, file).Replace('\\', '/')).Order(StringComparer.Ordinal)
            : [];

    /// <summary>Each output's relative path, write time and hash.</summary>
    public Dictionary<string, (DateTime WriteTime, string Hash)> SnapshotOutputs()
    {
        var snapshot = new Dictionary<string, (DateTime, string)>(StringComparer.Ordinal);
        foreach (string file in this.OutputFiles())
        {
            string path = this.OutputPath(file);
            snapshot[file] = (File.GetLastWriteTimeUtc(path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }

        return snapshot;
    }

    public List<ManifestEntry> Manifest()
    {
        var entries = new List<ManifestEntry>();
        foreach (string line in File.ReadAllLines(this.ManifestFile))
        {
            Match match = ManifestLine.Match(line);
            if (match.Success)
            {
                entries.Add(new ManifestEntry(match.Groups["key"].Value, match.Groups["path"].Value, match.Groups["sub"].Value, match.Groups["props"].Value));
            }
        }

        return entries;
    }

    public string ManifestFile => Path.Combine(this.ContentIntermediateDir, "ContentManifest.g.cs");

    /// <summary>Removes the cache, and the outputs and staged copies, leaving the sources: a clean clone.</summary>
    public void DeleteBuildOutputs()
    {
        foreach (string directory in new[] { Path.Combine(this.ProjectDir, "obj"), this.StageDir })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public BuildResult Build(params string[] targets)
    {
        return this.Run([], targets);
    }

    public BuildResult Run(string[] properties, params string[] targets)
    {
        int callsBefore = this.AllToolCalls().Count;
        (int exitCode, string output) = ProcessRunner.Run(Paths.DotNet, this.WriteProjectAndGetArguments(properties, targets), this.ProjectDir);
        List<ToolCall> calls = this.AllToolCalls();
        return new BuildResult(exitCode, output, calls.GetRange(callsBefore, calls.Count - callsBefore));
    }

    /// <summary>
    /// Starts a build to kill later, as a developer's Ctrl+C or a stopped CI job does. Its output
    /// goes to <paramref name="output"/>: unread, a full pipe would stop the build.
    /// </summary>
    public Process StartBuild(ConcurrentQueue<string> output)
    {
        Process build = ProcessRunner.Start(Paths.DotNet, this.WriteProjectAndGetArguments([], []), this.ProjectDir);
        build.OutputDataReceived += (_, line) => output.Enqueue(line.Data ?? "");
        build.ErrorDataReceived += (_, line) => output.Enqueue(line.Data ?? "");
        build.BeginOutputReadLine();
        build.BeginErrorReadLine();
        return build;
    }

    /// <summary>
    /// Waits for a file a running build will write, failing the test if the build ends first or
    /// never gets there. The limit only stops a stall: a loaded machine can be very slow.
    /// </summary>
    public void WaitForFile(string name, Process build, ConcurrentQueue<string> output)
    {
        string path = Path.Combine(this.Root, name);
        var clock = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.False(build.HasExited, $"The build ended before '{name}' appeared:\n{string.Join('\n', output)}");
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(5), $"'{name}' didn't appear in {clock.Elapsed}. The build's output so far:\n{string.Join('\n', output)}");
            Thread.Sleep(20);
        }
    }

    public void Dispose() => this._directory.Dispose();

    private string[] WriteProjectAndGetArguments(string[] properties, string[] targets)
    {
        string projectFile = Path.Combine(this.ProjectDir, "TestGame.proj");
        File.WriteAllText(projectFile, this.ProjectXml(), new UTF8Encoding(false));

        var arguments = new List<string> { "msbuild", projectFile, "-nologo", "-nodeReuse:false", "-maxCpuCount:1", "-verbosity:normal", "-terminalLogger:off", "-lowPriority" };
        if (targets.Length > 0)
        {
            arguments.Add("-target:" + string.Join(';', targets));
        }

        foreach (string property in properties)
        {
            arguments.Add("-property:" + property);
        }

        return [.. arguments];
    }

    private string ProjectXml()
    {
        string pipeline = Path.Combine(Paths.FrameworkRoot, "pipeline", "build", "DotGame.Framework.ContentPipeline.targets");

        var xml = new StringBuilder();
        xml.AppendLine("<Project DefaultTargets=\"Build\">");
        xml.AppendLine("  <PropertyGroup>");
        Property(xml, "Configuration", "Debug");
        Property(xml, "TargetFramework", "net10.0");
        Property(xml, "BaseIntermediateOutputPath", "obj/");
        Property(xml, "OutDir", "bin/");
        Property(xml, "RootNamespace", "TestGame");
        Property(xml, "AssemblyName", "TestGame");
        Property(xml, "ContentPipelinePlatform", "desktop");
        Property(xml, "ContentPipelineBuildTool", this.ToolFile);
        Property(xml, "ContentPipelineBuildProject", Path.Combine(Path.GetDirectoryName(this.ToolFile)!, "Tool.csproj"));
        if (!this.UseDefaultCacheFolder)
        {
            Property(xml, "ContentCacheDir", this.CacheDir);
        }

        if (this.ImportLibrary && (Paths.LibraryBuildTool is { Property: { } toolProperty, File: { } toolFile }))
        {
            Property(xml, toolProperty, Path.Combine(AppContext.BaseDirectory, toolFile));
        }

        foreach (var property in this.Properties)
        {
            Property(xml, property.Key, property.Value);
        }

        xml.AppendLine("  </PropertyGroup>");
        xml.AppendLine("  <ItemGroup>");
        foreach (ContentItem item in this._items)
        {
            xml.Append("    <").Append(item.Type).Append(" Include=\"").Append(SecurityElement.Escape(item.Include)).AppendLine("\">");
            foreach (var metadata in item.Metadata)
            {
                xml.Append("      <").Append(metadata.Key).Append('>').Append(SecurityElement.Escape(metadata.Value)).Append("</").Append(metadata.Key).AppendLine(">");
            }

            xml.Append("    </").Append(item.Type).AppendLine(">");
        }

        xml.AppendLine("  </ItemGroup>");
        xml.Append("  <Import Project=\"").Append(SecurityElement.Escape(pipeline)).AppendLine("\" />");
        if (this.ImportLibrary)
        {
            string library = Paths.LibraryContentTargetsOrNull
                ?? throw new InvalidOperationException("Only a library's tests have its targets (DotGameLibraryContentTargets).");
            xml.Append("  <Import Project=\"").Append(SecurityElement.Escape(library)).AppendLine("\" />");
        }

        xml.AppendLine(this.ExtraProjectXml);
        xml.AppendLine("  <Target Name=\"BeforeBuild\" />");
        xml.AppendLine("  <Target Name=\"Clean\" />");
        xml.AppendLine("  <Target Name=\"Build\" DependsOnTargets=\"BeforeBuild\" />");
        xml.AppendLine("</Project>");
        return xml.ToString();
    }

    private static void Property(StringBuilder xml, string name, string value)
    {
        xml.Append(CultureInfo.InvariantCulture, $"    <{name}>{SecurityElement.Escape(value)}</{name}>").AppendLine();
    }

    [GeneratedRegex("""manifest\.Add\("(?<key>[^"]*)", "(?<path>[^"]*)"(?:, subKey: "(?<sub>[^"]*)")?(?:, properties: (?<props>.*))?\);""")]
    private static partial Regex ManifestRegex();
}
