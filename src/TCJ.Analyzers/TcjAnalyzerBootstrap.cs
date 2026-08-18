using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TCJ.Analyzers;

/// <summary>
/// Provides the analyzer assembly entry point before the first TCJ diagnostic rule is introduced.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TcjAnalyzerBootstrap : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray<DiagnosticDescriptor>.Empty;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
    }
}
