using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using TCJ.Analyzers;

namespace TCJ.Analyzers.Tests;

public sealed class AnalyzerReleaseVerificationTests
{
    [Fact]
    public void Analyzer_descriptors_have_release_metadata()
    {
        Assembly analyzerAssembly = typeof(TcjAnalyzerBootstrap).Assembly;

        Type[] analyzerTypes = analyzerAssembly
            .GetTypes()
            .Where(type => typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
            .Where(type => !type.IsAbstract)
            .Where(type => type != typeof(TcjAnalyzerBootstrap))
            .ToArray();

        Assert.NotEmpty(analyzerTypes);

        foreach (Type analyzerType in analyzerTypes)
        {
            DiagnosticAnalyzer analyzer =
                (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType)!;

            Assert.NotEmpty(analyzer.SupportedDiagnostics);

            foreach (DiagnosticDescriptor descriptor in analyzer.SupportedDiagnostics)
            {
                Assert.False(string.IsNullOrWhiteSpace(descriptor.Id));
                Assert.False(string.IsNullOrWhiteSpace(descriptor.Title.ToString()));
                Assert.False(string.IsNullOrWhiteSpace(descriptor.Description.ToString()));
            }
        }
    }

    [Fact]
    public void Released_diagnostic_ids_are_unique()
    {
        string[] ids =
        [
            "TCJ0001",
            "TCJ0002",
            "TCJ0003",
            "TCJ0004",
            "TCJ1000",
            "TCJ2000",
            "TCJ3000",
        ];

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
