using BenchmarkDotNet.Attributes;
using TCJ.Core.Extensions;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "Extensions")]
public class StringExtensionBenchmarks
{
    private string _value = null!;
    private char _suffix;

    [GlobalSetup]
    public void Setup()
    {
        _value = "api/products/";
        _suffix = '/';
    }

    [Benchmark(Baseline = true)]
    public string BclEnsureEndsWith()
    {
        ArgumentNullException.ThrowIfNull(_value);
        return _value.EndsWith(_suffix)
            ? _value
            : string.Concat(_value, _suffix.ToString());
    }

    [Benchmark]
    public string TcjEnsureEndsWith()
        => _value.EnsureEndsWith(_suffix);
}
