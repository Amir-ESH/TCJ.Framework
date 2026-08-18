namespace TCJ.Analyzers.Tests.Infrastructure;

internal static class RepositoryLayout
{
    public static DirectoryInfo Root { get; } = FindRoot();

    public static string Combine(string repositoryRelativePath)
        => Path.Combine(
            Root.FullName,
            repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static DirectoryInfo FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TCJ.slnx"))
                && File.Exists(Path.Combine(current.FullName, "eng", "release-manifest.json")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root from '{AppContext.BaseDirectory}'.");
    }
}
