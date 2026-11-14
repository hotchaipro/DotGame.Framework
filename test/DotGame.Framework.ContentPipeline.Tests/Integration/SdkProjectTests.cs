// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Text;
using DotGame.ContentPipeline.Testing;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Integration;

/// <summary>
/// One real SDK project, as a game is: the pipeline hooked into an SDK build, the manifest compiled
/// in, content staged beside the app and removed by clean. The processors are fake; the build tool
/// is built on demand, as for a game.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class SdkProjectTests
{
    private static string WriteSdkProject(
        ContentProject project,
        string source = "src/hero.png",
        string item = "<TextureContent Include=\"src/hero.png\" Link=\"Content\\hero.png\" />")
    {
        string pipeline = Path.Combine(Paths.FrameworkRoot, "pipeline", "build", "DotGame.Framework.ContentPipeline.targets");
        project.WriteSource(source, "source");

        // The generated manifest's one dependency on the framework
        project.WriteSource("Stubs.cs", """
            namespace DotGame.Framework.Content;

            public sealed class ContentManifest(string identity, string basePath)
            {
                public static void Register(ContentManifest manifest) { }

                public void Add(string key, string path, string? subKey = null, System.Collections.Generic.KeyValuePair<string, string>[]? properties = null) { }
            }
            """);
        string projectFile = Path.Combine(project.ProjectDir, "SdkGame.csproj");
        File.WriteAllText(projectFile, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ContentPipelinePlatform>desktop</ContentPipelinePlatform>
                <ContentCacheDir>{project.CacheDir}</ContentCacheDir>
                <TextureContentLosslessProcessorCommand>{ContentProject.Command("texture").Replace("\"", "&quot;", StringComparison.Ordinal)}</TextureContentLosslessProcessorCommand>
                <UseSharedCompilation>false</UseSharedCompilation>
              </PropertyGroup>
              <ItemGroup>
                {item}
              </ItemGroup>
              <Import Project="{pipeline}" />
            </Project>
            """, new UTF8Encoding(false));
        project.WriteSource("Directory.Build.props", "<Project />");
        return projectFile;
    }

    private static (int ExitCode, string Output) Dotnet(ContentProject project, params string[] arguments) =>
        ProcessRunner.Run(Paths.DotNet, [.. arguments, "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-tl:off"], project.ProjectDir);

    [Fact]
    public void BuildThenCleanThenBuild_AsAGameDoes()
    {
        using var project = new ContentProject();
        string projectFile = WriteSdkProject(project);

        (int exitCode, string output) = Dotnet(project, "build", projectFile);
        Assert.True(exitCode == 0, output);
        string staged = Path.Combine(project.ProjectDir, "bin", "Debug", "net10.0", "Content", "hero.ktx2");
        Assert.True(File.Exists(staged), output);
        string manifest = Path.Combine(project.ProjectDir, "obj", "ContentPipeline", "Intermediate", "ContentManifest.g.cs");
        Assert.Contains("manifest.Add(\"hero\", \"hero.ktx2\"", File.ReadAllText(manifest), StringComparison.Ordinal);
        Assert.Single(project.AllToolCalls());

        // Nothing changed: no encoder and no recompile
        string assembly = Path.Combine(project.ProjectDir, "obj", "Debug", "net10.0", "SdkGame.dll");
        DateTime compiled = File.GetLastWriteTimeUtc(assembly);
        (exitCode, output) = Dotnet(project, "build", projectFile);
        Assert.True(exitCode == 0, output);
        Assert.Single(project.AllToolCalls());
        Assert.Equal(compiled, File.GetLastWriteTimeUtc(assembly));

        (exitCode, output) = Dotnet(project, "clean", projectFile);
        Assert.True(exitCode == 0, output);
        Assert.False(File.Exists(staged), output);

        // The clean kept the cache: back again with no encoder
        (exitCode, output) = Dotnet(project, "build", projectFile);
        Assert.True(exitCode == 0, output);
        Assert.True(File.Exists(staged), output);
        Assert.Single(project.AllToolCalls());
    }

    // The generated code once went in a namespace named after the project, which can't share its
    // name with a class in the global namespace (CS0101). Nor could it share "ContentManifest" with
    // the framework's class, which it hid from the game's namespace.
    [Fact]
    public void GeneratedCode_LeavesTheGamesNamesAlone()
    {
        using var project = new ContentProject();
        string projectFile = WriteSdkProject(project);
        project.WriteSource("SdkGame.cs", """
            using DotGame.Framework.Content;

            internal sealed class SdkGame
            {
            }

            namespace SdkGame2
            {
                internal static class UsesTheFramework
                {
                    internal static ContentManifest Create() => new("identity", "base");
                }
            }
            """);

        (int exitCode, string output) = Dotnet(project, "build", projectFile, "-p:RootNamespace=SdkGame2");
        Assert.True(exitCode == 0, output);

        (exitCode, output) = Dotnet(project, "build", projectFile, "--no-incremental");
        Assert.True(exitCode == 0, output);
    }

    [Fact]
    public void FailedEncoder_FailsTheSdkBuild()
    {
        using var project = new ContentProject();
        string projectFile = WriteSdkProject(project);
        project.SetToolConfig("fail.texture", "exit");

        (int exitCode, string output) = Dotnet(project, "build", projectFile);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("FAKE0001", output, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkBase_NamesTheItem_AsTheSdkDoesForItsOwnItems()
    {
        // As the SDK's LinkBase: the base, then the path under the wildcard, then the file's name
        using var project = new ContentProject();
        string projectFile = WriteSdkProject(project, "src/Textures/hero.png", "<TextureContent Include=\"src/**/*.png\" LinkBase=\"Content\\Art\" />");

        (int exitCode, string output) = Dotnet(project, "build", projectFile);
        Assert.True(exitCode == 0, output);
        string staged = Path.Combine(project.ProjectDir, "bin", "Debug", "net10.0", "Content", "Art", "Textures", "hero.ktx2");
        Assert.True(File.Exists(staged), output);
        string manifest = Path.Combine(project.ProjectDir, "obj", "ContentPipeline", "Intermediate", "ContentManifest.g.cs");
        Assert.Contains("manifest.Add(\"Art/Textures/hero\"", File.ReadAllText(manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultItems_BuildTheContentFolder_WithoutDeclaringIt()
    {
        // An image under Content is a texture, named by its path under Content
        using var project = new ContentProject();
        string projectFile = WriteSdkProject(project, "Content/Textures/hero.png", "");

        (int exitCode, string output) = Dotnet(project, "build", projectFile);
        Assert.True(exitCode == 0, output);
        Assert.True(File.Exists(Path.Combine(project.ProjectDir, "bin", "Debug", "net10.0", "Content", "Textures", "hero.ktx2")), output);
        string manifest = Path.Combine(project.ProjectDir, "obj", "ContentPipeline", "Intermediate", "ContentManifest.g.cs");
        Assert.Contains("manifest.Add(\"Textures/hero\"", File.ReadAllText(manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultItems_LeaveOutWhatADeclaredItemIncludes()
    {
        // Declared with its own settings, the image is built once, as declared
        using var project = new ContentProject();
        string projectFile = WriteSdkProject(project, "Content/Textures/hero.png", "<TextureContent Include=\"Content\\Textures\\hero.png\" Link=\"Content\\Art\\hero.png\" />");

        (int exitCode, string output) = Dotnet(project, "build", projectFile);
        Assert.True(exitCode == 0, output);
        Assert.Single(project.AllToolCalls());
        Assert.True(File.Exists(Path.Combine(project.ProjectDir, "bin", "Debug", "net10.0", "Content", "Art", "hero.ktx2")), output);
        Assert.False(File.Exists(Path.Combine(project.ProjectDir, "bin", "Debug", "net10.0", "Content", "Textures", "hero.ktx2")), output);
    }

    [Fact]
    public void DefaultItems_TurnedOff_BuildNothing()
    {
        using var project = new ContentProject();
        string projectFile = WriteSdkProject(project, "Content/Textures/hero.png", "");

        (int exitCode, string output) = Dotnet(project, "build", projectFile, "-p:EnableDefaultContentItems=false");
        Assert.True(exitCode == 0, output);
        Assert.Empty(project.AllToolCalls());
    }
}