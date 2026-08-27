using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using TCJ.Analyzers.Tests.Infrastructure;

namespace TCJ.Analyzers.Tests;

public sealed class AnalyzerGovernanceTests
{
    private static readonly string[] ExpectedCategories =
    [
        "TCJ.DependencyInjection",
        "TCJ.Persistence",
        "TCJ.Specifications",
        "TCJ.AotTrimming",
        "TCJ.StrongTypes",
    ];

    private static readonly string[] RequiredDocumentationHeadings =
    [
        "## Cause",
        "## Rule description",
        "## How to fix",
        "## Examples",
        "## Suppression",
        "## Known limitations",
        "## Compatibility notes",
    ];

    [Fact]
    public void Roslyn_governance_files_are_build_enforced_for_analyzers_and_generators()
    {
        AssertGovernanceFiles("src/TCJ.Analyzers/TCJ.Analyzers.csproj");
        AssertGovernanceFiles("src/TCJ.Generators/TCJ.Generators.csproj");

        string editorConfig = File.ReadAllText(RepositoryLayout.Combine(".editorconfig"));
        Assert.Contains("[src/TCJ.Analyzers/**.cs]", editorConfig, StringComparison.Ordinal);
        Assert.Contains("[src/TCJ.Generators/**.cs]", editorConfig, StringComparison.Ordinal);
        Assert.Contains("dotnet_diagnostic.RS1018.severity = error", editorConfig, StringComparison.Ordinal);
        Assert.Contains("dotnet_diagnostic.RS1019.severity = error", editorConfig, StringComparison.Ordinal);
        Assert.Contains("dotnet_diagnostic.RS1020.severity = error", editorConfig, StringComparison.Ordinal);
        Assert.Contains("dotnet_diagnostic.RS1021.severity = error", editorConfig, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet_analyzer_diagnostic.category-MicrosoftCodeAnalysisReleaseTracking.severity = error",
            editorConfig,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_category_ranges_are_complete_non_overlapping_and_use_TCJ_namespace()
    {
        ImmutableArray<DiagnosticRange> ranges = ReadDiagnosticRanges();

        Assert.Equal(ExpectedCategories, ranges.Select(range => range.Category));

        foreach (DiagnosticRange range in ranges)
        {
            Assert.True(range.StartId.StartsWith("TCJ", StringComparison.Ordinal));
            Assert.True(range.EndId.StartsWith("TCJ", StringComparison.Ordinal));
            Assert.Equal(7, range.StartId.Length);
            Assert.Equal(7, range.EndId.Length);
            Assert.True(range.StartId[3..].All(char.IsDigit));
            Assert.True(range.EndId[3..].All(char.IsDigit));
            Assert.InRange(range.Start, 1, 9999);
            Assert.InRange(range.End, 1, 9999);
            Assert.True(range.Start <= range.End, $"Invalid diagnostic range {range.StartId}-{range.EndId}.");
        }

        for (int i = 0; i < ranges.Length; i++)
        {
            for (int j = i + 1; j < ranges.Length; j++)
            {
                Assert.False(
                    ranges[i].Start <= ranges[j].End && ranges[j].Start <= ranges[i].End,
                    $"Diagnostic ranges '{ranges[i].Category}' and '{ranges[j].Category}' overlap.");
            }
        }

        Assert.Equal(4999, ranges.Max(range => range.End));
    }

    [Fact]
    public void Registered_diagnostic_ids_are_unique_and_follow_allocated_ranges()
    {
        ImmutableArray<DiagnosticDescriptor> descriptors = GetRegisteredDescriptors();
        ImmutableArray<DiagnosticRange> ranges = ReadDiagnosticRanges();

        string[] duplicateIds = descriptors
            .GroupBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            duplicateIds.Length == 0,
            $"Duplicate TCJ diagnostic IDs: {string.Join(", ", duplicateIds)}");

        foreach (DiagnosticDescriptor descriptor in descriptors)
        {
            Assert.Equal(7, descriptor.Id.Length);
            Assert.True(descriptor.Id.StartsWith("TCJ", StringComparison.Ordinal));
            Assert.True(
                int.TryParse(descriptor.Id[3..], out int numericId),
                $"Diagnostic ID '{descriptor.Id}' must use the TCJxxxx numeric format.");

            DiagnosticRange range = Assert.Single(
                ranges,
                candidate => string.Equals(candidate.Category, descriptor.Category, StringComparison.Ordinal));

            Assert.InRange(numericId, range.Start, range.End);
        }
    }

    [Fact]
    public void Release_tracking_metadata_covers_every_registered_descriptor_and_never_reuses_new_rule_ids()
    {
        string shippedPath = RepositoryLayout.Combine("src/TCJ.Analyzers/AnalyzerReleases.Shipped.md");
        string unshippedPath = RepositoryLayout.Combine("src/TCJ.Analyzers/AnalyzerReleases.Unshipped.md");

        Assert.True(File.Exists(shippedPath), $"Missing release tracking file '{shippedPath}'.");
        Assert.True(File.Exists(unshippedPath), $"Missing release tracking file '{unshippedPath}'.");

        string generatorShippedPath = RepositoryLayout.Combine("src/TCJ.Generators/AnalyzerReleases.Shipped.md");
        string generatorUnshippedPath = RepositoryLayout.Combine("src/TCJ.Generators/AnalyzerReleases.Unshipped.md");

        Assert.True(File.Exists(generatorShippedPath), $"Missing generator release tracking file '{generatorShippedPath}'.");
        Assert.True(File.Exists(generatorUnshippedPath), $"Missing generator release tracking file '{generatorUnshippedPath}'.");

        ImmutableArray<ReleaseEntry> newRules = ReadNewRuleEntries(shippedPath)
            .AddRange(ReadNewRuleEntries(unshippedPath))
            .AddRange(ReadNewRuleEntries(generatorShippedPath))
            .AddRange(ReadNewRuleEntries(generatorUnshippedPath));

        string[] reusedIds = newRules
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            reusedIds.Length == 0,
            $"Diagnostic IDs may appear only once as a new rule: {string.Join(", ", reusedIds)}");

        HashSet<string> trackedIds = newRules
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (DiagnosticDescriptor descriptor in GetRegisteredDescriptors())
        {
            Assert.Contains(descriptor.Id, trackedIds);
        }
    }

