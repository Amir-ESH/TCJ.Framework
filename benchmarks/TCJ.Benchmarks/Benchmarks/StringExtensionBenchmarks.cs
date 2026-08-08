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
        // Benchmark the branch that actually has work to do. The previous
        // already-suffixed input was so cheap that BenchmarkDotNet could not
        // distinguish it from invocation overhead on hosted CI runners.
        _value = $"api/products/{Environment.ProcessId}";
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
