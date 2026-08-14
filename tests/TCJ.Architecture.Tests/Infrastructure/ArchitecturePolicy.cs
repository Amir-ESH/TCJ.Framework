using System.Text.Json;

namespace TCJ.Architecture.Tests.Infrastructure;

internal sealed class ArchitecturePolicy
{
    public const string RelativePath = "eng/architecture-policy.json";
    public const string DocumentationPath = "docs/architecture-tests.md";

    public required int SchemaVersion { get; init; }

    public required string Documentation { get; init; }

    public required Dictionary<string, string[]> Assemblies { get; init; }

    public required Dictionary<string, string> ProjectPaths { get; init; }

    public required Dictionary<string, string> NamespaceRoots { get; init; }

    public required Dictionary<string, string[]> ForbiddenDependencyPrefixes { get; init; }

    public required Dictionary<string, string[]> ForbiddenPublicApiTypePrefixes { get; init; }

    public required string[] ApprovedExtensionContainers { get; init; }

    public required string[] ApprovedPublicOptionTypes { get; init; }

    public static ArchitecturePolicy Load()
    {
        var path = RepositoryLayout.Root.Combine(RelativePath);
        var json = File.ReadAllText(path);
        var policy = JsonSerializer.Deserialize<ArchitecturePolicy>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        return policy
            ?? throw new InvalidOperationException($"Unable to deserialize {RelativePath}.");
    }
}
