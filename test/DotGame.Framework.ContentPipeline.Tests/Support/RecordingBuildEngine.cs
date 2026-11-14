// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.ContentPipeline.Testing;

/// <summary>
/// Lets a test run an MSBuild task directly and see what it logged.
/// </summary>
internal sealed class RecordingBuildEngine : IBuildEngine
{
    public List<string> Messages { get; } = [];

    public List<string> Warnings { get; } = [];

    public List<string> Errors { get; } = [];

    public bool ContinueOnError => false;

    public int LineNumberOfTaskNode => 0;

    public int ColumnNumberOfTaskNode => 0;

    public string ProjectFileOfTaskNode => "test.proj";

    public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => false;

    public void LogCustomEvent(CustomBuildEventArgs e)
    {
    }

    public void LogErrorEvent(BuildErrorEventArgs e) => this.Errors.Add(e.Message ?? "");

    public void LogMessageEvent(BuildMessageEventArgs e) => this.Messages.Add(e.Message ?? "");

    public void LogWarningEvent(BuildWarningEventArgs e) => this.Warnings.Add(e.Message ?? "");
}

internal static class TaskItems
{
    public static TaskItem[] Items(params string[] specs) => [.. specs.Select(spec => new TaskItem(spec))];

    public static TaskItem Item(string spec, params (string Name, string Value)[] metadata)
    {
        var item = new TaskItem(spec);
        foreach ((string name, string value) in metadata)
        {
            item.SetMetadata(name, value);
        }

        return item;
    }
}
