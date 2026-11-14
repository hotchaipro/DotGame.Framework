// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DotGame.Analyzers;

/// <summary>
/// Reports a game that overrides <c>OnFixedUpdate</c> when nothing in its build sets
/// <c>FixedUpdateRate</c> (DG0005). Fixed updates are off until it's set, so the override would
/// never run. Setting it anywhere in the build satisfies this: which game it's set on, and whether
/// that line runs, isn't something the build can know.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FixedUpdateRateAnalyzer : DiagnosticAnalyzer
{
    private const string GameTypeName = "DotGame.Framework.Game";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        id: "DG0005",
        title: "OnFixedUpdate never runs without a FixedUpdateRate",
        messageFormat: "'{0}' overrides OnFixedUpdate, which is off until FixedUpdateRate is set: set it in the game's constructor",
        category: "DotGame.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Fixed updates are off by default, so that a game that doesn't use them pays nothing for them. An override of OnFixedUpdate needs FixedUpdateRate set, or it's never called.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(
        AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var overrides = new ConcurrentBag<IMethodSymbol>();
            int isRateSet = 0;

            start.RegisterSymbolAction(symbol =>
            {
                var method = (IMethodSymbol)symbol.Symbol;
                if (method.IsOverride
                    && (method.Name == "OnFixedUpdate")
                    && (!method.ContainingType.IsAbstract)
                    && DerivesFromGame(method.ContainingType))
                {
                    overrides.Add(method);
                }
            }, SymbolKind.Method);

            start.RegisterOperationAction(operation =>
            {
                var assignment = (ISimpleAssignmentOperation)operation.Operation;
                if ((assignment.Target is IPropertyReferenceOperation target)
                    && (target.Property.Name == "FixedUpdateRate")
                    && (target.Property.ContainingType.ToDisplayString() == GameTypeName))
                {
                    Interlocked.Exchange(ref isRateSet, 1);
                }
            }, OperationKind.SimpleAssignment);

            start.RegisterCompilationEndAction(end =>
            {
                if (Volatile.Read(ref isRateSet) == 1)
                {
                    return;
                }

                foreach (var method in overrides)
                {
                    end.ReportDiagnostic(Diagnostic.Create(Rule, method.Locations[0], method.ContainingType.Name));
                }
            });
        });
    }

    private static bool DerivesFromGame(
        INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == GameTypeName)
            {
                return true;
            }
        }

        return false;
    }
}
