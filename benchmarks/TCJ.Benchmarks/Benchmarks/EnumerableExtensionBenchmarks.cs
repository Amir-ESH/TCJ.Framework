using BenchmarkDotNet.Attributes;
using TCJ.Core.Extensions;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "Extensions")]
public class EnumerableExtensionBenchmarks
{
    private readonly int[] _values = Enumerable.Range(1, 128).ToArray();
    private readonly Func<int, bool> _predicate = static value => value % 2 == 0;
    private readonly bool _condition = true;

    [Benchmark(Baseline = true)]
    public int BclConditionalWhere()
        => (_condition ? _values.Where(_predicate) : _values).Count();

    [Benchmark]
    public int TcjWhereIf()
        => _values.WhereIf(_condition, _predicate).Count();
}
