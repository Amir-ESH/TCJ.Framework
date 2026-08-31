using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TCJ.Analyzers.DependencyInjection;

/// <summary>
/// Reports concrete convention dependencies whose effective accessibility prevents public-type scanning.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConventionDependencyMustBeEffectivelyPublicAnalyzer : DiagnosticAnalyzer
{
    internal const string DiagnosticId = "TCJ0003";
    internal const string AccessibilityBlockerPropertyName = "AccessibilityBlocker";
    internal const string SelfAccessibilityBlocker = "Self";
    internal const string ContainingTypeAccessibilityBlocker = "ContainingType";

    private const string Category = "TCJ.DependencyInjection";
    private const string DomainEventHandlerMetadataName = "TCJ.Core.DomainEvents.IDomainEventHandler`1";
    private const string CompilerGeneratedAttributeMetadataName = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    private static readonly ImmutableArray<string> MarkerMetadataNames = ImmutableArray.Create(
        "TCJ.DependencyInjection.Lifetimes.ITransientDependency",
        "TCJ.DependencyInjection.Lifetimes.IScopedDependency",
        "TCJ.DependencyInjection.Lifetimes.ISingletonDependency",
        "TCJ.DependencyInjection.Lifetimes.ISelfTransientDependency",
        "TCJ.DependencyInjection.Lifetimes.ISelfScopedDependency",
        "TCJ.DependencyInjection.Lifetimes.ISelfSingletonDependency");

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Convention dependency must be effectively public",
        "Type '{0}' uses a TCJ convention-registration marker but is not effectively public because {1}. Convention scanning requires an effectively public concrete type",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TCJ convention registration can discover a marked concrete dependency only when the type and every containing type are public.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(RegisterCompilationActions);
    }

    private static void RegisterCompilationActions(CompilationStartAnalysisContext context)
    {
        ImmutableArray<INamedTypeSymbol>.Builder resolvedMarkers =
            ImmutableArray.CreateBuilder<INamedTypeSymbol>();

        foreach (string metadataName in MarkerMetadataNames)
        {
            INamedTypeSymbol? symbol = context.Compilation.GetTypeByMetadataName(metadataName);
            if (symbol is not null)
            {
                resolvedMarkers.Add(symbol);
            }
        }

        if (resolvedMarkers.Count == 0)
        {
            return;
        }

        INamedTypeSymbol? domainEventHandler =
            context.Compilation.GetTypeByMetadataName(DomainEventHandlerMetadataName);
        INamedTypeSymbol? compilerGeneratedAttribute =
            context.Compilation.GetTypeByMetadataName(CompilerGeneratedAttributeMetadataName);
        ImmutableArray<INamedTypeSymbol> markers = resolvedMarkers.ToImmutable();

        context.RegisterSymbolAction(
            symbolContext => AnalyzeNamedType(
                symbolContext,
                markers,
                domainEventHandler,
                compilerGeneratedAttribute),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        ImmutableArray<INamedTypeSymbol> markers,
        INamedTypeSymbol? domainEventHandler,
        INamedTypeSymbol? compilerGeneratedAttribute)
    {
        INamedTypeSymbol type = (INamedTypeSymbol)context.Symbol;

        if (type.TypeKind != TypeKind.Class
            || type.IsAbstract
            || IsCompilerGenerated(type, compilerGeneratedAttribute)
            || IsDomainEventHandler(type, domainEventHandler)
            || !ImplementsConventionMarker(type, markers))
        {
            return;
        }

        INamedTypeSymbol? accessibilityBlocker = GetAccessibilityBlocker(type);
        if (accessibilityBlocker is null)
        {
            return;
        }

        bool blockedByContainingType = !SymbolEqualityComparer.Default.Equals(type, accessibilityBlocker);
        string blockerDescription = blockedByContainingType
            ? $"containing type '{accessibilityBlocker.Name}' is not public"
            : "the type itself is not public";
        string blockerProperty = blockedByContainingType
            ? ContainingTypeAccessibilityBlocker
            : SelfAccessibilityBlocker;

        ImmutableDictionary<string, string?> properties =
            ImmutableDictionary<string, string?>.Empty.Add(
                AccessibilityBlockerPropertyName,
                blockerProperty);

        context.ReportDiagnostic(
            Diagnostic.Create(
                Rule,
                GetTypeDeclarationLocation(type),
                properties,
                type.Name,
                blockerDescription));
    }

    private static bool ImplementsConventionMarker(
        INamedTypeSymbol type,
        ImmutableArray<INamedTypeSymbol> markers)
    {
        foreach (INamedTypeSymbol marker in markers)
        {
            if (type.AllInterfaces.Any(
                implemented => SymbolEqualityComparer.Default.Equals(implemented, marker)))
            {
                return true;
            }
        }

        return false;
    }

    private static INamedTypeSymbol? GetAccessibilityBlocker(INamedTypeSymbol type)
    {
        if (type.DeclaredAccessibility != Accessibility.Public)
        {
            return type;
        }

        for (INamedTypeSymbol? containingType = type.ContainingType;
             containingType is not null;
             containingType = containingType.ContainingType)
        {
            if (containingType.DeclaredAccessibility != Accessibility.Public)
            {
                return containingType;
            }
        }

        return null;
    }

    private static bool IsCompilerGenerated(
        INamedTypeSymbol type,
        INamedTypeSymbol? compilerGeneratedAttribute)
    {
        if (compilerGeneratedAttribute is null)
        {
            return false;
        }

        return type.GetAttributes().Any(
            attribute => SymbolEqualityComparer.Default.Equals(
                attribute.AttributeClass,
                compilerGeneratedAttribute));
    }

    private static bool IsDomainEventHandler(
        INamedTypeSymbol type,
        INamedTypeSymbol? domainEventHandler)
    {
        if (domainEventHandler is null)
        {
            return false;
        }

        return type.AllInterfaces.Any(
            implemented => SymbolEqualityComparer.Default.Equals(
                implemented.OriginalDefinition,
                domainEventHandler));
    }

    private static Location GetTypeDeclarationLocation(INamedTypeSymbol type)
    {
        SyntaxReference? declarationReference = type.DeclaringSyntaxReferences
            .OrderBy(reference => reference.SyntaxTree.FilePath, StringComparer.Ordinal)
            .ThenBy(reference => reference.Span.Start)
            .FirstOrDefault();

        if (declarationReference?.GetSyntax() is TypeDeclarationSyntax declaration)
        {
            return declaration.Identifier.GetLocation();
        }

        return type.Locations.First(location => location.IsInSource);
    }
}
