using BenchmarkDotNet.Attributes;
using TCJ.Core.Guards;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "Guards")]
public class GuardBenchmarks
{
    private const int OperationsPerInvoke = 64;
    private string _value = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        // Keep the benchmark input runtime-provided so the JIT cannot fold the
        // successful guard path down to an effectively empty constant expression.
        _value = $"benchmark-{Environment.ProcessId}-value";
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvoke)]
    public string BclNotNullOrWhiteSpace()
    {
        string value = _value;
        for (int index = 0; index < OperationsPerInvoke; index++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        }

        return value;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public string TcjNotNullOrWhiteSpace()
    {
        string value = _value;
        for (int index = 0; index < OperationsPerInvoke; index++)
        {
            value.NotNullOrWhiteSpace();
        }

        return value;
    }
}
