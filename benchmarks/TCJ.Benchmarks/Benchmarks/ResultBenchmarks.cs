using BenchmarkDotNet.Attributes;
using TCJ.Core.Results;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "Results")]
public class ResultBenchmarks
{
    private ResultError _error = null!;
    private Result<int> _successfulResult = null!;

    [GlobalSetup]
    public void Setup()
    {
        _error = CommonErrors.Validation("The supplied value is invalid.");
        _successfulResult = Result.Success(42);
    }

    [Benchmark(Baseline = true)]
    public Result CreateSuccessfulResult() => Result.Success();

    [Benchmark]
    public Result CreateFailedResult() => Result.Failure(_error);

    [Benchmark]
    public int ReadSuccessfulResultValue() => _successfulResult.Value;
}
