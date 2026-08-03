using BenchmarkDotNet.Attributes;
using TCJ.Core.Extensions;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "Extensions")]
public class StringExtensionBenchmarks
{
    private const string Value = "api/products/";

    [Benchmark(Baseline = true)]
    public string BclEnsureEndsWith()
        => Value.EndsWith('/') ? Value : string.Concat(Value, "/");

    [Benchmark]
    public string TcjEnsureEndsWith()
        => Value.EnsureEndsWith('/');
}
