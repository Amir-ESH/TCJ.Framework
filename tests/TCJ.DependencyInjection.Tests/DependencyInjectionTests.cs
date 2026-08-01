using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Identifiers;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.Lifetimes;

namespace TCJ.DependencyInjection.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void Marker_types_are_registered_with_their_declared_lifetimes()
    {
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection(typeof(DependencyInjectionTests).Assembly);

        ServiceDescriptor scopedDescriptor = Assert.Single(collection: services.Where(descriptor => descriptor.ServiceType == typeof(ITestScopedService)));
        ServiceDescriptor selfSingletonDescriptor = Assert.Single(collection: services.Where(descriptor => descriptor.ServiceType == typeof(SelfSingletonService)));

        Assert.Equal(ServiceLifetime.Scoped, scopedDescriptor.Lifetime);
        Assert.Equal(typeof(TestScopedService), scopedDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, selfSingletonDescriptor.Lifetime);
        Assert.Equal(typeof(SelfSingletonService), selfSingletonDescriptor.ImplementationType);
    }

    [Fact]
    public void Framework_services_are_registered_once()
    {
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection(typeof(DependencyInjectionTests).Assembly);
        services.AddTcjDependencyInjection(typeof(DependencyInjectionTests).Assembly);

        Assert.Single(collection: services.Where(descriptor => descriptor.ServiceType == typeof(TimeProvider)));
        Assert.Single(collection: services.Where(descriptor => descriptor.ServiceType == typeof(IGuidGenerator)));
        Assert.Single(collection: services.Where(descriptor => descriptor.ServiceType == typeof(IDomainEventDispatcher)));
    }

    [Fact]
    public async Task Dispatcher_invokes_handlers_sequentially_in_registration_order()
    {
        var services = new ServiceCollection();
        services.AddSingleton<HandlerLog>();
        services.AddTcjDependencyInjection(typeof(DependencyInjectionTests).Assembly);

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();

        await dispatcher.DispatchAsync(domainEvents: [new TestDomainEvent(DateTimeOffset.UtcNow)], CancellationToken.None);

        HandlerLog log = scope.ServiceProvider.GetRequiredService<HandlerLog>();
        Assert.Equal(new[] { "first", "second" }, log.Entries);
    }
}

public interface ITestScopedService
{
    Guid InstanceId { get; }
}

public sealed class TestScopedService : ITestScopedService, IScopedDependency
{
    public Guid InstanceId { get; } = Guid.NewGuid();
}

public sealed class SelfSingletonService : ISelfSingletonDependency;

public sealed record TestDomainEvent(DateTimeOffset OccurredOn) : IDomainEvent;

public sealed class HandlerLog
{
    public List<string> Entries { get; } = [];
}

public sealed class FirstTestDomainEventHandler(HandlerLog log) : IDomainEventHandler<TestDomainEvent>
{
    public Task HandleAsync(TestDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        log.Entries.Add("first");
        return Task.CompletedTask;
    }
}

public sealed class SecondTestDomainEventHandler(HandlerLog log) : IDomainEventHandler<TestDomainEvent>
{
    public Task HandleAsync(TestDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        log.Entries.Add("second");
        return Task.CompletedTask;
    }
}
