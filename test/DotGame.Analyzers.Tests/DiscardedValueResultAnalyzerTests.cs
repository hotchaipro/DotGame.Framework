// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DotGame.Analyzers.Tests;

public sealed class DiscardedValueResultAnalyzerTests
{
    private const string Framework = """
        namespace DotGame.Framework
        {
            public readonly struct RectangleInt
            {
                public RectangleInt Inflate(int x, int y) => this;
                public RectangleInt Offset(int x, int y) => this;
                public int Area() => 0;
                public void Deconstruct(out int x) { x = 0; }
                public static RectangleInt Make() => default;
            }

            public struct Mutable
            {
                public Mutable Grow() => this;
            }
        }

        namespace Other
        {
            public readonly struct Value
            {
                public Value Grow() => this;
            }
        }
        """;

    private static ImmutableArray<Diagnostic> Analyze(
        string body)
    {
        var source = "using DotGame.Framework; class Game { void Run(RectangleInt r, Mutable m, Other.Value v) { " + body + " } }";
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
            .WithAnalyzers([new DiscardedValueResultAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Theory]
    [InlineData("r.Inflate(1, 1);")]
    [InlineData("r.Offset(1, 1);")]
    [InlineData("r.Inflate(1, 1).Offset(2, 2);")]
    [InlineData("RectangleInt.Make();")]
    public void DiscardedNewValueIsAnError(
        string body)
    {
        var diagnostic = Assert.Single(Analyze(body));
        Assert.Equal("DG0004", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("r = r.Inflate(1, 1);")]
    [InlineData("_ = r.Inflate(1, 1);")]
    [InlineData("var x = r.Offset(1, 1);")]
    [InlineData("r.Area();")]
    [InlineData("r.Deconstruct(out _);")]
    [InlineData("m.Grow();")]
    [InlineData("v.Grow();")]
    public void AnythingElseIsFine(
        string body)
    {
        Assert.Empty(Analyze(body));
    }
}
