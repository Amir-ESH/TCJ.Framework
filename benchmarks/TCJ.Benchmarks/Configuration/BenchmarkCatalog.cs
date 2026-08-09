using System.Text.Json;

namespace TCJ.Benchmarks.Configuration;

internal static class BenchmarkCatalog
{
    private static readonly BenchmarkDefinition[] Definitions =
    [
        Core("ResultBenchmarks", "CreateSuccessfulResult", baseline: true),
        Core("ResultBenchmarks", "CreateFailedResult"),
        Core("ResultBenchmarks", "ReadSuccessfulResultValue"),

        Core("GuidVersion7Benchmarks", "BclCreateVersion7ThroughTimeProvider", "GuidVersion7", baseline: true),
        Core("GuidVersion7Benchmarks", "TcjGuidGeneratorCreateVersion7", "GuidVersion7"),

        Core("GuardBenchmarks", "BclNotNullOrWhiteSpace", "GuardNotNullOrWhiteSpace", baseline: true),
        Core("GuardBenchmarks", "TcjNotNullOrWhiteSpace", "GuardNotNullOrWhiteSpace"),

        Core("StringExtensionBenchmarks", "BclEnsureEndsWith", "StringEnsureEndsWith", baseline: true),
        Core("StringExtensionBenchmarks", "TcjEnsureEndsWith", "StringEnsureEndsWith"),

        Core("EnumerableExtensionBenchmarks", "BclConditionalWhere", "EnumerableWhereIf", baseline: true),
        Core("EnumerableExtensionBenchmarks", "TcjWhereIf", "EnumerableWhereIf"),

        // RoundUp remains informational. The TCJ API performs decimal-place
        // validation and a scale lookup that the prepared BCL expression does
        // not, so their raw means are not a like-for-like regression ratio.
        Core("DecimalExtensionBenchmarks", "BclRoundUp", baseline: true),
        Core("DecimalExtensionBenchmarks", "TcjRoundUp"),

        DependencyInjection("DependencyDiscoveryBenchmarks", "DiscoverPublicConcreteTypes", baseline: true),
        DependencyInjection("DependencyDiscoveryBenchmarks", "ClassifyDependencyMarkers"),

        DependencyInjection("DependencyRegistrationBenchmarks", "RegisterTransientDependency", "DependencyRegistrationLifetime", baseline: true),
        DependencyInjection("DependencyRegistrationBenchmarks", "RegisterScopedDependency", "DependencyRegistrationLifetime"),
        DependencyInjection("DependencyRegistrationBenchmarks", "RegisterSingletonDependency", "DependencyRegistrationLifetime"),
        DependencyInjection("DependencyRegistrationBenchmarks", "RepeatedRegistrationWithDuplicateProtection"),

        Observability("ObservabilityBenchmarks", "TelemetryDisabled", baseline: true),
        Observability("ObservabilityBenchmarks", "TracingListenerEnabled"),
        Observability("ObservabilityBenchmarks", "MetricsListenerEnabled"),
        Observability("ObservabilityBenchmarks", "TracingAndMetricsEnabled"),

        Resilience("ResilienceBenchmarks", "NoPolicy", baseline: true),
        Resilience("ResilienceBenchmarks", "PolicyConfiguredNoFailure"),
        Resilience("ResilienceBenchmarks", "OneRetry"),
        Resilience("ResilienceBenchmarks", "RetryExhaustion"),
        Resilience("ResilienceBenchmarks", "TimeoutSetup"),
        Resilience("ResilienceBenchmarks", "CircuitBreakerClosed"),
        Resilience("ResilienceBenchmarks", "CircuitBreakerOpenFastFail")
    ];

    internal static void WriteManifest()
    {
        Directory.CreateDirectory(TcjBenchmarkConfig.ReportsDirectory);

        var manifest = new
        {
            schemaVersion = 1,
            generatedAtUtc = DateTimeOffset.UtcNow,
            benchmarks = Definitions
        };

        string path = Path.Combine(
            TcjBenchmarkConfig.ReportsDirectory,
            "benchmark-manifest.json");

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
    }

    private static BenchmarkDefinition Core(
        string type,
        string method,
        string? comparisonGroup = null,
        bool baseline = false)
        => new(type, method, ["TCJ.Core"], comparisonGroup, baseline);

    private static BenchmarkDefinition DependencyInjection(
        string type,
        string method,
        string? comparisonGroup = null,
        bool baseline = false)
        => new(type, method, ["TCJ.DependencyInjection"], comparisonGroup, baseline);

    private static BenchmarkDefinition Observability(
        string type,
        string method,
        bool baseline = false)
        => new(type, method, ["TCJ.Core", "TCJ.DependencyInjection", "Observability"], null, baseline);

    private static BenchmarkDefinition Resilience(
        string type,
        string method,
        bool baseline = false)
        => new(type, method, ["TCJ.Core", "Resilience"], null, baseline);

    private sealed record BenchmarkDefinition(
        string Type,
        string Method,
        string[] Categories,
        string? ComparisonGroup,
        bool Baseline);
}
