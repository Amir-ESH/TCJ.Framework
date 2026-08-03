using BenchmarkDotNet.Attributes;
using TCJ.Core.Extensions;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "Extensions")]
public class DecimalExtensionBenchmarks
{
    private decimal _value;
    private decimal _scale;

    [GlobalSetup]
    public void Setup()
    {
        _value = 1234.56781m;
        _scale = 10_000m;
    }

    [Benchmark(Baseline = true)]
    public decimal BclRoundUp()
        => Math.Ceiling(_value * _scale) / _scale;

    [Benchmark]
    public decimal TcjRoundUp()
        => _value.RoundUp();
}
