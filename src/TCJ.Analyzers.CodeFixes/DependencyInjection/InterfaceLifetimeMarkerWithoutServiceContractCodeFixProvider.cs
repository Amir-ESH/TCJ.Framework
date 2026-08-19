using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TCJ.Analyzers.DependencyInjection;

namespace TCJ.Analyzers.CodeFixes.DependencyInjection;

/// <summary>
/// Switches a directly declared interface-registration lifetime marker to the corresponding self-registration marker.
/// </summary>
[ExportCodeFixProvider(
    LanguageNames.CSharp,
    Name = nameof(InterfaceLifetimeMarkerWithoutServiceContractCodeFixProvider))]
[Shared]
public sealed class InterfaceLifetimeMarkerWithoutServiceContractCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = InterfaceLifetimeMarkerWithoutServiceContractAnalyzer.DiagnosticId;
    private const string EquivalenceKeyPrefix = DiagnosticId + ":UseSelf:";

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
                InterfaceLifetimeMarkerWithoutServiceContractAnalyzer.MarkerMetadataNameProperty,
                out string? markerMetadataName)
            || string.IsNullOrWhiteSpace(markerMetadataName)
            || !diagnostic.Properties.TryGetValue(
                InterfaceLifetimeMarkerWithoutServiceContractAnalyzer.SelfMarkerMetadataNameProperty,
                out string? selfMarkerMetadataName)
            || string.IsNullOrWhiteSpace(selfMarkerMetadataName))
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

        INamedTypeSymbol? markerSymbol = semanticModel.Compilation.GetTypeByMetadataName(markerMetadataName);
        INamedTypeSymbol? selfMarkerSymbol = semanticModel.Compilation.GetTypeByMetadataName(selfMarkerMetadataName);
        if (markerSymbol is null || selfMarkerSymbol is null)
        {
            return;
        }

        BaseTypeSyntax? markerBaseType = declaration.BaseList.Types.FirstOrDefault(
            baseType => SymbolEqualityComparer.Default.Equals(
                semanticModel.GetTypeInfo(baseType.Type, context.CancellationToken).Type,
                markerSymbol));

        // Inherited/custom marker declarations are diagnosed, but changing their source would
        // require editing an unrelated interface or base type and is therefore not a safe local fix.
        if (markerBaseType is null)
        {
            return;
        }

        string selfMarkerDisplayName = selfMarkerSymbol.Name;
        context.RegisterCodeFix(
            CodeAction.Create(
                $"Use {selfMarkerDisplayName}",
                cancellationToken => ReplaceMarkerAsync(
                    context.Document,
                    declaration,
                    markerBaseType,
                    selfMarkerSymbol,
                    semanticModel,
                    cancellationToken),
                equivalenceKey: EquivalenceKeyPrefix + selfMarkerMetadataName),
            diagnostic);
    }

    private static Task<Document> ReplaceMarkerAsync(
        Document document,
        TypeDeclarationSyntax declaration,
        BaseTypeSyntax markerBaseType,
        INamedTypeSymbol selfMarkerSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string replacementName = selfMarkerSymbol.ToMinimalDisplayString(
            semanticModel,
            markerBaseType.Type.SpanStart);
        TypeSyntax replacementType = SyntaxFactory
            .ParseTypeName(replacementName)
            .WithTriviaFrom(markerBaseType.Type);
        BaseTypeSyntax updatedBaseType = markerBaseType.WithType(replacementType);
        TypeDeclarationSyntax updatedDeclaration = declaration.ReplaceNode(markerBaseType, updatedBaseType);

        SyntaxNode root = declaration.SyntaxTree.GetRoot(cancellationToken);
        SyntaxNode updatedRoot = root.ReplaceNode(declaration, updatedDeclaration);

        return Task.FromResult(document.WithSyntaxRoot(updatedRoot));
    }
}
