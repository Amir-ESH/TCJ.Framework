using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TCJ.Analyzers.DependencyInjection;

/// <summary>
/// Reports public concrete dependency types that implement more than one TCJ lifetime marker.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConflictingDependencyLifetimeMarkersAnalyzer : DiagnosticAnalyzer
{
    internal const string DiagnosticId = "TCJ0001";
    internal const string ConflictingMarkersPropertyName = "ConflictingMarkers";

    private const string Category = "TCJ.DependencyInjection";
    private const string DomainEventHandlerMetadataName = "TCJ.Core.DomainEvents.IDomainEventHandler`1";
    private const string CompilerGeneratedAttributeMetadataName = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    private static readonly ImmutableArray<MarkerDefinition> MarkerDefinitions = ImmutableArray.Create(
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.ITransientDependency",
            "ITransientDependency"),
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.IScopedDependency",
            "IScopedDependency"),
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.ISingletonDependency",
            "ISingletonDependency"),
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.ISelfTransientDependency",
            "ISelfTransientDependency"),
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.ISelfScopedDependency",
            "ISelfScopedDependency"),
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.ISelfSingletonDependency",
            "ISelfSingletonDependency"));

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Dependency type has conflicting TCJ lifetime markers",
        "Type '{0}' implements multiple TCJ lifetime markers: {1}. A dependency must declare exactly one lifetime marker",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TCJ convention registration requires each public concrete dependency type to implement exactly one of the six supported lifetime markers.");

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
        ImmutableArray<ResolvedMarkerDefinition>.Builder resolvedMarkers =
            ImmutableArray.CreateBuilder<ResolvedMarkerDefinition>();

        foreach (MarkerDefinition definition in MarkerDefinitions)
        {
            INamedTypeSymbol? symbol = context.Compilation.GetTypeByMetadataName(definition.MetadataName);
            if (symbol is not null)
            {
                resolvedMarkers.Add(new ResolvedMarkerDefinition(definition, symbol));
            }
        }

        if (resolvedMarkers.Count < 2)
        {
            return;
        }

        INamedTypeSymbol? domainEventHandler =
            context.Compilation.GetTypeByMetadataName(DomainEventHandlerMetadataName);
        INamedTypeSymbol? compilerGeneratedAttribute =
            context.Compilation.GetTypeByMetadataName(CompilerGeneratedAttributeMetadataName);
        ImmutableArray<ResolvedMarkerDefinition> markers = resolvedMarkers.ToImmutable();

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
        ImmutableArray<ResolvedMarkerDefinition> markers,
        INamedTypeSymbol? domainEventHandler,
        INamedTypeSymbol? compilerGeneratedAttribute)
    {
        INamedTypeSymbol type = (INamedTypeSymbol)context.Symbol;

        if (type.TypeKind != TypeKind.Class
            || type.IsAbstract
            || type.DeclaredAccessibility != Accessibility.Public
            || IsCompilerGenerated(type, compilerGeneratedAttribute)
            || IsDomainEventHandler(type, domainEventHandler))
        {
            return;
        }

        ImmutableArray<ResolvedMarkerDefinition>.Builder matches =
            ImmutableArray.CreateBuilder<ResolvedMarkerDefinition>();

        foreach (ResolvedMarkerDefinition marker in markers)
        {
            if (type.AllInterfaces.Any(
                implemented => SymbolEqualityComparer.Default.Equals(implemented, marker.Symbol)))
            {
                matches.Add(marker);
            }
        }

        if (matches.Count <= 1)
        {
            return;
        }

        ImmutableArray<ResolvedMarkerDefinition> conflictingMarkers = matches.ToImmutable();
        string markerNames = string.Join(
            ", ",
            conflictingMarkers.Select(marker => marker.Definition.DisplayName));
        string markerMetadataNames = string.Join(
            ";",
            conflictingMarkers.Select(marker => marker.Definition.MetadataName));

        ImmutableDictionary<string, string?> properties =
            ImmutableDictionary<string, string?>.Empty.Add(
                ConflictingMarkersPropertyName,
                markerMetadataNames);

        context.ReportDiagnostic(
            Diagnostic.Create(
                Rule,
                GetTypeDeclarationLocation(type),
                properties,
                type.Name,
                markerNames));
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

    private sealed class MarkerDefinition
    {
        public MarkerDefinition(string metadataName, string displayName)
        {
            MetadataName = metadataName;
            DisplayName = displayName;
        }

        public string MetadataName { get; }

        public string DisplayName { get; }
    }

    private sealed class ResolvedMarkerDefinition
    {
        public ResolvedMarkerDefinition(MarkerDefinition definition, INamedTypeSymbol symbol)
        {
            Definition = definition;
            Symbol = symbol;
        }

        public MarkerDefinition Definition { get; }

        public INamedTypeSymbol Symbol { get; }
    }
}
