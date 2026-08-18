using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using TCJ.Analyzers.Tests.Infrastructure;
using TCJ.Core.Results;

namespace TCJ.Analyzers.Tests;

public sealed class AnalyzerTestHostTests
{
    [Fact]
    public void Source_snippets_can_use_explicit_TCJ_reference_assemblies()
    {
        string coreAssemblyPath = typeof(Result).Assembly.Location;

        CSharpCompilation compilation = AnalyzerTestHost.CreateCompilation(
            """
            using TCJ.Core.Results;

            namespace Sample;

            public static class Example
            {
                public static Result Create() => Result.Success();
            }
            """,
            coreAssemblyPath);

        ImmutableArray<Diagnostic> errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Code_fix_harness_executes_a_test_only_analyzer_and_fix()
    {
        const string source = """
            namespace Sample;

            public sealed class Example
            {
                public void OldName() { }
                public void NewName() { }

                public void Execute()
                {
                    OldName();
                }
            }
            """;

        string fixedSource = await AnalyzerTestHost.ApplyFirstCodeFixAsync(
            source,
            new TestOnlyRenameAnalyzer(),
            new TestOnlyRenameCodeFixProvider());

        Assert.Contains("NewName();", fixedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("OldName();", fixedSource, StringComparison.Ordinal);
    }

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class TestOnlyRenameAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "TEST0001";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            "Test-only rename",
            "Rename OldName to NewName",
            "Testing",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeIdentifier, SyntaxKind.IdentifierName);
        }

        private static void AnalyzeIdentifier(SyntaxNodeAnalysisContext context)
        {
            IdentifierNameSyntax identifier = (IdentifierNameSyntax)context.Node;
            if (identifier.Identifier.ValueText == "OldName")
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, identifier.GetLocation()));
            }
        }
    }

    private sealed class TestOnlyRenameCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(TestOnlyRenameAnalyzer.DiagnosticId);

        public override FixAllProvider GetFixAllProvider()
            => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            SyntaxNode root = await context.Document
                .GetSyntaxRootAsync(context.CancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The test document does not have a syntax root.");

            Diagnostic diagnostic = context.Diagnostics[0];
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan);
            IdentifierNameSyntax identifier = node as IdentifierNameSyntax
                ?? node.AncestorsAndSelf().OfType<IdentifierNameSyntax>().First();

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Rename to NewName",
                    cancellationToken => ReplaceIdentifierAsync(
                        context.Document,
                        identifier,
                        cancellationToken),
                    equivalenceKey: "RenameToNewName"),
                diagnostic);
        }

        private static async Task<Document> ReplaceIdentifierAsync(
            Document document,
            IdentifierNameSyntax identifier,
            CancellationToken cancellationToken)
        {
            SyntaxNode root = await document
                .GetSyntaxRootAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The test document does not have a syntax root.");

            SyntaxNode updatedRoot = root.ReplaceNode(
                identifier,
                SyntaxFactory.IdentifierName("NewName").WithTriviaFrom(identifier));

            return document.WithSyntaxRoot(updatedRoot);
        }
    }
}
