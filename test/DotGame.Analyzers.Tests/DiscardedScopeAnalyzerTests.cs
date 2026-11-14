// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DotGame.Analyzers.Tests;

public sealed class DiscardedScopeAnalyzerTests
{
    private const string Framework = """
        namespace DotGame.Framework.Graphics
        {
            public readonly struct SpriteBatchScope : System.IDisposable
            {
                public void Dispose() { }
            }

            public sealed class SpriteBatch
            {
                public SpriteBatchScope Begin() => default;
                public int Count() => 0;
            }
        }

        namespace SomeEngine
        {
            public readonly struct DrawScope : System.IDisposable
            {
                public void Dispose() { }
            }

            public sealed class Context
            {
                public DrawScope Use() => default;
            }
        }

        namespace Other
        {
            public readonly struct OtherScope
            {
            }

            public sealed class OtherDisposableScope : System.IDisposable
            {
                public void Dispose() { }
            }

            public sealed class Thing
            {
                public OtherScope Begin() => default;
                public OtherDisposableScope Open() => new OtherDisposableScope();
            }
        }
        """;

    private static ImmutableArray<Diagnostic> Analyze(
        string body)
    {
        var source = "using DotGame.Framework.Graphics; using Other; class Game { SpriteBatchScope kept; SpriteBatchScope Run(SpriteBatch b, Thing t, SomeEngine.Context c) { " + body + " return default; } }";
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
            .WithAnalyzers([new DiscardedScopeAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Theory]
    [InlineData("b.Begin();")]
    [InlineData("_ = b.Begin();")]
    public void AScopeThrownAwayIsReported(
        string body)
    {
        var diagnostic = Assert.Single(Analyze(body));

        Assert.Equal("DG0006", diagnostic.Id);
        Assert.Contains("SpriteBatchScope", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void ALibrarysScopeThrownAwayIsReported()
    {
        Assert.Equal("DG0006", Assert.Single(Analyze("c.Use();")).Id);
    }

    [Theory]
    [InlineData("using var batch = b.Begin();")]
    [InlineData("using (b.Begin()) { }")]
    [InlineData("this.kept = b.Begin();")]
    [InlineData("return b.Begin();")]
    [InlineData("var batch = b.Begin(); batch.Dispose();")]
    [InlineData("b.Count();")]
    [InlineData("t.Begin();")]
    [InlineData("t.Open();")]
    public void AScopeThatsKeptOrAnythingElseIsFine(
        string body)
    {
        Assert.Empty(Analyze(body));
    }
}
