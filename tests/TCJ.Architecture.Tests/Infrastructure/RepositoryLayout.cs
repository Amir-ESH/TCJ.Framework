namespace TCJ.Architecture.Tests.Infrastructure;

internal static class RepositoryLayout
{
    public static DirectoryInfo Root { get; } = FindRoot();

    public static string Combine(this DirectoryInfo root, string repositoryRelativePath)
        => Path.Combine(
            root.FullName,
            repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static DirectoryInfo FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, ArchitecturePolicy.RelativePath))
                && File.Exists(Path.Combine(current.FullName, "TCJ.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root from '{AppContext.BaseDirectory}'. " +
            $"Expected to find {ArchitecturePolicy.RelativePath} and TCJ.slnx.");
    }
}
