// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace DotGame.Analyzers.Tests;

public sealed class ColorNumberAnalyzerTests
{
    private const string Framework = """
        namespace DotGame.Framework
        {
            public readonly struct Color
            {
                public static Color FromRgb(uint rgb, float alpha = 1f) => default;
            }
        }

        namespace Other
        {
            public readonly struct Color
            {
                public static Color FromRgb(uint rgb, float alpha = 1f) => default;
            }
        }
        """;

    private static ImmutableArray<Diagnostic> Analyze(
        string body)
    {
        var source = "using Color = DotGame.Framework.Color; class Game { const uint Named = 255; void Run(uint u, float f) { " + body + " } }";
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
            .WithAnalyzers([new ColorNumberAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Theory]
    [InlineData("Color.FromRgb(255);")]
    [InlineData("Color.FromRgb(16777215);")]
    [InlineData("Color.FromRgb(0x87CEEB80);")]
    [InlineData("Color.FromRgb(0xFFFFFF, 128);")]
    [InlineData("Color.FromRgb(0xFFFFFF, -0.5f);")]
    [InlineData("Color.FromRgb(alpha: 2f, rgb: 0xFFFFFF);")]
    public void ANumberThatCantBeMeantIsReported(
        string body)
    {
        Assert.Equal("DG0007", Assert.Single(Analyze(body)).Id);
    }

    [Fact]
    public void BothNumbersWrongAreBothReported()
    {
        Assert.Equal(2, Analyze("Color.FromRgb(255, 128);").Length);
    }

    [Theory]
    [InlineData("Color.FromRgb(0xFF);")]
    [InlineData("Color.FromRgb(0xFFFFFF);")]
    [InlineData("Color.FromRgb(0);")]
    [InlineData("Color.FromRgb(0x87CEEB, 0.5f);")]
    [InlineData("Color.FromRgb(0xFFFFFF, 1);")]
    [InlineData("Color.FromRgb(Named);")]
    [InlineData("Color.FromRgb(u, f);")]
    [InlineData("Other.Color.FromRgb(255, 128);")]
    public void WhatsMeantOrUnknownIsFine(
        string body)
    {
        Assert.Empty(Analyze(body));
    }
}
