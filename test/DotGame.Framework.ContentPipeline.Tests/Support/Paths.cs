// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace DotGame.ContentPipeline.Testing;

/// <summary>
/// The trait that marks the slow set: tests that start MSBuild or a real external tool. Filter
/// them out with <c>-trait- "Category=Slow"</c> or <c>--filter Category!=Slow</c>.
/// </summary>
internal static class Traits
{
    public const string Category = "Category";
    public const string Slow = "Slow";
}

/// <summary>
/// Where things are: the framework's repository and, for the tests of a library built on the framework
/// that link these files, the library's (as the test project's build found them), and the fake tool
/// beside these tests.
/// </summary>
internal static class Paths
{
    /// <summary>The framework's repository: the test project's DotGameFrameworkDir.</summary>
    public static string FrameworkRoot { get; } = FromBuild("DotGameFrameworkDir")
        ?? throw new InvalidOperationException("The test project doesn't say where the framework is (DotGameFrameworkDir).");

    /// <summary>
    /// The library's repository (DotGameLibraryDir), for a library's tests, or null for the framework's own.
    /// </summary>
    public static string? LibraryRootOrNull { get; } = FromBuild("DotGameLibraryDir");

    /// <summary>
    /// The library's content pipeline targets (DotGameLibraryContentTargets), which
    /// <see cref="ContentProject.ImportLibrary"/> imports after the framework's, or null.
    /// </summary>
    public static string? LibraryContentTargetsOrNull { get; } = FromBuild("DotGameLibraryContentTargets");

    /// <summary>
    /// The property that names the library's build tool (DotGameLibraryBuildToolProperty), and the tool's
    /// file name beside these tests (DotGameLibraryBuildTool), or nulls.
    /// </summary>
    public static (string? Property, string? File) LibraryBuildTool { get; } =
        (TextFromBuild("DotGameLibraryBuildToolProperty"), TextFromBuild("DotGameLibraryBuildTool"));

    public static string FakeTool { get; } = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "FakeContentTool.exe" : "FakeContentTool");

    public static string DotNet { get; } = FindDotNet();

    // NOTE: From the test project's AssemblyMetadata, so the framework's and a library's repositories
    // can be one or two, wherever their clones are
    private static string? FromBuild(string key)
    {
        return TextFromBuild(key) is { } value ? Path.GetFullPath(value) : null;
    }

    private static string? TextFromBuild(string key)
    {
        foreach (var metadata in typeof(Paths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if ((metadata.Key == key) && !string.IsNullOrEmpty(metadata.Value))
            {
                return metadata.Value;
            }
        }

        return null;
    }

    private static string FindDotNet()
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host))
        {
            return host;
        }

        return Environment.ProcessPath is { } path && Path.GetFileNameWithoutExtension(path) == "dotnet" ? path : "dotnet";
    }
}

/// <summary>
/// A temporary directory, removed afterwards, with a <c>fake-tool.config</c> so the fake tool logs
/// its calls here and takes its behavior from here.
/// </summary>
internal sealed class TestDirectory : IDisposable
{
    private readonly Dictionary<string, string> _config = new(StringComparer.Ordinal);

    public TestDirectory(string suffix = "")
    {
        this.Root = Path.Combine(Path.GetTempPath(), "dgpt", Guid.NewGuid().ToString("N")[..10] + suffix);
        Directory.CreateDirectory(this.Root);
        this.WriteConfig();
    }

    public string Root { get; }

    public string Path_(params string[] parts) => Path.Combine([this.Root, .. parts]);

    /// <summary>Sets one of the fake tool's settings, or removes it with null.</summary>
    public void SetToolConfig(string key, string? value)
    {
        if (value is null)
        {
            this._config.Remove(key);
        }
        else
        {
            this._config[key] = value;
        }

        this.WriteConfig();
    }

    /// <summary>The fake tool's calls so far: kind, input file name and arguments.</summary>
    public List<ToolCall> ToolCalls()
    {
        var calls = new List<ToolCall>();
        string log = this.Path_("calls.log");
        if (File.Exists(log))
        {
            foreach (string line in File.ReadAllLines(log))
            {
                string[] fields = line.Split('\t', 3);
                calls.Add(new ToolCall(fields[0], fields[1], fields[2]));
            }
        }

        return calls;
    }

    public string WriteFile(string relativePath, string text)
    {
        string path = Path.Combine(this.Root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(this.Root))
                {
                    Directory.Delete(this.Root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A killed build's children can hold a file for a moment; a leftover is harmless
                Thread.Sleep(50);
            }
        }
    }

    private void WriteConfig()
    {
        var lines = new List<string>();
        foreach (var setting in this._config)
        {
            lines.Add($"{setting.Key}={setting.Value}");
        }

        File.WriteAllLines(this.Path_("fake-tool.config"), lines);
    }
}

internal readonly record struct ToolCall(string Kind, string Input, string Arguments);

/// <summary>Runs a program and collects what it prints, in full, without a deadlock.</summary>
internal static class ProcessRunner
{
    public static (int ExitCode, string Output) Run(string fileName, IEnumerable<string> arguments, string? workingDirectory = null, int timeoutMilliseconds = 300000)
    {
        using var process = Start(fileName, arguments, workingDirectory);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{fileName}' didn't finish in {timeoutMilliseconds} ms.");
        }

        process.WaitForExit();
        return (process.ExitCode, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
    }

    public static Process Start(string fileName, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            info.WorkingDirectory = workingDirectory;
        }

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // What a dotnet test host passes down mustn't steer the MSBuild these tests start
        foreach (string name in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildLoadMicrosoftTargetsReadOnly", "VSTEST_HOST_DEBUG" })
        {
            info.Environment.Remove(name);
        }

        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["MSBUILDTERMINALLOGGER"] = "off";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        return Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start '{fileName}'.");
    }
}

/// <summary>
/// Builds the pipeline's task assemblies once before any test starts MSBuild: they're built on
/// demand under the source tree, and parallel builds of them would collide.
/// </summary>
public sealed class TaskAssembliesFixture
{
    public TaskAssembliesFixture()
    {
        // Across test assemblies too, since a solution's test projects run at once
        string lockPath = Path.Combine(Path.GetTempPath(), "hcpt-task-assemblies.lock");
        using FileStream held = AcquireLock(lockPath);

        // A library's tasks too, for a library's tests
        using var project = new ContentProject();
        project.ImportLibrary = Paths.LibraryContentTargetsOrNull is not null;
        BuildResult result = project.Build("_BuildContentPipelineTasks");
        Assert.True(result.Succeeded, result.Output);
    }

    private static FileStream AcquireLock(string lockPath)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (clock.Elapsed < TimeSpan.FromMinutes(5))
            {
                Thread.Sleep(100);
            }
        }
    }
}
