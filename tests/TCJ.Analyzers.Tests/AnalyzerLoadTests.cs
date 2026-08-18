using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using TCJ.Analyzers.Tests.Infrastructure;

namespace TCJ.Analyzers.Tests;

public sealed class AnalyzerLoadTests
{
    [Fact]
    public async Task Analyzer_assembly_loads_under_the_test_compiler_without_shipping_a_rule()
    {
        AnalyzerFileReference reference = new(
            typeof(TcjAnalyzerBootstrap).Assembly.Location,
            new TestAnalyzerAssemblyLoader());

        ImmutableArray<DiagnosticAnalyzer> analyzers = reference.GetAnalyzers(LanguageNames.CSharp);
        TcjAnalyzerBootstrap analyzer = Assert.IsType<TcjAnalyzerBootstrap>(Assert.Single(analyzers));

        Assert.Empty(analyzer.SupportedDiagnostics);

        ImmutableArray<Diagnostic> diagnostics = await AnalyzerTestHost.GetAnalyzerDiagnosticsAsync(
            "namespace Sample; public sealed class Example { }",
            analyzer);

        Assert.Empty(diagnostics);
    }

    private sealed class TestAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath)
            => Assembly.LoadFrom(fullPath);
    }
}
