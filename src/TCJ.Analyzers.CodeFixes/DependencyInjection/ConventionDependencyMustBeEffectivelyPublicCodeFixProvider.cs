using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using TCJ.Analyzers.DependencyInjection;

namespace TCJ.Analyzers.CodeFixes.DependencyInjection;

/// <summary>
/// Makes a convention dependency public when changing only that type is sufficient and syntactically safe.
/// </summary>
[ExportCodeFixProvider(
    LanguageNames.CSharp,
    Name = nameof(ConventionDependencyMustBeEffectivelyPublicCodeFixProvider))]
[Shared]
public sealed class ConventionDependencyMustBeEffectivelyPublicCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = ConventionDependencyMustBeEffectivelyPublicAnalyzer.DiagnosticId;
    private const string EquivalenceKey = DiagnosticId + ":MakePublic";

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
                ConventionDependencyMustBeEffectivelyPublicAnalyzer.AccessibilityBlockerPropertyName,
                out string? blocker)
            || !string.Equals(
                blocker,
                ConventionDependencyMustBeEffectivelyPublicAnalyzer.SelfAccessibilityBlocker,
                StringComparison.Ordinal))
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

        if (declaration is null
            || declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.FileKeyword)))
        {
            return;
        }

        INamedTypeSymbol? type = semanticModel.GetDeclaredSymbol(
            declaration,
            context.CancellationToken);

        if (type is null
            || type.DeclaredAccessibility == Accessibility.Public
            || type.DeclaringSyntaxReferences.Length != 1
            || !AllContainingTypesArePublic(type))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Make type public",
                cancellationToken => MakePublicAsync(
                    context.Document,
                    declaration,
                    cancellationToken),
                equivalenceKey: EquivalenceKey),
            diagnostic);
    }

    private static bool AllContainingTypesArePublic(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? containingType = type.ContainingType;
             containingType is not null;
             containingType = containingType.ContainingType)
        {
            if (containingType.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static Task<Document> MakePublicAsync(
        Document document,
        TypeDeclarationSyntax declaration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SyntaxGenerator generator = SyntaxGenerator.GetGenerator(document);
        SyntaxNode updatedDeclaration = generator.WithAccessibility(
            declaration,
            Accessibility.Public);
        SyntaxNode root = declaration.SyntaxTree.GetRoot(cancellationToken);
        SyntaxNode updatedRoot = root.ReplaceNode(declaration, updatedDeclaration);

        return Task.FromResult(document.WithSyntaxRoot(updatedRoot));
    }
}
