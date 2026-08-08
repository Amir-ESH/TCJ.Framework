using System.Text.Json;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

internal sealed record SqlServerIntegrationPolicy(
    int SchemaVersion,
    string TestProject,
    string ContainerImage,
    int MinimumTestCount,
    int StartupTimeoutSeconds,
    int CommandTimeoutSeconds,
    bool CollectContainerLogsOnFailure,
    bool RequirePinnedImage,
    bool RequireDockerHealthCheck,
    bool AllowExternalDatabase,
    string DatabaseIsolation,
    string[] RequiredCategories)
{
    public static SqlServerIntegrationPolicy Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "sqlserver-integration-policy.json");
        string json = File.ReadAllText(path);

        return JsonSerializer.Deserialize<SqlServerIntegrationPolicy>(json, new JsonSerializerOptions
               {
                   PropertyNameCaseInsensitive = true
               })
            ?? throw new InvalidOperationException($"SQL Server integration policy could not be read from '{path}'.");
    }
}
