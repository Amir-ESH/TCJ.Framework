using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TCJ.Analyzers.DependencyInjection;

/// <summary>
/// Reports interface-registration lifetime markers on public concrete dependency types that expose no eligible service interface.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InterfaceLifetimeMarkerWithoutServiceContractAnalyzer : DiagnosticAnalyzer
{
    internal const string DiagnosticId = "TCJ0002";
    internal const string MarkerMetadataNameProperty = "MarkerMetadataName";
    internal const string SelfMarkerMetadataNameProperty = "SelfMarkerMetadataName";

    private const string Category = "TCJ.DependencyInjection";
    private const string DependencyMetadataName = "TCJ.DependencyInjection.Lifetimes.IDependency";
    private const string DisposableMetadataName = "System.IDisposable";
    private const string AsyncDisposableMetadataName = "System.IAsyncDisposable";
    private const string DomainEventHandlerMetadataName = "TCJ.Core.DomainEvents.IDomainEventHandler`1";
    private const string CompilerGeneratedAttributeMetadataName = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    private static readonly ImmutableArray<MarkerDefinition> MarkerDefinitions = ImmutableArray.Create(
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.ITransientDependency",
            "ITransientDependency",
            registerAsSelf: false,
            "TCJ.DependencyInjection.Lifetimes.ISelfTransientDependency",
            "ISelfTransientDependency"),
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.IScopedDependency",
            "IScopedDependency",
            registerAsSelf: false,
            "TCJ.DependencyInjection.Lifetimes.ISelfScopedDependency",
            "ISelfScopedDependency"),
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.ISingletonDependency",
            "ISingletonDependency",
            registerAsSelf: false,
            "TCJ.DependencyInjection.Lifetimes.ISelfSingletonDependency",
            "ISelfSingletonDependency"),
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.ISelfTransientDependency",
            "ISelfTransientDependency",
            registerAsSelf: true,
            selfMarkerMetadataName: null,
            selfMarkerDisplayName: null),
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.ISelfScopedDependency",
            "ISelfScopedDependency",
            registerAsSelf: true,
            selfMarkerMetadataName: null,
            selfMarkerDisplayName: null),
        new MarkerDefinition(
            "TCJ.DependencyInjection.Lifetimes.ISelfSingletonDependency",
            "ISelfSingletonDependency",
            registerAsSelf: true,
            selfMarkerMetadataName: null,
            selfMarkerDisplayName: null));

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Interface lifetime marker requires a service contract",
        "Type '{0}' implements '{1}' but exposes no eligible service interface. Implement a service contract or use '{2}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TCJ interface-registration lifetime markers require at least one service interface after applying the convention scanner exclusions.");

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
        INamedTypeSymbol? dependency = context.Compilation.GetTypeByMetadataName(DependencyMetadataName);
        if (dependency is null)
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

        if (!resolvedMarkers.Any(marker => !marker.Definition.RegisterAsSelf))
        {
            return;
        }

        INamedTypeSymbol? disposable = context.Compilation.GetTypeByMetadataName(DisposableMetadataName);
        INamedTypeSymbol? asyncDisposable = context.Compilation.GetTypeByMetadataName(AsyncDisposableMetadataName);
        INamedTypeSymbol? domainEventHandler =
            context.Compilation.GetTypeByMetadataName(DomainEventHandlerMetadataName);
        INamedTypeSymbol? compilerGeneratedAttribute =
            context.Compilation.GetTypeByMetadataName(CompilerGeneratedAttributeMetadataName);
        ImmutableArray<ResolvedMarkerDefinition> markers = resolvedMarkers.ToImmutable();

        context.RegisterSymbolAction(
            symbolContext => AnalyzeNamedType(
                symbolContext,
                markers,
                dependency,
                disposable,
                asyncDisposable,
                domainEventHandler,
                compilerGeneratedAttribute),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        ImmutableArray<ResolvedMarkerDefinition> markers,
        INamedTypeSymbol dependency,
        INamedTypeSymbol? disposable,
        INamedTypeSymbol? asyncDisposable,
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

        // The runtime scanner validates marker conflicts before it validates service contracts.
        // TCJ0001 owns the multi-marker failure, so do not cascade TCJ0002 on the same type.
        if (matches.Count != 1)
        {
            return;
        }

        ResolvedMarkerDefinition lifetime = matches[0];
        if (lifetime.Definition.RegisterAsSelf
            || lifetime.Definition.SelfMarkerMetadataName is null
            || lifetime.Definition.SelfMarkerDisplayName is null)
        {
            return;
        }

        if (HasEligibleServiceInterface(
                type,
                dependency,
                disposable,
                asyncDisposable,
                domainEventHandler))
        {
            return;
        }

        ImmutableDictionary<string, string?> properties =
            ImmutableDictionary<string, string?>.Empty
                .Add(MarkerMetadataNameProperty, lifetime.Definition.MetadataName)
                .Add(SelfMarkerMetadataNameProperty, lifetime.Definition.SelfMarkerMetadataName);

        context.ReportDiagnostic(
            Diagnostic.Create(
                Rule,
                GetTypeDeclarationLocation(type),
                properties,
                type.Name,
                lifetime.Definition.DisplayName,
                lifetime.Definition.SelfMarkerDisplayName));
    }

    private static bool HasEligibleServiceInterface(
        INamedTypeSymbol type,
        INamedTypeSymbol dependency,
        INamedTypeSymbol? disposable,
        INamedTypeSymbol? asyncDisposable,
        INamedTypeSymbol? domainEventHandler)
    {
        foreach (INamedTypeSymbol implementedInterface in type.AllInterfaces)
        {
            if (IsDependencyInterface(implementedInterface, dependency)
                || SymbolEqualityComparer.Default.Equals(implementedInterface, disposable)
                || SymbolEqualityComparer.Default.Equals(implementedInterface, asyncDisposable)
                || IsDomainEventHandlerInterface(implementedInterface, domainEventHandler))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsDependencyInterface(
        INamedTypeSymbol interfaceType,
        INamedTypeSymbol dependency)
        => SymbolEqualityComparer.Default.Equals(interfaceType, dependency)
            || interfaceType.AllInterfaces.Any(
                inherited => SymbolEqualityComparer.Default.Equals(inherited, dependency));

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
        => type.AllInterfaces.Any(
            implemented => IsDomainEventHandlerInterface(implemented, domainEventHandler));

    private static bool IsDomainEventHandlerInterface(
        INamedTypeSymbol interfaceType,
        INamedTypeSymbol? domainEventHandler)
        => domainEventHandler is not null
            && interfaceType.IsGenericType
            && SymbolEqualityComparer.Default.Equals(
                interfaceType.OriginalDefinition,
                domainEventHandler);

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
        public MarkerDefinition(
            string metadataName,
            string displayName,
            bool registerAsSelf,
            string? selfMarkerMetadataName,
            string? selfMarkerDisplayName)
        {
            MetadataName = metadataName;
            DisplayName = displayName;
            RegisterAsSelf = registerAsSelf;
            SelfMarkerMetadataName = selfMarkerMetadataName;
            SelfMarkerDisplayName = selfMarkerDisplayName;
        }

        public string MetadataName { get; }

        public string DisplayName { get; }

        public bool RegisterAsSelf { get; }

        public string? SelfMarkerMetadataName { get; }

        public string? SelfMarkerDisplayName { get; }
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
