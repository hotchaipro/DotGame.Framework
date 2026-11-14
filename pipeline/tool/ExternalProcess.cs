// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Diagnostics;

namespace DotGame.Framework.ContentPipeline.BuildTool;

public static class ExternalProcess
{
    /// <summary>
    /// Runs a tool.  Its output goes straight to the build log.
    /// </summary>
    public static void Run(string fileName, IEnumerable<string> arguments, string description)
    {
        var startInfo = CreateStartInfo(fileName, arguments);
        using var process = Start(startInfo, description);
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new ContentBuildException($"{description} failed with exit code {process.ExitCode}.");
        }
    }

    /// <summary>
    /// Runs a tool and returns its standard output.  Standard error goes to the
    /// build log unless <paramref name="captureStandardError"/> is set, in which
    /// case it is included in the failure message.
    /// </summary>
    public static string RunAndCapture(string fileName, IEnumerable<string> arguments, string description, bool captureStandardError = false)
    {
        var startInfo = CreateStartInfo(fileName, arguments);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = captureStandardError;
        using var process = Start(startInfo, description);
        Task<string>? standardError = captureStandardError ? process.StandardError.ReadToEndAsync() : null;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        string errors = standardError?.Result.Trim() ?? "";
        if (process.ExitCode != 0)
        {
            throw new ContentBuildException($"{description} failed with exit code {process.ExitCode}{(errors.Length > 0 ? ": " + errors : ".")}");
        }
        return output;
    }

    /// <summary>
    /// Runs a tool and returns its standard output as bytes.  Standard error is
    /// included in the failure message.
    /// </summary>
    public static byte[] RunAndCaptureBytes(string fileName, IEnumerable<string> arguments, string description)
    {
        var startInfo = CreateStartInfo(fileName, arguments);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var process = Start(startInfo, description);
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        string errors = standardError.Result.Trim();
        if (process.ExitCode != 0)
        {
            throw new ContentBuildException($"{description} failed with exit code {process.ExitCode}{(errors.Length > 0 ? ": " + errors : ".")}");
        }
        return output.ToArray();
    }

    /// <summary>
    /// Runs a content processor command template (after token substitution) with
    /// the platform shell (cmd on Windows, sh elsewhere) so the template's own
    /// quoting is honored, as when MSBuild runs it.
    /// </summary>
    public static void RunShellCommand(string command, string description)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            // /s with the whole command quoted makes cmd strip exactly the outer quotes.
            startInfo = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"");
        }
        else
        {
            startInfo = new ProcessStartInfo("/bin/sh");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }
        startInfo.UseShellExecute = false;
        using var process = Start(startInfo, description);
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new ContentBuildException($"{description} failed with exit code {process.ExitCode}: {command}");
        }
    }

    /// <summary>Substitutes the per-file tokens of a content processor command template.</summary>
    public static string ExpandCommandTemplate(string template, string inputFile, string outputFile, string intermediateDir) =>
        template
            .Replace("{InputFile}", inputFile, StringComparison.Ordinal)
            .Replace("{OutputFile}", outputFile, StringComparison.Ordinal)
            .Replace("{IntermediateDir}", intermediateDir.TrimEnd('\\', '/'), StringComparison.Ordinal);

    private static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static Process Start(ProcessStartInfo startInfo, string description)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new ContentBuildException($"{description}: failed to start '{startInfo.FileName}'.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ContentBuildException(
                $"{description}: failed to start '{startInfo.FileName}': {ex.Message}. "
                + $"Run pipeline/{(OperatingSystem.IsWindows() ? "install.cmd" : "install.sh")} to install the content tools, or set the tool's path property (see ExternalTools.props).");
        }
    }
}
