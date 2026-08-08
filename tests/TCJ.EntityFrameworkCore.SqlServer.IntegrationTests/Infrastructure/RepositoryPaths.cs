namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

internal static class RepositoryPaths
{
    public static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TCJ.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        string current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "TCJ.slnx")))
        {
            return current;
        }

        throw new InvalidOperationException("The TCJ repository root could not be located.");
    }
}
