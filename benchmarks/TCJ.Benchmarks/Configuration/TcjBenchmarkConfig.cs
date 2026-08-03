using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

namespace TCJ.Benchmarks.Configuration;

internal static class TcjBenchmarkConfig
{
    internal const string ReportsDirectory = "artifacts/performance/reports";

    internal static IConfig Create()
    {
        Job job = string.Equals(
            Environment.GetEnvironmentVariable("TCJ_BENCHMARK_MODE"),
            "short",
            StringComparison.OrdinalIgnoreCase)
            ? Job.ShortRun.WithId("Short")
            : Job.Default.WithId("Full");

        return ManualConfig
            .Create(DefaultConfig.Instance)
            .AddJob(job)
            .AddExporter(JsonExporter.Full)
            .AddExporter(MarkdownExporter.GitHub)
            .WithArtifactsPath(ReportsDirectory);
    }
}
