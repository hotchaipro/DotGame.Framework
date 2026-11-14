// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotGame.Analyzers;

/// <summary>
/// Keeps platform code to the platform contract (DG0002): in an assembly the framework gives
/// its internals to (a platform: DotGame.Framework.Desktop, .Android, .iOS, .WebGL), an
/// internal framework type or member may be used only if it's in the contract namespace,
/// DotGame.Framework.Platform (the strategy interfaces, Strategy&lt;T&gt;, GameHost), or is
/// marked [PlatformContract] (the strategy behind a framework object, and the constructors that
/// wrap one).
/// </summary>
/// <remarks>
/// InternalsVisibleTo gives the platforms every internal of the framework; this narrows what
/// they may use to what's meant for them, so the framework's implementation can change without
/// breaking a platform, and a platform can't skip past the framework's invariants. Assemblies the
/// framework gives nothing to (games, libraries on it, tests) have nothing to check.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PlatformContractAnalyzer : DiagnosticAnalyzer
{
    private const string FrameworkAssemblyName = "DotGame.Framework";
    private const string ContractNamespace = "DotGame.Framework.Platform";
    private const string ContractAttributeName = "DotGame.Framework.Platform.PlatformContractAttribute";

    private static readonly DiagnosticDescriptor OutsideContract = new DiagnosticDescriptor(
        id: "DG0002",
        title: "Platform code uses a framework internal outside the platform contract",
        messageFormat: "'{0}' is internal to the framework and not part of the platform contract: drive the framework through GameHost and the strategies (DotGame.Framework.Platform), or mark the member [PlatformContract] if platforms need it",
        category: "DotGame.Platform",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Platform assemblies see every framework internal through InternalsVisibleTo; they may use only the platform contract.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(OutsideContract);

    public override void Initialize(
        AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(
        CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        if (compilation.AssemblyName == FrameworkAssemblyName)
        {
            return;
        }

        IAssemblySymbol? framework = null;
        foreach (var reference in compilation.References)
        {
            if ((compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                && (assembly.Name == FrameworkAssemblyName))
            {
                framework = assembly;
                break;
            }
        }

        // Only an assembly the framework gives its internals to can use them
        if ((framework is null) || !framework.GivesAccessTo(compilation.Assembly))
        {
            return;
        }

        var contractAttribute = framework.GetTypeByMetadataName(ContractAttributeName);
        context.RegisterSyntaxNodeAction(
            nodeContext => AnalyzeName(nodeContext, framework, contractAttribute),
            SyntaxKind.IdentifierName,
            SyntaxKind.GenericName);
        context.RegisterSyntaxNodeAction(
            nodeContext => AnalyzeCreation(nodeContext, framework, contractAttribute),
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void AnalyzeName(
        SyntaxNodeAnalysisContext context,
        IAssemblySymbol framework,
        INamedTypeSymbol? contractAttribute)
    {
        // A documentation comment's reference (a cref) mentions a member; it doesn't use it
        if (context.Node.IsPartOfStructuredTrivia())
        {
            return;
        }

        // A creation's constructor is checked by AnalyzeCreation; its type name is checked here
        var symbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
        if ((symbol is IMethodSymbol { MethodKind: MethodKind.Constructor }) || (symbol is null))
        {
            return;
        }

        Report(context, symbol, framework, contractAttribute);
    }

    private static void AnalyzeCreation(
        SyntaxNodeAnalysisContext context,
        IAssemblySymbol framework,
        INamedTypeSymbol? contractAttribute)
    {
        var symbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
        if (symbol is not null)
        {
            Report(context, symbol, framework, contractAttribute);
        }
    }

    private static void Report(
        SyntaxNodeAnalysisContext context,
        ISymbol symbol,
        IAssemblySymbol framework,
        INamedTypeSymbol? contractAttribute)
    {
        symbol = symbol.OriginalDefinition;
        if ((symbol is INamespaceSymbol) || (symbol.Kind is SymbolKind.Local or SymbolKind.Parameter or SymbolKind.TypeParameter or SymbolKind.RangeVariable or SymbolKind.Label))
        {
            return;
        }

        if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, framework))
        {
            return;
        }

        if (!IsInternal(symbol) || IsContract(symbol, contractAttribute))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(OutsideContract, context.Node.GetLocation(), symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
    }

    // Not visible outside the framework: the symbol, or a type containing it, isn't public (or
    // protected, for members a platform reaches through a derived type)
    private static bool IsInternal(
        ISymbol symbol)
    {
        for (var current = symbol; current is not null && current is not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedAndInternal or Accessibility.Private)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsContract(
        ISymbol symbol,
        INamedTypeSymbol? contractAttribute)
    {
        // Declared in the contract namespace (or a type in it)
        var ns = symbol.ContainingNamespace?.ToDisplayString();
        if ((ns is not null) && ((ns == ContractNamespace) || ns.StartsWith(ContractNamespace + ".", System.StringComparison.Ordinal)))
        {
            return true;
        }

        if (contractAttribute is null)
        {
            return false;
        }

        // Marked [PlatformContract]: the member, or the property an accessor belongs to
        if (HasAttribute(symbol, contractAttribute))
        {
            return true;
        }

        return (symbol is IMethodSymbol { AssociatedSymbol: { } associated }) && HasAttribute(associated, contractAttribute);
    }

    private static bool HasAttribute(
        ISymbol symbol,
        INamedTypeSymbol attribute)
    {
        foreach (var data in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
            {
                return true;
            }
        }

        return false;
    }
}
