using BenchmarkDotNet.Attributes;
using TCJ.Core.Guards;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "Guards")]
public class GuardBenchmarks
{
    private const string Value = "benchmark-value";

    [Benchmark(Baseline = true)]
    public string BclNotNullOrWhiteSpace()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Value);
        return Value;
    }

    [Benchmark]
    public string TcjNotNullOrWhiteSpace()
        => Value.NotNullOrWhiteSpace();
}
