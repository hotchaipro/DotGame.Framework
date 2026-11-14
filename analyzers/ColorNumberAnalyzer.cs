// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DotGame.Analyzers;

/// <summary>
/// Reports a number passed to <c>Color.FromRgb</c> that can't be what was meant (DG0007):
/// <c>FromRgb(255)</c> is blue rather than gray, as the number reads 0xRRGGBB, and
/// <c>FromRgb(0xFFFFFF, 128)</c> and <c>FromRgb(0x87CEEB80)</c> would throw when they ran.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ColorNumberAnalyzer : DiagnosticAnalyzer
{
    private const string ColorTypeName = "DotGame.Framework.Color";
    private const uint MaxRgb = 0xFFFFFF;

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        id: "DG0007",
        title: "A color's number can't be what was meant",
        messageFormat: "{0}",
        category: "DotGame.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Color.FromRgb takes a number written as 0xRRGGBB, and an alpha from 0 to 1. A number written in decimal reads as the wrong color, and the others throw when they run.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(
        AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if ((method.Name != "FromRgb") || (method.ContainingType.ToDisplayString() != ColorTypeName))
        {
            return;
        }

        foreach (var argument in invocation.Arguments)
        {
            if (argument.IsImplicit || !argument.Value.ConstantValue.HasValue)
            {
                continue;
            }

            string? problem = argument.Parameter?.Name switch
            {
                "rgb" => CheckRgb(argument.Value),
                "alpha" => CheckAlpha(argument.Value),
                _ => null,
            };

            if (problem is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, argument.Syntax.GetLocation(), problem));
            }
        }
    }

    private static string? CheckRgb(
        IOperation value)
    {
        var constant = value.ConstantValue.Value;
        if (constant is not uint rgb)
        {
            return null;
        }

        if (rgb > MaxRgb)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "0x{0:X} is more than 0xRRGGBB: Color.FromRgb throws above 0xFFFFFF. Pass the alpha second, from 0 to 1",
                rgb);
        }

        // NOTE: Zero is black however it's written, and a named constant says what it means
        string text = value.Syntax.ToString();
        bool isDecimal = (text.Length > 0) && char.IsDigit(text[0])
            && (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            && (!text.StartsWith("0b", StringComparison.OrdinalIgnoreCase));
        if (isDecimal && (rgb != 0))
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Color.FromRgb reads its number as 0xRRGGBB, so {0} is 0x{1:X6}: write it in hex. For components from 0 to 1, use new Color(r, g, b)",
                text,
                rgb);
        }

        return null;
    }

    private static string? CheckAlpha(
        IOperation value)
    {
        var constant = value.ConstantValue.Value;
        if ((constant is float alpha) && !((alpha >= 0f) && (alpha <= 1f)))
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "An alpha of {0} isn't from 0 to 1: Color.FromRgb throws. For a byte, divide it by 255f",
                alpha);
        }

        return null;
    }
}
