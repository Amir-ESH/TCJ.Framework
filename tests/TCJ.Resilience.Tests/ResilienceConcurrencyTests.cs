using System.Collections.Concurrent;
using TCJ.Core.Resilience;
using TCJ.Resilience.Tests.Infrastructure;

namespace TCJ.Resilience.Tests;

public sealed class ResilienceConcurrencyTests
{
    [Fact]
    [Trait("Category", "Concurrency")]
    [Trait("Category", "Retry")]
    public async Task Concurrency_retry_counters_are_operation_local_on_shared_policy()
    {
        var policy = new TcjRetryPolicy(
            new TransientFailureDetector([new InjectedTransientClassifier()]),
            new TcjRetryOptions
            {
                MaxRetryAttempts = 1,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                UseJitter = false
            });
        var attempts = new ConcurrentDictionary<int, int>();

        Task[] operations = Enumerable.Range(0, 64)
            .Select(operationId => policy.ExecuteAsync<int>(_ =>
            {
                int attempt = attempts.AddOrUpdate(operationId, 1, static (_, current) => current + 1);
                if (attempt == 1)
                {
                    throw new InjectedTransientException();
                }

                return Task.FromResult(operationId);
            }, "concurrent_retry"))
            .ToArray();

        await Task.WhenAll(operations);

        Assert.Equal(64, attempts.Count);
        Assert.All(attempts.Values, count => Assert.Equal(2, count));
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    [Trait("Category", "Cancellation")]
    public async Task Concurrency_canceling_one_operation_does_not_cancel_another()
    {
        var policy = new TcjRetryPolicy(
            new TransientFailureDetector([new InjectedTransientClassifier()]),
            new TcjRetryOptions { MaxRetryAttempts = 0 });
        using var canceledSource = new CancellationTokenSource();
        canceledSource.Cancel();

        Task canceled = policy.ExecuteAsync(_ => Task.CompletedTask, "isolated_cancel", canceledSource.Token);
        Task successful = policy.ExecuteAsync(_ => Task.CompletedTask, "isolated_cancel");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        await successful;
    }
}
