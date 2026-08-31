using System.Text.Json;
using System.Xml.Linq;
using TCJ.Analyzers.Tests.Infrastructure;

namespace TCJ.Analyzers.Tests;

public sealed class AnalyzerBoundaryTests
{
    [Fact]
    public void Runtime_TCJ_packages_do_not_reference_analyzer_projects_or_packages()
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(RepositoryLayout.Combine("eng/release-manifest.json")));

        string[] runtimePackages = manifest.RootElement
            .GetProperty("releasePackages")
            .GetProperty("runtime")
            .EnumerateArray()
            .Select(element => element.GetProperty("id").GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        Assert.NotEmpty(runtimePackages);

        foreach (string packageId in runtimePackages)
        {
            string projectPath = RepositoryLayout.Combine($"src/{packageId}/{packageId}.csproj");
            XDocument project = XDocument.Load(projectPath);

            string[] references = project.Descendants()
                .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();

            Assert.DoesNotContain(
                references,
                reference => reference.Contains("TCJ.Analyzers", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Netstandard_analyzer_projects_pin_CSharp_10_for_shared_compiler_settings()
    {
        string[] analyzerProjects =
        [
            "src/TCJ.Analyzers/TCJ.Analyzers.csproj",
            "src/TCJ.Analyzers.CodeFixes/TCJ.Analyzers.CodeFixes.csproj",
            "eng/packaging/TCJ.Analyzers/TCJ.Analyzers.Package.csproj",
        ];

        foreach (string relativePath in analyzerProjects)
        {
            XDocument project = XDocument.Load(RepositoryLayout.Combine(relativePath));

            string? targetFramework = project.Descendants()
                .Single(element => element.Name.LocalName == "TargetFramework")
                .Value;
            string? languageVersion = project.Descendants()
                .Single(element => element.Name.LocalName == "LangVersion")
                .Value;

            Assert.Equal("netstandard2.0", targetFramework);
            Assert.Equal("10.0", languageVersion);
        }
    }

    [Fact]
    public void Analyzer_packaging_project_does_not_generate_an_empty_symbol_package()
    {
        XDocument project = XDocument.Load(
            RepositoryLayout.Combine("eng/packaging/TCJ.Analyzers/TCJ.Analyzers.Package.csproj"));

        string includeBuildOutput = project.Descendants()
            .Single(element => element.Name.LocalName == "IncludeBuildOutput")
            .Value;
        string includeSymbols = project.Descendants()
            .Single(element => element.Name.LocalName == "IncludeSymbols")
            .Value;
        string includeSource = project.Descendants()
            .Single(element => element.Name.LocalName == "IncludeSource")
            .Value;

        Assert.Equal("false", includeBuildOutput);
        Assert.Equal("false", includeSymbols);
        Assert.Equal("false", includeSource);
    }

    [Fact]
    public void Analyzer_implementation_projects_do_not_reference_runtime_TCJ_projects()
    {
        string[] analyzerProjects =
        [
            "src/TCJ.Analyzers/TCJ.Analyzers.csproj",
            "src/TCJ.Analyzers.CodeFixes/TCJ.Analyzers.CodeFixes.csproj",
        ];

        foreach (string relativePath in analyzerProjects)
        {
            XDocument project = XDocument.Load(RepositoryLayout.Combine(relativePath));

            string[] references = project.Descendants()
                .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();

            Assert.DoesNotContain(
                references,
                reference => reference.StartsWith("TCJ.Core", StringComparison.OrdinalIgnoreCase)
                    || reference.StartsWith("TCJ.DependencyInjection", StringComparison.OrdinalIgnoreCase)
                    || reference.StartsWith("TCJ.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                    || reference.StartsWith("TCJ.AspNetCore", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("\\TCJ.Core\\", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("/TCJ.Core/", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("\\TCJ.DependencyInjection\\", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("/TCJ.DependencyInjection/", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("\\TCJ.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("/TCJ.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("\\TCJ.AspNetCore\\", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("/TCJ.AspNetCore/", StringComparison.OrdinalIgnoreCase));
        }
    }
}
