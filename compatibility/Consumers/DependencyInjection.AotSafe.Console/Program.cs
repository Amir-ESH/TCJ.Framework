using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Identifiers;
using TCJ.DependencyInjection.Extensions;

namespace TcjCompatibility.DependencyInjectionAotSafeConsumer;

public static class Program
{
    public static void Main()
    {
        var services = new ServiceCollection();
        var log = new DispatchLog();

        services.AddTcjDependencyInjection();
        services.AddTcjDependencyInjection();
        services.AddTcjDomainEvent<AotSafeDomainEvent>();
        services.AddTcjDomainEvent<AotSafeDomainEvent>();
        services.AddSingleton(log);
        services.AddTransient<IDomainEventHandler<AotSafeDomainEvent>, AotSafeDomainEventHandler>();

        EnsureSingleRegistration<TimeProvider>(services);
        EnsureSingleRegistration<IGuidGenerator>(services);
        EnsureSingleRegistration<IDomainEventDispatcher>(services);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        if (provider.GetRequiredService<TimeProvider>() != TimeProvider.System ||
            provider.GetRequiredService<IGuidGenerator>() is null)
        {
            throw new InvalidOperationException("Reflection-free framework registration is invalid.");
        }

        using IServiceScope scope = provider.CreateScope();
        IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
        dispatcher.DispatchAsync(
            [new AotSafeDomainEvent(42, DateTimeOffset.UnixEpoch)],
            CancellationToken.None).GetAwaiter().GetResult();

        if (log.Sequences.Count != 1 || log.Sequences[0] != 42)
        {
            throw new InvalidOperationException("AOT-safe domain-event dispatch did not invoke the explicit handler exactly once.");
        }

        Console.WriteLine("TCJ.DependencyInjection AOT-safe bootstrap consumer passed");
    }

    private static void EnsureSingleRegistration<TService>(IServiceCollection services)
    {
        if (services.Count(descriptor => descriptor.ServiceType == typeof(TService)) != 1)
        {
            throw new InvalidOperationException($"Expected exactly one {typeof(TService).Name} registration.");
        }
    }

    private sealed record AotSafeDomainEvent(int Sequence, DateTimeOffset OccurredOn) : IDomainEvent;

    private sealed class DispatchLog
    {
        public List<int> Sequences { get; } = [];
    }

    private sealed class AotSafeDomainEventHandler(DispatchLog log)
        : IDomainEventHandler<AotSafeDomainEvent>
    {
        public Task HandleAsync(AotSafeDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Sequences.Add(domainEvent.Sequence);
            return Task.CompletedTask;
        }
    }
}
