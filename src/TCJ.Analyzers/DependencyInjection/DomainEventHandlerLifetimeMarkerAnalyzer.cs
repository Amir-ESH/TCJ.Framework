using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TCJ.Analyzers.DependencyInjection;

/// <summary>
/// Reports TCJ lifetime markers on domain-event handlers because handler registration ignores those markers.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DomainEventHandlerLifetimeMarkerAnalyzer : DiagnosticAnalyzer
{
    internal const string DiagnosticId = "TCJ0004";
    internal const string LifetimeMarkersPropertyName = "LifetimeMarkers";

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
        "Domain-event handler lifetime marker is ignored",
        "Type '{0}' is a TCJ domain-event handler and implements TCJ lifetime marker(s): {1}. TCJ domain-event handlers are registered by the handler pipeline, so TCJ lifetime markers do not control handler lifetime",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "TCJ convention scanning registers domain-event handlers through the handler pipeline and skips them during lifetime-marker registration, so TCJ lifetime markers on handlers are misleading.");

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
        INamedTypeSymbol? domainEventHandler =
            context.Compilation.GetTypeByMetadataName(DomainEventHandlerMetadataName);
        if (domainEventHandler is null)
        {
            return;
        }

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

        if (resolvedMarkers.Count == 0)
        {
            return;
        }

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
        INamedTypeSymbol domainEventHandler,
        INamedTypeSymbol? compilerGeneratedAttribute)
    {
        INamedTypeSymbol type = (INamedTypeSymbol)context.Symbol;

        if (type.TypeKind != TypeKind.Class
            || type.IsAbstract
            || !IsEffectivelyPublic(type)
            || IsCompilerGenerated(type, compilerGeneratedAttribute)
            || !IsDomainEventHandler(type, domainEventHandler))
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

        if (matches.Count == 0)
        {
            return;
        }

        ImmutableArray<ResolvedMarkerDefinition> lifetimeMarkers = matches.ToImmutable();
        string markerNames = string.Join(
            ", ",
            lifetimeMarkers.Select(marker => marker.Definition.DisplayName));
        string markerMetadataNames = string.Join(
            ";",
            lifetimeMarkers.Select(marker => marker.Definition.MetadataName));

        ImmutableDictionary<string, string?> properties =
            ImmutableDictionary<string, string?>.Empty.Add(
                LifetimeMarkersPropertyName,
                markerMetadataNames);

        context.ReportDiagnostic(
            Diagnostic.Create(
                Rule,
                GetTypeDeclarationLocation(type),
                properties,
                type.Name,
                markerNames));
    }

    private static bool IsEffectivelyPublic(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
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
        INamedTypeSymbol domainEventHandler)
        => type.AllInterfaces.Any(
            implemented => SymbolEqualityComparer.Default.Equals(
                implemented.OriginalDefinition,
                domainEventHandler));

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
