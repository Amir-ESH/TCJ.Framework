using BenchmarkDotNet.Attributes;
using TCJ.Core.Identifiers;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "Identifiers")]
public class GuidVersion7Benchmarks
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 3, 8, 0, 0, TimeSpan.Zero);

    private readonly TimeProvider _timeProvider = new FixedTimeProvider(Timestamp);
    private GuidGenerator _generator = null!;

    [GlobalSetup]
    public void Setup() => _generator = new GuidGenerator(_timeProvider);

    [Benchmark(Baseline = true)]
    public Guid BclCreateVersion7ThroughTimeProvider()
        => Guid.CreateVersion7(_timeProvider.GetUtcNow());

    [Benchmark]
    public Guid TcjGuidGeneratorCreateVersion7()
        => _generator.CreateVersion7();

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
