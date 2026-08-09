using System.Text.Json;

namespace TCJ.Concurrency.Tests.Infrastructure;

internal sealed record StressSettings(
    int Workers,
    int Iterations,
    int Seed,
    int OperationTimeoutMilliseconds,
    int ScenarioTimeoutSeconds,
    string TraceDirectory,
    string FailureDirectory,
    string CommitSha)
{
    public static StressSettings Load(string group)
    {
        ConcurrencyPolicy policy = ConcurrencyPolicy.Load();
        bool scheduled = string.Equals(Environment.GetEnvironmentVariable("TCJ_STRESS_MODE"), "scheduled", StringComparison.OrdinalIgnoreCase);
        bool sqlServer = string.Equals(group, "sqlserver", StringComparison.OrdinalIgnoreCase);

        int defaultWorkers = sqlServer
            ? (scheduled ? policy.SqlServerScheduledWorkers : policy.SqlServerPullRequestWorkers)
            : (scheduled ? policy.ScheduledWorkers : policy.PullRequestWorkers);
        int defaultIterations = sqlServer
            ? (scheduled ? policy.SqlServerScheduledIterations : policy.SqlServerPullRequestIterations)
            : (scheduled ? policy.ScheduledIterations : policy.PullRequestIterations);
        int defaultScenarioSeconds = scheduled ? policy.MaximumScheduledScenarioSeconds : policy.MaximumScenarioSeconds;

        string root = Directory.GetCurrentDirectory();
        string traceDirectory = Environment.GetEnvironmentVariable("TCJ_CONCURRENCY_TRACE_DIR")
            ?? Path.Combine(root, "artifacts", "concurrency", "traces");
        string failureDirectory = Environment.GetEnvironmentVariable("TCJ_CONCURRENCY_FAILURE_DIR")
            ?? Path.Combine(root, "artifacts", "concurrency", "failures");

        return new StressSettings(
            ReadPositiveInt("TCJ_STRESS_WORKERS", defaultWorkers),
            ReadPositiveInt("TCJ_STRESS_ITERATIONS", defaultIterations),
            ReadInt("TCJ_STRESS_SEED", policy.PullRequestSeeds[0]),
            ReadPositiveInt("TCJ_STRESS_OPERATION_TIMEOUT_MS", policy.OperationTimeoutMilliseconds),
            ReadPositiveInt("TCJ_STRESS_SCENARIO_TIMEOUT_SECONDS", defaultScenarioSeconds),
            Path.GetFullPath(traceDirectory),
            Path.GetFullPath(failureDirectory),
            Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local");
    }

    private static int ReadPositiveInt(string name, int fallback)
    {
        int value = ReadInt(name, fallback);
        return value > 0 ? value : throw new InvalidOperationException($"{name} must be greater than zero.");
    }

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;
}

internal sealed class ConcurrencyPolicy
{
    public int PullRequestWorkers { get; init; }
    public int PullRequestIterations { get; init; }
    public int ScheduledWorkers { get; init; }
    public int ScheduledIterations { get; init; }
    public int SqlServerPullRequestWorkers { get; init; }
    public int SqlServerPullRequestIterations { get; init; }
    public int SqlServerScheduledWorkers { get; init; }
    public int SqlServerScheduledIterations { get; init; }
    public int OperationTimeoutMilliseconds { get; init; }
    public int MaximumScenarioSeconds { get; init; }
    public int MaximumScheduledScenarioSeconds { get; init; }
    public int[] PullRequestSeeds { get; init; } = [];
    public string SqlServerContainerImage { get; init; } = string.Empty;

    public static ConcurrencyPolicy Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "concurrency-policy.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "eng", "concurrency-policy.json");
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ConcurrencyPolicy>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Unable to deserialize concurrency policy.");
    }
}
