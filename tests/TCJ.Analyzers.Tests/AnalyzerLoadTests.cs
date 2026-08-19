using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using TCJ.Analyzers.DependencyInjection;
using TCJ.Analyzers.Tests.Infrastructure;

namespace TCJ.Analyzers.Tests;

public sealed class AnalyzerLoadTests
{
    [Fact]
    public async Task Analyzer_assembly_loads_under_the_test_compiler_with_registered_rules()
    {
        AnalyzerFileReference reference = new(
            typeof(TcjAnalyzerBootstrap).Assembly.Location,
            new TestAnalyzerAssemblyLoader());

        ImmutableArray<DiagnosticAnalyzer> analyzers = reference.GetAnalyzers(LanguageNames.CSharp);

        TcjAnalyzerBootstrap bootstrap = Assert.Single(analyzers.OfType<TcjAnalyzerBootstrap>());
        ConflictingDependencyLifetimeMarkersAnalyzer conflictAnalyzer =
            Assert.Single(analyzers.OfType<ConflictingDependencyLifetimeMarkersAnalyzer>());
        InterfaceLifetimeMarkerWithoutServiceContractAnalyzer serviceContractAnalyzer =
            Assert.Single(analyzers.OfType<InterfaceLifetimeMarkerWithoutServiceContractAnalyzer>());

        Assert.Empty(bootstrap.SupportedDiagnostics);
        Assert.Equal("TCJ0001", Assert.Single(conflictAnalyzer.SupportedDiagnostics).Id);
        Assert.Equal("TCJ0002", Assert.Single(serviceContractAnalyzer.SupportedDiagnostics).Id);

        foreach (DiagnosticAnalyzer analyzer in analyzers)
        {
            ImmutableArray<Diagnostic> diagnostics = await AnalyzerTestHost.GetAnalyzerDiagnosticsAsync(
                "namespace Sample; public sealed class Example { }",
                analyzer);

            Assert.Empty(diagnostics);
        }
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
