// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Text;
using DotGame.ContentPipeline.Testing;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Integration;

/// <summary>
/// The on-demand build of a pipeline's task assembly (ContentPipeline.Tasks.targets), on a task
/// project of the test's own registered as a pipeline registers its own.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class TaskAssemblyBuildTests
{
    [Fact]
    public void DesignTimeBuild_CompilesTheChangedSources()
    {
        using var directory = new TestDirectory();
        string tasks = Path.Combine(directory.Root, "tasks");
        Directory.CreateDirectory(tasks);
        File.WriteAllText(Path.Combine(tasks, "Fake.Tasks.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        string source = Path.Combine(tasks, "FakeTasks.cs");
        File.WriteAllText(source, "public static class FakeTasks { public static string Version() => \"first\"; }\n");

        // The folder stands in for the hash of the sources, which a build names on its own
        string game = Path.Combine(directory.Root, "Game.proj");
        File.WriteAllText(game, $"""
            <Project>
              <Import Project="{Path.Combine(Paths.FrameworkRoot, "pipeline", "ContentPipeline.Tasks.targets")}" />
              <ItemGroup>
                <_ContentPipelineTasksProject Include="{Path.Combine(tasks, "Fake.Tasks.csproj")}" AssemblyFile="{tasks}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}$(Sources){Path.DirectorySeparatorChar}Fake.Tasks.dll" />
                <_ContentPipelineTasksSource Include="{source}" />
              </ItemGroup>
            </Project>
            """);

        Build(game, directory.Root, "Sources=first");
        File.WriteAllText(source, "public static class FakeTasks { public static string Version() => \"second\"; }\n");

        // As Visual Studio builds a web game for IntelliSense, which runs the fingerprint tasks:
        // the task project gets a real build, not the design-time one that skips the compiler
        Build(game, directory.Root, "Sources=second", "DesignTimeBuild=true", "SkipCompilerExecution=true", "ProvideCommandLineArgs=true", "BuildingProject=false");

        byte[] assembly = File.ReadAllBytes(Path.Combine(tasks, "bin", "second", "Fake.Tasks.dll"));
        Assert.True(Contains(assembly, Encoding.Unicode.GetBytes("second")), "The design-time build copied the last build's assembly instead of compiling the changed source.");
    }

    private static void Build(string project, string workingDirectory, params string[] properties)
    {
        (int exitCode, string output) = ProcessRunner.Run(
            Paths.DotNet,
            ["msbuild", project, "-t:_BuildContentPipelineTasks", "-nologo", "-nodeReuse:false", "-terminalLogger:off", .. properties.Select(property => "-p:" + property)],
            workingDirectory);
        Assert.True(exitCode == 0, output);
    }

    private static bool Contains(byte[] bytes, byte[] value)
    {
        return bytes.AsSpan().IndexOf(value) >= 0;
    }
}
