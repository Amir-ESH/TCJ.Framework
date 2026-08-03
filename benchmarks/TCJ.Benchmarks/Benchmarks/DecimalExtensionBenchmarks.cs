using BenchmarkDotNet.Attributes;
using TCJ.Core.Extensions;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "Extensions")]
public class DecimalExtensionBenchmarks
{
    private const decimal Value = 1234.56781m;
    private const decimal Scale = 10_000m;

    [Benchmark(Baseline = true)]
    public decimal BclRoundUp()
        => Math.Ceiling(Value * Scale) / Scale;

    [Benchmark]
    public decimal TcjRoundUp()
        => Value.RoundUp();
}
