using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TCJ.Analyzers.DependencyInjection;

namespace TCJ.Analyzers.CodeFixes.DependencyInjection;

/// <summary>
/// Removes directly declared conflicting TCJ lifetime markers while keeping the user's selected marker.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ConflictingDependencyLifetimeMarkersCodeFixProvider))]
[Shared]
public sealed class ConflictingDependencyLifetimeMarkersCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = ConflictingDependencyLifetimeMarkersAnalyzer.DiagnosticId;
    private const string ConflictingMarkersPropertyName =
        ConflictingDependencyLifetimeMarkersAnalyzer.ConflictingMarkersPropertyName;
    private const string EquivalenceKeyPrefix = DiagnosticId + ":Keep:";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds
        => ImmutableArray.Create(DiagnosticId);

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider()
        => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Diagnostic diagnostic = context.Diagnostics.First();
        if (!diagnostic.Properties.TryGetValue(
                ConflictingMarkersPropertyName,
                out string? serializedMarkers)
            || string.IsNullOrWhiteSpace(serializedMarkers))
        {
            return;
        }

        SyntaxNode? root = await context.Document
            .GetSyntaxRootAsync(context.CancellationToken)
            .ConfigureAwait(false);
        SemanticModel? semanticModel = await context.Document
            .GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);

        if (root is null || semanticModel is null)
        {
            return;
        }

        TypeDeclarationSyntax? declaration = root
            .FindToken(diagnostic.Location.SourceSpan.Start)
            .Parent?
            .AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (declaration?.BaseList is null)
        {
            return;
        }

        string[] conflictingMarkerMetadataNames = serializedMarkers
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

        if (conflictingMarkerMetadataNames.Length <= 1)
        {
            return;
        }

        Dictionary<string, INamedTypeSymbol> markerSymbols = ResolveMarkerSymbols(
            semanticModel.Compilation,
            conflictingMarkerMetadataNames);

        if (markerSymbols.Count != conflictingMarkerMetadataNames.Length)
        {
            return;
        }

        Dictionary<string, ImmutableArray<BaseTypeSyntax>> directlyDeclaredMarkers =
            GetDirectlyDeclaredMarkers(
                semanticModel,
                declaration,
                markerSymbols,
                context.CancellationToken);

        foreach (string markerToKeep in conflictingMarkerMetadataNames)
        {
            string[] markersToRemove = conflictingMarkerMetadataNames
                .Where(marker => !string.Equals(marker, markerToKeep, StringComparison.Ordinal))
                .ToArray();

            if (markersToRemove.Any(marker => !directlyDeclaredMarkers.ContainsKey(marker)))
            {
                continue;
            }

            ImmutableArray<BaseTypeSyntax> baseTypesToRemove = markersToRemove
                .SelectMany(marker => directlyDeclaredMarkers[marker])
                .OrderByDescending(baseType => baseType.SpanStart)
                .ToImmutableArray();

            string markerDisplayName = GetSimpleMetadataName(markerToKeep);
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Keep {markerDisplayName}",
                    cancellationToken => RemoveMarkersAsync(
                        context.Document,
                        declaration,
                        baseTypesToRemove,
                        cancellationToken),
                    equivalenceKey: EquivalenceKeyPrefix + markerToKeep),
                diagnostic);
        }
    }

    private static Dictionary<string, INamedTypeSymbol> ResolveMarkerSymbols(
        Compilation compilation,
        IEnumerable<string> metadataNames)
    {
        Dictionary<string, INamedTypeSymbol> symbols =
            new(StringComparer.Ordinal);

        foreach (string metadataName in metadataNames)
        {
            INamedTypeSymbol? symbol = compilation.GetTypeByMetadataName(metadataName);
            if (symbol is not null)
            {
                symbols[metadataName] = symbol;
            }
        }

        return symbols;
    }

    private static Dictionary<string, ImmutableArray<BaseTypeSyntax>> GetDirectlyDeclaredMarkers(
        SemanticModel semanticModel,
        TypeDeclarationSyntax declaration,
        IReadOnlyDictionary<string, INamedTypeSymbol> markerSymbols,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ImmutableArray<BaseTypeSyntax>.Builder> builders =
            new(StringComparer.Ordinal);

        foreach (BaseTypeSyntax baseType in declaration.BaseList!.Types)
        {
            ITypeSymbol? baseTypeSymbol = semanticModel
                .GetTypeInfo(baseType.Type, cancellationToken)
                .Type;

            foreach (KeyValuePair<string, INamedTypeSymbol> marker in markerSymbols)
            {
                if (!SymbolEqualityComparer.Default.Equals(baseTypeSymbol, marker.Value))
                {
                    continue;
                }

                if (!builders.TryGetValue(marker.Key, out ImmutableArray<BaseTypeSyntax>.Builder? builder))
                {
                    builder = ImmutableArray.CreateBuilder<BaseTypeSyntax>();
                    builders.Add(marker.Key, builder);
                }

                builder.Add(baseType);
                break;
            }
        }

        return builders.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutable(),
            StringComparer.Ordinal);
    }

    private static Task<Document> RemoveMarkersAsync(
        Document document,
        TypeDeclarationSyntax declaration,
        ImmutableArray<BaseTypeSyntax> baseTypesToRemove,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        BaseListSyntax baseList = declaration.BaseList
            ?? throw new InvalidOperationException("The diagnostic type no longer has a base list.");
        SeparatedSyntaxList<BaseTypeSyntax> updatedTypes = baseList.Types;

        foreach (BaseTypeSyntax baseType in baseTypesToRemove)
        {
            updatedTypes = updatedTypes.Remove(baseType);
        }

        TypeDeclarationSyntax updatedDeclaration = declaration.WithBaseList(
            baseList.WithTypes(updatedTypes));

        SyntaxNode root = declaration.SyntaxTree.GetRoot(cancellationToken);
        SyntaxNode updatedRoot = root.ReplaceNode(declaration, updatedDeclaration);

        return Task.FromResult(document.WithSyntaxRoot(updatedRoot));
    }

    private static string GetSimpleMetadataName(string metadataName)
    {
        int separatorIndex = metadataName.LastIndexOf('.');
        return separatorIndex >= 0
            ? metadataName.Substring(separatorIndex + 1)
            : metadataName;
    }
}
