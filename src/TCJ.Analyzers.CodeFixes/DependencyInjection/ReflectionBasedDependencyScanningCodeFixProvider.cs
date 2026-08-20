using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TCJ.Analyzers.AotTrimming;

namespace TCJ.Analyzers.CodeFixes.DependencyInjection;

/// <summary>
/// Removes an empty assembly scan argument when it has no observable registration effect.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ReflectionBasedDependencyScanningCodeFixProvider))]
[Shared]
public sealed class ReflectionBasedDependencyScanningCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(ReflectionBasedDependencyScanningAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        InvocationExpressionSyntax? invocation = root.FindNode(context.Diagnostics[0].Location.SourceSpan)
            as InvocationExpressionSyntax;
        if (invocation is null || invocation.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        if (invocation.ArgumentList.Arguments.Count != 1
            || !IsEmptyArrayExpression(invocation.ArgumentList.Arguments[0].Expression))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Remove empty TCJ assembly scan",
                cancellationToken => RemoveArgumentAsync(context.Document, invocation, cancellationToken),
                ReflectionBasedDependencyScanningAnalyzer.DiagnosticId + ":RemoveEmptyScan"),
            context.Diagnostics[0]);
    }

    private static bool IsEmptyArrayExpression(ExpressionSyntax expression)
        => expression is ArrayCreationExpressionSyntax array
           && array.Initializer is not null
           && array.Initializer.Expressions.Count == 0;

    private static Task<Document> RemoveArgumentAsync(
        Document document,
        InvocationExpressionSyntax invocation,
        CancellationToken cancellationToken)
    {
        SyntaxNode root = invocation.SyntaxTree.GetRoot(cancellationToken);
        SyntaxNode updated = root.ReplaceNode(
            invocation.ArgumentList,
            invocation.ArgumentList.WithArguments(SyntaxFactory.SeparatedList<ArgumentSyntax>()));

        return Task.FromResult(document.WithSyntaxRoot(updated));
    }
}
