// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DotGame.Analyzers.Tests;

public sealed class FixedUpdateRateAnalyzerTests
{
    private const string Framework = """
        namespace DotGame.Framework
        {
            public class Game
            {
                public int? FixedUpdateRate { get; set; }
                protected virtual void OnFixedUpdate() { }
                protected virtual void OnUpdate() { }
            }
        }

        namespace Other
        {
            public class Game
            {
                public int? FixedUpdateRate { get; set; }
                protected virtual void OnFixedUpdate() { }
            }
        }
        """;

    private static ImmutableArray<Diagnostic> Analyze(
        string source)
    {
        var references = new List<MetadataReference>();
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
        {
            references.Add(MetadataReference.CreateFromFile(path));
        }

        var compilation = CSharpCompilation.Create(
            "Sample",
            [CSharpSyntaxTree.ParseText(Framework), CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        return compilation
            .WithAnalyzers([new FixedUpdateRateAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void AnOverrideWithNoRateIsAnError()
    {
        var diagnostic = Assert.Single(Analyze("class MyGame : DotGame.Framework.Game { protected override void OnFixedUpdate() { } }"));

        Assert.Equal("DG0005", diagnostic.Id);
        Assert.Contains("MyGame", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnOverrideInAGameFurtherDownIsAnError()
    {
        var diagnostic = Assert.Single(Analyze("""
            class Base : DotGame.Framework.Game { }
            class MyGame : Base { protected override void OnFixedUpdate() { } }
            """));

        Assert.Equal("DG0005", diagnostic.Id);
    }

    [Theory]
    [InlineData("class MyGame : DotGame.Framework.Game { public MyGame() { this.FixedUpdateRate = 30; } protected override void OnFixedUpdate() { } }")]
    [InlineData("class MyGame : DotGame.Framework.Game { protected override void OnFixedUpdate() { } } class Host { object Make() => new MyGame { FixedUpdateRate = 30 }; }")]
    [InlineData("class MyGame : DotGame.Framework.Game { protected override void OnFixedUpdate() { } } class Host { void Set(MyGame game) { game.FixedUpdateRate = 30; } }")]
    public void SettingTheRateAnywhereIsFine(
        string source)
    {
        Assert.Empty(Analyze(source));
    }

    [Theory]
    [InlineData("class MyGame : DotGame.Framework.Game { protected override void OnUpdate() { } }")]
    [InlineData("class MyGame : DotGame.Framework.Game { }")]
    [InlineData("abstract class Base : DotGame.Framework.Game { protected override void OnFixedUpdate() { } }")]
    [InlineData("class Mine : Other.Game { protected override void OnFixedUpdate() { } }")]
    public void WhatIsNotAnOverrideOfTheGamesFixedUpdateIsFine(
        string source)
    {
        Assert.Empty(Analyze(source));
    }
}
