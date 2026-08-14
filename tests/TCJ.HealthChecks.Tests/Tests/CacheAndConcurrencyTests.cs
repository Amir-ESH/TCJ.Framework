using Microsoft.Extensions.Time.Testing;
using TCJ.Core.HealthChecks;

namespace TCJ.HealthChecks.Tests.Tests;

[Trait("Category", "Integration")]
[Trait("Category", "HealthChecks")]
[Trait("Category", "Concurrency")]
public sealed class CacheAndConcurrencyTests
{
    [Fact]
    public async Task Cache_hit_executes_expensive_operation_once()
    {
        var time = new FakeTimeProvider();
        var cache = new AsyncHealthCheckCache<int>(time, TimeSpan.FromSeconds(10));
        int executions = 0;
        Task<int> Factory(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref executions));
        Assert.Equal(1, await cache.GetOrCreateAsync(Factory, CancellationToken.None));
        Assert.Equal(1, await cache.GetOrCreateAsync(Factory, CancellationToken.None));
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Cache_expiration_observes_new_dependency_state()
    {
        var time = new FakeTimeProvider();
        var cache = new AsyncHealthCheckCache<int>(time, TimeSpan.FromSeconds(5));
        int state = 1;
        Assert.Equal(1, await cache.GetOrCreateAsync(_ => Task.FromResult(state), CancellationToken.None));
        state = 2;
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(2, await cache.GetOrCreateAsync(_ => Task.FromResult(state), CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_requests_share_single_flight_execution()
    {
        var cache = new AsyncHealthCheckCache<int>(TimeProvider.System, TimeSpan.FromSeconds(5));
        int executions = 0;
        async Task<int> Factory(CancellationToken token)
        {
            Interlocked.Increment(ref executions);
            await Task.Delay(40, token);
            return 42;
        }
        int[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => cache.GetOrCreateAsync(Factory, CancellationToken.None)));
        Assert.All(results, value => Assert.Equal(42, value));
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_corrupt_cache()
    {
        var cache = new AsyncHealthCheckCache<int>(TimeProvider.System, TimeSpan.FromSeconds(5));
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync(WaitUntilCanceledAsync, canceled.Token));
        Assert.Equal(7, await cache.GetOrCreateAsync(_ => Task.FromResult(7), CancellationToken.None));
    }

    [Fact]
    public async Task Independent_named_check_caches_do_not_share_state()
    {
        var first = new AsyncHealthCheckCache<int>(TimeProvider.System, TimeSpan.FromSeconds(10));
        var second = new AsyncHealthCheckCache<int>(TimeProvider.System, TimeSpan.FromSeconds(10));
        Assert.Equal(1, await first.GetOrCreateAsync(_ => Task.FromResult(1), CancellationToken.None));
        Assert.Equal(2, await second.GetOrCreateAsync(_ => Task.FromResult(2), CancellationToken.None));
    }

    [Fact]
    public async Task Zero_cache_duration_executes_each_probe()
    {
        var cache = new AsyncHealthCheckCache<int>(TimeProvider.System, TimeSpan.Zero);
        int calls = 0;
        await cache.GetOrCreateAsync(_ => Task.FromResult(++calls), CancellationToken.None);
        await cache.GetOrCreateAsync(_ => Task.FromResult(++calls), CancellationToken.None);
        Assert.Equal(2, calls);
    }
    private static async Task<int> WaitUntilCanceledAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

}
