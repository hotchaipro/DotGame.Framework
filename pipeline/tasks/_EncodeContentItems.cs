// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _EncodeContentItems - brings a type's one-output items up to date: whichever are out of date are
// served from the content cache or encoded, several at once. MSBuild runs a target's batches one
// after another, so a cold build otherwise scales with assets, whatever the machine.
//
// Each item says everything about itself in metadata; the command templates remain the contract.
public sealed class _EncodeContentItems : Task, ICancelableTask
{
    private readonly object _processesLock = new object();
    private readonly List<Process> _processes = new List<Process>();
    private volatile bool _isCanceled;

    [Required]
    public ITaskItem[] Items { get; set; }

    public string CacheDir { get; set; }

    public string CommonInputs { get; set; }

    public string WorkingDirectory { get; set; }

    public string IgnoreFolder { get; set; }

    public int LockTimeoutMilliseconds { get; set; }

    // 0 for half the processors: encoders such as cwebp and astcenc have threads of their own
    public int MaxParallel { get; set; }

    public void Cancel()
    {
        this._isCanceled = true;
        lock (this._processesLock)
        {
            foreach (var process in this._processes)
            {
                try
                {
                    // NOTE: With the encoder under the shell; this framework has no tree kill
                    using var kill = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                        ? Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + process.Id) { UseShellExecute = false, CreateNoWindow = true })
                        : Process.Start(new ProcessStartInfo("/bin/sh", "-c \"pkill -TERM -P " + process.Id + "; kill " + process.Id + "\"") { UseShellExecute = false });
                    kill?.WaitForExit(5000);
                }
                catch (Exception e) when ((e is InvalidOperationException) || (e is System.ComponentModel.Win32Exception))
                {
                    // It has ended by itself
                }
            }
        }
    }

    public override bool Execute()
    {
        if (!this.HasDistinctOutputs())
        {
            return false;
        }

        var commonInputs = SplitList(this.CommonInputs);
        long commonNewest = commonInputs.Length == 0 ? 0 : commonInputs.Max(NewestTicks);

        var outOfDate = this.Items.Where(item => IsOutOfDate(item, commonNewest)).ToArray();
        if (outOfDate.Length == 0)
        {
            return true;
        }

        int parallel = this.MaxParallel > 0 ? this.MaxParallel : Math.Max(1, Environment.ProcessorCount / 2);
        bool isFailed = false;
        System.Threading.Tasks.Parallel.ForEach(
            outOfDate,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = parallel },
            (item, loop) =>
            {
                if (this._isCanceled || !this.BuildItem(item))
                {
                    isFailed = true;
                    loop.Stop();
                }
            });

        return !isFailed && !this.Log.HasLoggedErrors;
    }

    // NOTE: Encoded at once, items with one output delete and move each other's staged file, and
    // the build ends in an exception that names neither. The manifest's check on keys comes too late.
    private bool HasDistinctOutputs()
    {
        var firstItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool isDistinct = true;
        foreach (var item in this.Items)
        {
            string output = item.GetMetadata("_OutputFile");
            if (firstItems.TryGetValue(output, out string first))
            {
                this.Log.LogError(
                    "Two content items have the output '{0}': '{1}' and '{2}'. An output is named after its path without the extension: rename one, or give it Link metadata.",
                    output,
                    first,
                    item.ItemSpec);
                isDistinct = false;
            }
            else
            {
                firstItems.Add(output, item.ItemSpec);
            }
        }

        return isDistinct;
    }

    private static string[] SplitList(string list)
    {
        return (list ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Trim()).Where(entry => entry.Length > 0).ToArray();
    }

    private static long NewestTicks(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        }
        catch (Exception e) when ((e is IOException) || (e is UnauthorizedAccessException) || (e is ArgumentException))
        {
            return 0;
        }
    }

    private static bool IsOutOfDate(ITaskItem item, long commonNewest)
    {
        string output = item.GetMetadata("_OutputFile");
        if (!File.Exists(output))
        {
            return true;
        }

        long outputTicks = File.GetLastWriteTimeUtc(output).Ticks;
        if ((commonNewest > outputTicks) || (NewestTicks(item.ItemSpec) > outputTicks) || (NewestTicks(item.GetMetadata("_StampFile")) > outputTicks))
        {
            return true;
        }

        foreach (string dep in SplitList(item.GetMetadata("_Deps")))
        {
            if (NewestTicks(dep) > outputTicks)
            {
                return true;
            }
        }

        return false;
    }

    private bool BuildItem(ITaskItem item)
    {
        var stopwatch = Stopwatch.StartNew();
        string output = item.GetMetadata("_OutputFile");
        string staged = item.GetMetadata("_StagedFile");
        string template = item.GetMetadata("_CommandTemplate");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        Directory.CreateDirectory(item.GetMetadata("_IntermediateDir"));

        var restore = new _TryRestoreFromContentCache
        {
            BuildEngine = this.BuildEngine,
            SourceFile = item.ItemSpec,
            OutputFile = output,
            CacheDir = this.CacheDir,
            CommandTemplate = template,
            ToolPaths = item.GetMetadata("_ToolPaths"),
            ProcessorDeps = item.GetMetadata("_Deps"),
            ExtraKey = item.GetMetadata("_ExtraKey"),
            LockTimeoutMilliseconds = this.LockTimeoutMilliseconds,
        };
        if (!restore.Execute())
        {
            return false;
        }

        bool isRestored = restore.Restored == "true";
        if (!isRestored)
        {
            var resolve = new _ResolveProcessorCommand
            {
                BuildEngine = this.BuildEngine,
                Template = template,
                InputFile = item.ItemSpec,
                OutputFile = staged,
                IntermediateDir = item.GetMetadata("_IntermediateDir"),
                Quality = (item.GetMetadata("_Quality") is { Length: > 0 } quality) ? quality : item.GetMetadata("LossyQuality"),
                PremultiplyAlpha = item.GetMetadata("_PremultiplyAlpha"),
            };
            resolve.Execute();
            this.Log.LogMessage(MessageImportance.Low, "    {0}", resolve.ResolvedCommand);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(staged)));
            if (File.Exists(staged))
            {
                File.Delete(staged);
            }

            // NOTE: Encoded beside the output and moved when it's whole, so a failed or killed
            // encode can't leave a partial output that a later build takes for finished
            if (!this.RunCommand(resolve.ResolvedCommand, item))
            {
                return false;
            }

            if (File.Exists(output))
            {
                File.Delete(output);
            }

            File.Move(staged, output);

            var save = new _SaveToContentCache
            {
                BuildEngine = this.BuildEngine,
                IgnoreFolder = this.IgnoreFolder,
                OutputFile = output,
                SourceFile = item.ItemSpec,
                CachePath = restore.CachePath,
                CacheDir = this.CacheDir,
            };
            if (!save.Execute())
            {
                return false;
            }
        }

        this.Log.LogMessage(
            MessageImportance.High,
            "{0}{1} [{2:0.##}s]",
            item.GetMetadata("_Description"),
            isRestored ? " [cached]" : "",
            stopwatch.Elapsed.TotalSeconds);
        return true;
    }

    private bool RunCommand(string command, ITaskItem item)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(this.WorkingDirectory) ? Directory.GetCurrentDirectory() : this.WorkingDirectory,
        };
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = "/S /C \"" + command + "\"";
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.Arguments = "-c \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        using var process = new Process { StartInfo = startInfo };
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        DataReceivedEventHandler onLine = (_, e) =>
        {
            if (e.Data is not null)
            {
                lines.Enqueue(e.Data);
            }
        };
        process.OutputDataReceived += onLine;
        process.ErrorDataReceived += onLine;

        lock (this._processesLock)
        {
            if (this._isCanceled)
            {
                return false;
            }

            process.Start();
            this._processes.Add(process);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        lock (this._processesLock)
        {
            this._processes.Remove(process);
        }

        // An encoder's output together, so items at the same time don't interleave. "error" lines
        // from the tool are its own canonical form, which MSBuild shows as errors
        foreach (string line in lines)
        {
            this.Log.LogMessageFromText(line, MessageImportance.Normal);
        }

        if (process.ExitCode != 0)
        {
            this.Log.LogError(
                "The content command for '{0}' exited with code {1}: {2}",
                item.ItemSpec,
                process.ExitCode,
                command);
            return false;
        }

        return true;
    }
}
