using System.Reflection;
using TCJ.Architecture.Tests.Infrastructure;

namespace TCJ.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class AssemblyDependencyArchitectureTests
{
    private static readonly ArchitecturePolicy Policy = ProductionAssemblies.CurrentPolicy;

    [Fact]
    public void Compiled_TCJ_dependencies_follow_the_approved_direction()
    {
        var knownAssemblies = Policy.Assemblies.Keys.ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var (assemblyName, assembly) in ProductionAssemblies.All.OrderBy(pair => pair.Key))
        {
            var allowed = Policy.Assemblies[assemblyName].ToHashSet(StringComparer.Ordinal);
            var actual = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => name is not null && knownAssemblies.Contains(name))
                .Cast<string>()
                .Order(StringComparer.Ordinal);

            foreach (var dependency in actual.Where(dependency => !allowed.Contains(dependency)))
            {
                violations.Add(
                    $"Assembly '{assemblyName}' references forbidden TCJ assembly '{dependency}'. " +
                    $"Allowed TCJ dependencies: {FormatAllowed(allowed)}.");
            }
        }

        Assert.True(
            violations.Count == 0,
            ArchitectureFailure.Format("compiled TCJ assembly dependency directions", violations));
    }

    [Fact]
    public void Compiled_assemblies_do_not_reference_forbidden_infrastructure()
    {
        var violations = new List<string>();

        foreach (var (assemblyName, assembly) in ProductionAssemblies.All.OrderBy(pair => pair.Key))
        {
            var forbiddenPrefixes = Policy.ForbiddenDependencyPrefixes[assemblyName];
            var references = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>();

            foreach (var reference in references)
            {
                var prefix = forbiddenPrefixes.FirstOrDefault(
                    candidate => reference.StartsWith(candidate, StringComparison.Ordinal));
                if (prefix is not null)
                {
                    violations.Add(
                        $"Assembly '{assemblyName}' references '{reference}', which matches forbidden prefix '{prefix}'.");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            ArchitectureFailure.Format("forbidden compiled infrastructure references", violations));
    }

    [Fact]
    public void Project_and_package_references_follow_the_policy()
    {
        var knownAssemblies = Policy.Assemblies.Keys.ToHashSet(StringComparer.Ordinal);
        var dependencies = ProjectGraph.ReadDependencies(Policy);
        var violations = new List<string>();

        foreach (var dependency in dependencies)
        {
            if (dependency.Kind == "ProjectReference")
            {
                if (dependency.Reference.StartsWith("TCJ.", StringComparison.Ordinal)
                    && !knownAssemblies.Contains(dependency.Reference))
                {
                    violations.Add(
                        $"Project '{dependency.ProjectPath}' references unknown TCJ project '{dependency.Reference}'.");
                    continue;
                }

                if (knownAssemblies.Contains(dependency.Reference)
                    && !Policy.Assemblies[dependency.SourceAssembly].Contains(
                        dependency.Reference,
                        StringComparer.Ordinal))
                {
                    violations.Add(
                        $"Project '{dependency.ProjectPath}' ({dependency.SourceAssembly}) references forbidden " +
                        $"TCJ project '{dependency.Reference}'. Allowed TCJ dependencies: " +
                        $"{FormatAllowed(Policy.Assemblies[dependency.SourceAssembly])}.");
                }

                continue;
            }

            var forbiddenPrefix = Policy.ForbiddenDependencyPrefixes[dependency.SourceAssembly]
                .FirstOrDefault(prefix => dependency.Reference.StartsWith(prefix, StringComparison.Ordinal));
            if (forbiddenPrefix is not null)
            {
                violations.Add(
                    $"Project '{dependency.ProjectPath}' ({dependency.SourceAssembly}) has {dependency.Kind} " +
                    $"'{dependency.Reference}', which matches forbidden prefix '{forbiddenPrefix}'.");
            }
        }

        Assert.True(
            violations.Count == 0,
            ArchitectureFailure.Format("project, package, and framework references", violations));
    }

    [Fact]
    public void Actual_project_dependency_graph_is_acyclic()
    {
        var knownAssemblies = Policy.Assemblies.Keys.ToHashSet(StringComparer.Ordinal);
        var graph = ProjectGraph.ReadDependencies(Policy)
            .Where(dependency => dependency.Kind == "ProjectReference"
                && knownAssemblies.Contains(dependency.Reference))
            .GroupBy(dependency => dependency.SourceAssembly, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<string>)group
                    .Select(dependency => dependency.Reference)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        foreach (var assembly in knownAssemblies)
        {
            graph.TryAdd(assembly, []);
        }

        var cycles = ProjectGraph.FindCycles(graph);

        Assert.True(
            cycles.Count == 0,
            ArchitectureFailure.Format(
                "production project dependency graph must be acyclic",
                cycles.Select(cycle => $"Dependency cycle detected: {cycle}.")));
    }

    private static string FormatAllowed(IEnumerable<string> allowed)
    {
        var values = allowed.Order(StringComparer.Ordinal).ToArray();
        return values.Length == 0 ? "none" : string.Join(", ", values);
    }
}
