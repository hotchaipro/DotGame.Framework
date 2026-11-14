// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DotGame.Analyzers;

/// <summary>
/// Reports a call that makes one of the framework's immutable values and throws it away (DG0004):
/// <c>Rectangle.Inflate(bounds, 2, 2);</c> leaves <c>bounds</c> as it was.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedValueResultAnalyzer : DiagnosticAnalyzer
{
    private const string FrameworkNamespace = "DotGame.Framework";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        id: "DG0004",
        title: "The new value from an immutable value's method is thrown away",
        messageFormat: "'{0}' returns a new {1} and changes nothing else: assign the result",
        category: "DotGame.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The framework's Rectangle, RectangleInt and the other values are immutable. A method that returns the same type gives a new value, so calling it as a statement does nothing.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(
        AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeStatement, OperationKind.ExpressionStatement);
    }

    private static void AnalyzeStatement(
        OperationAnalysisContext context)
    {
        var statement = (IExpressionStatementOperation)context.Operation;
        if (statement.Operation is not IInvocationOperation invocation)
        {
            return;
        }

        var method = invocation.TargetMethod;
        var type = method.ContainingType;
        if ((!type.IsValueType)
            || (!type.IsReadOnly)
            || (type.ContainingNamespace?.ToDisplayString() != FrameworkNamespace)
            || (!SymbolEqualityComparer.Default.Equals(method.ReturnType, type)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.Syntax.GetLocation(), method.Name, type.Name));
    }
}
