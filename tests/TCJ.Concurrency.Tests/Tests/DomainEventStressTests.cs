using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Concurrency.Tests.Fixtures;
using TCJ.Concurrency.Tests.Infrastructure;
using TCJ.Core.DomainEvents;
using TCJ.DependencyInjection.Extensions;

namespace TCJ.Concurrency.Tests.Tests;

[Trait("Category", "Concurrency")]
[Trait("Category", "Stress")]
[Trait("Category", "DomainEvents")]
public sealed class DomainEventStressTests
{
    [Fact]
    public Task DomainEventsDispatchExactlyOncePerOperation()
    {
        using ServiceProvider provider = BuildProvider();
        return StressRunner.RunAsync(nameof(DomainEventsDispatchExactlyOncePerOperation), "core", async context =>
        {
            using IServiceScope scope = provider.CreateScope();
            IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
            IOperationRecorder recorder = scope.ServiceProvider.GetRequiredService<IOperationRecorder>();
            await dispatcher.DispatchAsync([new StressDomainEvent(context.OperationId, DateTimeOffset.UnixEpoch)], context.CancellationToken);
            Assert.Equal([context.OperationId], recorder.Records);
        });
    }

    [Fact]
    [Trait("Category", "RequestScope")]
    public Task DomainEventsFromIndependentScopesDoNotMix()
    {
        using ServiceProvider provider = BuildProvider();
        return StressRunner.RunAsync(nameof(DomainEventsFromIndependentScopesDoNotMix), "core", async context =>
        {
            using IServiceScope scope = provider.CreateScope();
            IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
            IOperationRecorder recorder = scope.ServiceProvider.GetRequiredService<IOperationRecorder>();
            string eventId = $"event-{context.OperationId}";
            await dispatcher.DispatchAsync([new StressDomainEvent(eventId, DateTimeOffset.UnixEpoch)], context.CancellationToken);
            Assert.Single(recorder.Records);
            Assert.Equal(eventId, recorder.Records.Single());
        });
    }

    [Fact]
    [Trait("Category", "Cancellation")]
    public Task CancellationStopsOnlyTargetDomainEventOperation()
    {
        using ServiceProvider provider = BuildProvider();
        var successful = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        return StressRunner.RunAsync(nameof(CancellationStopsOnlyTargetDomainEventOperation), "core", async context =>
        {
            using IServiceScope scope = provider.CreateScope();
            IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
            IOperationRecorder recorder = scope.ServiceProvider.GetRequiredService<IOperationRecorder>();
            if ((context.Worker + context.Iteration) % 5 == 0)
            {
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => dispatcher.DispatchAsync([new StressDomainEvent(context.OperationId, DateTimeOffset.UnixEpoch)], canceled.Token));
                Assert.Empty(recorder.Records);
                return;
            }

            await dispatcher.DispatchAsync([new StressDomainEvent(context.OperationId, DateTimeOffset.UnixEpoch)], context.CancellationToken);
            Assert.True(successful.TryAdd(context.OperationId, 0));
            Assert.Single(recorder.Records);
        });
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(StressDomainEvent).Assembly);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
