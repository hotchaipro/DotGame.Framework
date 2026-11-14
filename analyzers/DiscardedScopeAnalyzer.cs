// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DotGame.Analyzers;

/// <summary>
/// Reports a scope that's thrown away (DG0006): <c>spriteBatch.Begin();</c> starts a batch that nothing
/// ends, and <c>spriteBatch.UseShader(shader);</c> a shader nothing goes back from. A scope is a value
/// type named <c>…Scope</c> that's disposable, from the framework or from any library built on it, so a
/// library's scopes are covered without the rule naming the library.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedScopeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        id: "DG0006",
        title: "A scope is thrown away",
        messageFormat: "'{0}' returns a {1} that must be disposed to end it: use 'using'",
        category: "DotGame.Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The framework's scopes, such as a sprite batch's or a scissor's, last until they're disposed. One that's thrown away never ends.");

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
        var value = statement.Operation;
        if ((value is ISimpleAssignmentOperation assignment) && (assignment.Target is IDiscardOperation))
        {
            value = assignment.Value;
        }

        if ((value is IInvocationOperation invocation) && IsScope(invocation.TargetMethod.ReturnType))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, statement.Syntax.GetLocation(), invocation.TargetMethod.Name, invocation.TargetMethod.ReturnType.Name));
        }
    }

    private static bool IsScope(
        ITypeSymbol type)
    {
        if ((!type.IsValueType) || (!type.Name.EndsWith("Scope", System.StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (var implemented in type.AllInterfaces)
        {
            if (implemented.SpecialType == SpecialType.System_IDisposable)
            {
                return true;
            }
        }

        return false;
    }
}
