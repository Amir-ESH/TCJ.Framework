using BenchmarkDotNet.Attributes;
using TCJ.Core.Resilience;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "Resilience")]
public class ResilienceBenchmarks
{
    private readonly TcjRetryPolicy _successPolicy;
    private readonly TcjRetryPolicy _retryPolicy;
    private readonly TcjTimeoutPolicy _timeoutPolicy;
    private readonly TcjCircuitBreaker _closedCircuit;
    private readonly TcjCircuitBreaker _openCircuit;
    private int _retryAttempt;

    public ResilienceBenchmarks()
    {
        var detector = new TransientFailureDetector([new BenchmarkTransientClassifier()]);
        _successPolicy = new TcjRetryPolicy(detector, new TcjRetryOptions
        {
            MaxRetryAttempts = 0,
            BaseDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero,
            UseJitter = false
        });
        _retryPolicy = new TcjRetryPolicy(detector, new TcjRetryOptions
        {
            MaxRetryAttempts = 1,
            BaseDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero,
            UseJitter = false
        });
        _timeoutPolicy = new TcjTimeoutPolicy(new TcjTimeoutOptions
        {
            OperationTimeout = TimeSpan.FromSeconds(30)
        });
        _closedCircuit = new TcjCircuitBreaker(detector, new TcjCircuitBreakerOptions
        {
            FailureThreshold = 2,
            BreakDuration = TimeSpan.FromSeconds(30)
        });
        _openCircuit = new TcjCircuitBreaker(detector, new TcjCircuitBreakerOptions
        {
            FailureThreshold = 1,
            BreakDuration = TimeSpan.FromMinutes(1)
        });
        try
        {
            _openCircuit.ExecuteAsync<int>(_ => throw new BenchmarkTransientException()).GetAwaiter().GetResult();
        }
        catch (BenchmarkTransientException)
        {
        }
    }

    [Benchmark(Baseline = true)]
    public Task<int> NoPolicy() => Task.FromResult(42);

    [Benchmark]
    public Task<int> PolicyConfiguredNoFailure() =>
        _successPolicy.ExecuteAsync(_ => Task.FromResult(42));

    [Benchmark]
    public async Task<int> OneRetry()
    {
        _retryAttempt = 0;
        return await _retryPolicy.ExecuteAsync(_ =>
        {
            if (Interlocked.Increment(ref _retryAttempt) == 1)
            {
                throw new BenchmarkTransientException();
            }

            return Task.FromResult(42);
        }).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task<int> RetryExhaustion()
    {
        try
        {
            return await _retryPolicy.ExecuteAsync<int>(_ => throw new BenchmarkTransientException())
                .ConfigureAwait(false);
        }
        catch (BenchmarkTransientException)
        {
            return -1;
        }
    }

    [Benchmark]
    public Task<int> TimeoutSetup() =>
        _timeoutPolicy.ExecuteAsync(_ => Task.FromResult(42));

    [Benchmark]
    public Task<int> CircuitBreakerClosed() =>
        _closedCircuit.ExecuteAsync(_ => Task.FromResult(42));

    [Benchmark]
    public async Task<int> CircuitBreakerOpenFastFail()
    {
        try
        {
            return await _openCircuit.ExecuteAsync(_ => Task.FromResult(42)).ConfigureAwait(false);
        }
        catch (TcjCircuitOpenException)
        {
            return -1;
        }
    }

    private sealed class BenchmarkTransientException : Exception { }

    private sealed class BenchmarkTransientClassifier : ITransientFailureClassifier
    {
        public bool IsTransient(Exception exception) => exception is BenchmarkTransientException;
    }
}