    [Fact]
    public void Every_registered_descriptor_has_documentation_from_the_required_template()
    {
        string templatePath = RepositoryLayout.Combine("docs/analyzers/diagnostic-template.md");
        Assert.True(File.Exists(templatePath), $"Missing diagnostic documentation template '{templatePath}'.");

        string template = File.ReadAllText(templatePath);
        foreach (string heading in RequiredDocumentationHeadings)
        {
            Assert.Contains(heading, template, StringComparison.Ordinal);
        }

        foreach (DiagnosticDescriptor descriptor in GetRegisteredDescriptors())
        {
            string documentPath = RepositoryLayout.Combine($"docs/analyzers/{descriptor.Id}.md");
            Assert.True(
                File.Exists(documentPath),
                $"Diagnostic '{descriptor.Id}' must have documentation at '{documentPath}'.");

            string document = File.ReadAllText(documentPath);
            Assert.True(
                document.StartsWith($"# {descriptor.Id}:", StringComparison.Ordinal),
                $"Diagnostic documentation '{documentPath}' must start with '# {descriptor.Id}:'.");
            foreach (string heading in RequiredDocumentationHeadings)
            {
                Assert.Contains(heading, document, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Every_generator_release_entry_has_documentation_from_the_required_template()
    {
        string shippedPath = RepositoryLayout.Combine("src/TCJ.Generators/AnalyzerReleases.Shipped.md");
        string unshippedPath = RepositoryLayout.Combine("src/TCJ.Generators/AnalyzerReleases.Unshipped.md");
        ImmutableArray<ReleaseEntry> entries = ReadNewRuleEntries(shippedPath)
            .AddRange(ReadNewRuleEntries(unshippedPath));

        Assert.NotEmpty(entries);
        foreach (ReleaseEntry entry in entries)
        {
            string documentPath = RepositoryLayout.Combine($"docs/analyzers/{entry.Id}.md");
            Assert.True(
                File.Exists(documentPath),
                $"Generator diagnostic '{entry.Id}' must have documentation at '{documentPath}'.");

            string document = File.ReadAllText(documentPath);
            Assert.True(
                document.StartsWith($"# {entry.Id}:", StringComparison.Ordinal),
                $"Generator diagnostic documentation '{documentPath}' must start with '# {entry.Id}:'.");
            foreach (string heading in RequiredDocumentationHeadings)
            {
                Assert.Contains(heading, document, StringComparison.Ordinal);
            }
        }
    }

    private static void AssertGovernanceFiles(string projectRelativePath)
    {
        XDocument project = XDocument.Load(RepositoryLayout.Combine(projectRelativePath));

        string[] additionalFiles = project.Descendants()
            .Where(element => element.Name.LocalName == "AdditionalFiles")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Select(static value => Path.GetFileName(value.Replace('\\', '/')))
            .ToArray();

        Assert.Contains("DiagnosticCategoryAndIdRanges.txt", additionalFiles);
        Assert.Contains("AnalyzerReleases.Shipped.md", additionalFiles);
        Assert.Contains("AnalyzerReleases.Unshipped.md", additionalFiles);
    }

    private static ImmutableArray<DiagnosticDescriptor> GetRegisteredDescriptors()
    {
        ImmutableArray<DiagnosticDescriptor>.Builder descriptors = ImmutableArray.CreateBuilder<DiagnosticDescriptor>();

        Type[] analyzerTypes = typeof(TcjAnalyzerBootstrap).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        foreach (Type analyzerType in analyzerTypes)
        {
            DiagnosticAnalyzer analyzer = Assert.IsAssignableFrom<DiagnosticAnalyzer>(
                Activator.CreateInstance(analyzerType));
            descriptors.AddRange(analyzer.SupportedDiagnostics);
        }

        return descriptors.ToImmutable();
    }

    private static ImmutableArray<DiagnosticRange> ReadDiagnosticRanges()
    {
        string path = RepositoryLayout.Combine("src/TCJ.Analyzers/DiagnosticCategoryAndIdRanges.txt");
        Assert.True(File.Exists(path), $"Missing diagnostic range file '{path}'.");

        ImmutableArray<DiagnosticRange>.Builder ranges = ImmutableArray.CreateBuilder<DiagnosticRange>();

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] categoryParts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            Assert.Equal(2, categoryParts.Length);

            string[] rangeParts = categoryParts[1].Split('-', 2, StringSplitOptions.TrimEntries);
            Assert.Equal(2, rangeParts.Length);

            Assert.True(
                TryParseDiagnosticId(rangeParts[0], out int start),
                $"Invalid diagnostic range start '{rangeParts[0]}'.");
            Assert.True(
                TryParseDiagnosticId(rangeParts[1], out int end),
                $"Invalid diagnostic range end '{rangeParts[1]}'.");

            ranges.Add(new DiagnosticRange(
                categoryParts[0],
                rangeParts[0],
                rangeParts[1],
                start,
                end));
        }

        return ranges.ToImmutable();
    }

    private static ImmutableArray<ReleaseEntry> ReadNewRuleEntries(string path)
    {
        ImmutableArray<ReleaseEntry>.Builder entries = ImmutableArray.CreateBuilder<ReleaseEntry>();
        bool inNewRules = false;

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                inNewRules = string.Equals(line, "### New Rules", StringComparison.Ordinal);
                continue;
            }

            if (!inNewRules || line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith("Rule ID", StringComparison.Ordinal)
                || line.All(character => character is '-' or '|'))
            {
                continue;
            }

            if (!line.StartsWith("TCJ", StringComparison.Ordinal))
            {
                continue;
            }

            string[] columns = line.Split('|', StringSplitOptions.TrimEntries);
            Assert.Equal(4, columns.Length);
            Assert.True(TryParseDiagnosticId(columns[0], out _), $"Invalid release rule ID '{columns[0]}'.");
            Assert.Contains(columns[1], ExpectedCategories);
            Assert.Contains(columns[2], new[] { "Error", "Warning", "Info", "Hidden", "Disabled" });
            Assert.False(string.IsNullOrWhiteSpace(columns[3]), $"Release entry '{columns[0]}' must include notes.");

            entries.Add(new ReleaseEntry(columns[0], columns[1], columns[2]));
        }

        return entries.ToImmutable();
    }

    private static bool TryParseDiagnosticId(string id, out int numericId)
    {
        numericId = 0;
        return id.Length == 7
            && id.StartsWith("TCJ", StringComparison.Ordinal)
            && id[3..].All(char.IsDigit)
            && int.TryParse(id[3..], out numericId);
    }

    private sealed record DiagnosticRange(
        string Category,
        string StartId,
        string EndId,
        int Start,
        int End);

    private sealed record ReleaseEntry(string Id, string Category, string Severity);
}
