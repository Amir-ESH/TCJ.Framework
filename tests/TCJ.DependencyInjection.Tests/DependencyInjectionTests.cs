using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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
    public void Reflection_free_bootstrap_registers_only_framework_services_once()
    {
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection();
        services.AddTcjDependencyInjection();

        Assert.Single(collection: services.Where(descriptor => descriptor.ServiceType == typeof(TimeProvider)));
        Assert.Single(collection: services.Where(descriptor => descriptor.ServiceType == typeof(IGuidGenerator)));
        Assert.Single(collection: services.Where(descriptor => descriptor.ServiceType == typeof(IDomainEventDispatcher)));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ITestScopedService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IDomainEventHandler<TestDomainEvent>));
    }

    [Fact]
    public void Scanning_overloads_are_trim_restricted_but_safe_bootstrap_is_not()
    {
        MethodInfo[] overloads = typeof(ServiceCollectionExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(ServiceCollectionExtensions.AddTcjDependencyInjection))
            .ToArray();

        MethodInfo safeOverload = Assert.Single(
            overloads.Where(method => method.GetParameters().Length == 1));
        Assert.Null(safeOverload.GetCustomAttribute<RequiresUnreferencedCodeAttribute>());
        Assert.Null(safeOverload.GetCustomAttribute<RequiresDynamicCodeAttribute>());

        MethodInfo[] scanningOverloads = overloads
            .Where(method => method.GetParameters().Length == 2)
            .ToArray();
        Assert.Equal(3, scanningOverloads.Length);

        Assert.All(scanningOverloads, method =>
        {
            RequiresUnreferencedCodeAttribute trimmingAttribute = Assert.IsType<RequiresUnreferencedCodeAttribute>(
                method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>());
            RequiresDynamicCodeAttribute aotAttribute = Assert.IsType<RequiresDynamicCodeAttribute>(
                method.GetCustomAttribute<RequiresDynamicCodeAttribute>());
            Assert.Contains("AddTcjDomainEvent<TEvent>()", trimmingAttribute.Message, StringComparison.Ordinal);
            Assert.Contains("AddTcjDomainEvent<TEvent>()", aotAttribute.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Reflection_free_dispatch_uses_explicit_event_route_and_manual_handler_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<HandlerLog>();
        services.AddTcjDependencyInjection();
        services.AddTcjDomainEvent<TestDomainEvent>();
        services.AddTcjDomainEvent<TestDomainEvent>();
        services.AddTransient<IDomainEventHandler<TestDomainEvent>, FirstTestDomainEventHandler>();
        services.AddTransient<IDomainEventHandler<TestDomainEvent>, SecondTestDomainEventHandler>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();

        await dispatcher.DispatchAsync([new TestDomainEvent(DateTimeOffset.UnixEpoch)]);

        HandlerLog log = scope.ServiceProvider.GetRequiredService<HandlerLog>();
        Assert.Equal(new[] { "first", "second" }, log.Entries);
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
