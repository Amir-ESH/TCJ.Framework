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

        Core("DecimalExtensionBenchmarks", "BclRoundUp", "DecimalRoundUp", baseline: true),
        Core("DecimalExtensionBenchmarks", "TcjRoundUp", "DecimalRoundUp"),

        DependencyInjection("DependencyDiscoveryBenchmarks", "DiscoverPublicConcreteTypes", baseline: true),
        DependencyInjection("DependencyDiscoveryBenchmarks", "ClassifyDependencyMarkers"),

        DependencyInjection("DependencyRegistrationBenchmarks", "RegisterTransientDependency", "DependencyRegistrationLifetime", baseline: true),
        DependencyInjection("DependencyRegistrationBenchmarks", "RegisterScopedDependency", "DependencyRegistrationLifetime"),
        DependencyInjection("DependencyRegistrationBenchmarks", "RegisterSingletonDependency", "DependencyRegistrationLifetime"),
        DependencyInjection("DependencyRegistrationBenchmarks", "RepeatedRegistrationWithDuplicateProtection")
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

    private sealed record BenchmarkDefinition(
        string Type,
        string Method,
        string[] Categories,
        string? ComparisonGroup,
        bool Baseline);
}
