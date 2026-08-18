using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace TCJ.Analyzers.Tests.Infrastructure;

internal static class AnalyzerTestHost
{
    private static readonly ImmutableArray<MetadataReference> PlatformReferences = CreatePlatformReferences();

    public static CSharpCompilation CreateCompilation(
        string source,
        params string[] tcjReferenceAssemblyPaths)
    {
        ArgumentNullException.ThrowIfNull(source);

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest));

        return CSharpCompilation.Create(
            assemblyName: "TCJ.Analyzers.Tests.Snippet",
            syntaxTrees: [syntaxTree],
            references: PlatformReferences.AddRange(CreateTcjReferences(tcjReferenceAssemblyPaths)),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));
    }

    public static async Task<ImmutableArray<Diagnostic>> GetAnalyzerDiagnosticsAsync(
        string source,
        DiagnosticAnalyzer analyzer,
        string[]? tcjReferenceAssemblyPaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analyzer);

        CSharpCompilation compilation = CreateCompilation(
            source,
            tcjReferenceAssemblyPaths ?? []);

        CompilationWithAnalyzers compilationWithAnalyzers = new(
            compilation,
            ImmutableArray.Create(analyzer),
            options: null);

        return await compilationWithAnalyzers
            .GetAnalyzerDiagnosticsAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<string> ApplyFirstCodeFixAsync(
        string source,
        DiagnosticAnalyzer analyzer,
        CodeFixProvider codeFixProvider,
        string[]? tcjReferenceAssemblyPaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(codeFixProvider);

        using AdhocWorkspace workspace = new();
        Document document = CreateDocument(
            workspace,
            source,
            tcjReferenceAssemblyPaths ?? []);

        Compilation compilation = await document.Project
            .GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Roslyn test project did not produce a compilation.");

        CompilationWithAnalyzers compilationWithAnalyzers = new(
            compilation,
            ImmutableArray.Create(analyzer),
            options: null);

        ImmutableArray<Diagnostic> diagnostics = await compilationWithAnalyzers
            .GetAnalyzerDiagnosticsAsync(cancellationToken)
            .ConfigureAwait(false);

        Diagnostic diagnostic = diagnostics.FirstOrDefault(candidate =>
                codeFixProvider.FixableDiagnosticIds.Contains(candidate.Id, StringComparer.Ordinal))
            ?? throw new InvalidOperationException("No fixable analyzer diagnostic was produced by the test source.");

        List<CodeAction> actions = [];
        CodeFixContext context = new(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            cancellationToken);

        await codeFixProvider.RegisterCodeFixesAsync(context).ConfigureAwait(false);

        CodeAction action = actions.FirstOrDefault()
            ?? throw new InvalidOperationException("The code-fix provider did not register an action.");

        ImmutableArray<CodeActionOperation> operations = await action
            .GetOperationsAsync(cancellationToken)
            .ConfigureAwait(false);

        ApplyChangesOperation applyChanges = operations
            .OfType<ApplyChangesOperation>()
            .Single();

        Document changedDocument = applyChanges.ChangedSolution.GetDocument(document.Id)
            ?? throw new InvalidOperationException("The code-fix action removed the test document unexpectedly.");

        SourceText changedText = await changedDocument
            .GetTextAsync(cancellationToken)
            .ConfigureAwait(false);

        return changedText.ToString();
    }

    private static Document CreateDocument(
        AdhocWorkspace workspace,
        string source,
        IReadOnlyCollection<string> tcjReferenceAssemblyPaths)
    {
        ProjectId projectId = ProjectId.CreateNewId("AnalyzerTestProject");
        DocumentId documentId = DocumentId.CreateNewId(projectId, "Test0.cs");

        Solution solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "AnalyzerTestProject",
                "AnalyzerTestProject",
                LanguageNames.CSharp,
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithNullableContextOptions(NullableContextOptions.Enable)))
            .AddMetadataReferences(
                projectId,
                PlatformReferences.AddRange(CreateTcjReferences(tcjReferenceAssemblyPaths)))
            .AddDocument(documentId, "Test0.cs", SourceText.From(source));

        return solution.GetDocument(documentId)
            ?? throw new InvalidOperationException("The Roslyn test document could not be created.");
    }

    private static ImmutableArray<MetadataReference> CreatePlatformReferences()
    {
        string trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is unavailable.");

        return trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static assemblyPath => (MetadataReference)MetadataReference.CreateFromFile(assemblyPath))
            .ToImmutableArray();
    }

    private static ImmutableArray<MetadataReference> CreateTcjReferences(
        IEnumerable<string> assemblyPaths)
    {
        ImmutableArray<MetadataReference>.Builder references = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (string assemblyPath in assemblyPaths)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new ArgumentException("TCJ reference assembly paths must not be empty.", nameof(assemblyPaths));
            }

            string fullPath = Path.GetFullPath(assemblyPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("A TCJ reference assembly was not found.", fullPath);
            }

            references.Add(MetadataReference.CreateFromFile(fullPath));
        }

        return references.ToImmutable();
    }
}
