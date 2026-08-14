using Microsoft.Extensions.Time.Testing;
using TCJ.Core.Resilience;
using TCJ.Resilience.Tests.Infrastructure;

namespace TCJ.Resilience.Tests;

public sealed class CircuitBreakerTests
{
    private static TcjCircuitBreaker CreateBreaker(FakeTimeProvider time, int threshold = 2) =>
        new(
            new TransientFailureDetector([new InjectedTransientClassifier()]),
            new TcjCircuitBreakerOptions
            {
                FailureThreshold = threshold,
                BreakDuration = TimeSpan.FromSeconds(10)
            },
            time);

    [Fact]
    [Trait("Category", "CircuitBreaker")]
    public async Task CircuitBreaker_opens_after_bounded_transient_failures_and_fails_fast()
    {
        var time = new FakeTimeProvider();
        TcjCircuitBreaker breaker = CreateBreaker(time);
        int underlyingCalls = 0;

        for (int index = 0; index < 2; index++)
        {
            await Assert.ThrowsAsync<InjectedTransientException>(() =>
                breaker.ExecuteAsync<int>(_ =>
                {
                    underlyingCalls++;
                    throw new InjectedTransientException();
                }, "test_circuit"));
        }

        Assert.Equal(TcjCircuitState.Open, breaker.State);
        await Assert.ThrowsAsync<TcjCircuitOpenException>(() =>
            breaker.ExecuteAsync(_ =>
            {
                underlyingCalls++;
                return Task.CompletedTask;
            }, "test_circuit"));
        Assert.Equal(2, underlyingCalls);
        ResilienceTrace.Write(nameof(CircuitBreaker_opens_after_bounded_transient_failures_and_fails_fast), new { underlyingCalls, state = breaker.State.ToString() });
    }

    [Fact]
    [Trait("Category", "CircuitBreaker")]
    public async Task CircuitBreaker_half_open_success_closes_after_break_duration()
    {
        var time = new FakeTimeProvider();
        TcjCircuitBreaker breaker = CreateBreaker(time, threshold: 1);
        await Assert.ThrowsAsync<InjectedTransientException>(() =>
            breaker.ExecuteAsync<int>(_ => throw new InjectedTransientException(), "test_recovery"));

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TcjCircuitState.HalfOpen, breaker.State);
        await breaker.ExecuteAsync(_ => Task.CompletedTask, "test_recovery");

        Assert.Equal(TcjCircuitState.Closed, breaker.State);
    }

    [Fact]
    [Trait("Category", "CircuitBreaker")]
    public async Task CircuitBreaker_half_open_transient_failure_reopens()
    {
        var time = new FakeTimeProvider();
        TcjCircuitBreaker breaker = CreateBreaker(time, threshold: 1);
        await Assert.ThrowsAsync<InjectedTransientException>(() =>
            breaker.ExecuteAsync<int>(_ => throw new InjectedTransientException(), "test_reopen"));
        time.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InjectedTransientException>(() =>
            breaker.ExecuteAsync<int>(_ => throw new InjectedTransientException(), "test_reopen"));

        Assert.Equal(TcjCircuitState.Open, breaker.State);
    }

    [Fact]
    [Trait("Category", "CircuitBreaker")]
    [Trait("Category", "Concurrency")]
    public async Task CircuitBreaker_concurrent_half_open_allows_only_one_probe()
    {
        var time = new FakeTimeProvider();
        TcjCircuitBreaker breaker = CreateBreaker(time, threshold: 1);
        await Assert.ThrowsAsync<InjectedTransientException>(() =>
            breaker.ExecuteAsync<int>(_ => throw new InjectedTransientException(), "test_half_open_concurrency"));
        time.Advance(TimeSpan.FromSeconds(10));

        var probeEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = breaker.ExecuteAsync(async _ =>
        {
            probeEntered.SetResult(true);
            await releaseProbe.Task;
        }, "test_half_open_concurrency");
        await probeEntered.Task;

        await Assert.ThrowsAsync<TcjCircuitOpenException>(() =>
            breaker.ExecuteAsync(_ => Task.CompletedTask, "test_half_open_concurrency"));
        releaseProbe.SetResult(true);
        await first;

        Assert.Equal(TcjCircuitState.Closed, breaker.State);
    }

    [Fact]
    [Trait("Category", "CircuitBreaker")]
    [Trait("Category", "Cancellation")]
    public async Task CircuitBreaker_half_open_caller_cancellation_releases_probe_without_reopening()
    {
        var time = new FakeTimeProvider();
        TcjCircuitBreaker breaker = CreateBreaker(time, threshold: 1);
        await Assert.ThrowsAsync<InjectedTransientException>(() =>
            breaker.ExecuteAsync<int>(_ => throw new InjectedTransientException(), "test_half_open_cancel"));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TcjCircuitState.HalfOpen, breaker.State);

        using var source = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => breaker.ExecuteAsync(
            token =>
            {
                source.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            "test_half_open_cancel",
            source.Token));

        Assert.Equal(TcjCircuitState.HalfOpen, breaker.State);
        await breaker.ExecuteAsync(_ => Task.CompletedTask, "test_half_open_cancel");
        Assert.Equal(TcjCircuitState.Closed, breaker.State);
    }

    [Fact]
    [Trait("Category", "CircuitBreaker")]
    public async Task CircuitBreaker_permanent_failure_does_not_open_circuit()
    {
        var time = new FakeTimeProvider();
        TcjCircuitBreaker breaker = CreateBreaker(time, threshold: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            breaker.ExecuteAsync<int>(_ => throw new InvalidOperationException("permanent"), "test_permanent_circuit"));

        Assert.Equal(TcjCircuitState.Closed, breaker.State);
    }
}
