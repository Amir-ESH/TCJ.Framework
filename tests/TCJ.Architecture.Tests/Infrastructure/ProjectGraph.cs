using System.Xml.Linq;

namespace TCJ.Architecture.Tests.Infrastructure;

internal sealed record ProjectDependency(
    string SourceAssembly,
    string Reference,
    string Kind,
    string ProjectPath);

internal static class ProjectGraph
{
    public static IReadOnlyCollection<ProjectDependency> ReadDependencies(ArchitecturePolicy policy)
    {
        var projectToAssembly = policy.ProjectPaths.ToDictionary(
            pair => Normalize(Path.GetFullPath(RepositoryLayout.Root.Combine(pair.Value))),
            pair => pair.Key,
            StringComparer.OrdinalIgnoreCase);

        var dependencies = new List<ProjectDependency>();

        foreach (var (assemblyName, relativeProjectPath) in policy.ProjectPaths.OrderBy(pair => pair.Key))
        {
            var projectPath = RepositoryLayout.Root.Combine(relativeProjectPath);
            var document = XDocument.Load(projectPath, LoadOptions.SetLineInfo);
            var projectDirectory = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Project path has no directory: {projectPath}");

            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                var normalizedInclude = include.Replace(
                    '\\',
                    Path.DirectorySeparatorChar);
                var fullReference = Normalize(Path.GetFullPath(Path.Combine(projectDirectory, normalizedInclude)));
                var referenceAssemblyName = Path.GetFileNameWithoutExtension(normalizedInclude);
                var displayReference = projectToAssembly.TryGetValue(fullReference, out var targetAssembly)
                    ? targetAssembly
                    : referenceAssemblyName.StartsWith("TCJ.", StringComparison.Ordinal)
                        ? referenceAssemblyName
                        : include.Replace('\\', '/');

                dependencies.Add(new ProjectDependency(
                    assemblyName,
                    displayReference,
                    "ProjectReference",
                    relativeProjectPath));
            }

            foreach (var reference in document.Descendants("PackageReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                {
                    dependencies.Add(new ProjectDependency(
                        assemblyName,
                        include,
                        "PackageReference",
                        relativeProjectPath));
                }
            }

            foreach (var reference in document.Descendants("FrameworkReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                {
                    dependencies.Add(new ProjectDependency(
                        assemblyName,
                        include,
                        "FrameworkReference",
                        relativeProjectPath));
                }
            }
        }

        return dependencies;
    }

    public static IReadOnlyCollection<string> FindCycles(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> graph)
    {
        var cycles = new SortedSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var node in graph.Keys.Order(StringComparer.Ordinal))
        {
            Visit(node);
        }

        return cycles;

        void Visit(string node)
        {
            if (visited.Contains(node))
            {
                return;
            }

            if (!visiting.Add(node))
            {
                var index = path.IndexOf(node);
                if (index >= 0)
                {
                    cycles.Add(string.Join(" -> ", path.Skip(index).Append(node)));
                }

                return;
            }

            path.Add(node);
            if (graph.TryGetValue(node, out var dependencies))
            {
                foreach (var dependency in dependencies.Order(StringComparer.Ordinal))
                {
                    Visit(dependency);
                }
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(node);
            visited.Add(node);
        }
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
