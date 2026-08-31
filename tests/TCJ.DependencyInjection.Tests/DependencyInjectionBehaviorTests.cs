using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Identifiers;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.Lifetimes;

namespace TCJ.DependencyInjection.Tests;

public sealed class DependencyInjectionBehaviorTests
{
    [Fact]
    public void Registration_rejects_null_arguments()
    {
        IServiceCollection services = new ServiceCollection();
        IServiceCollection nullServices = null!;
        Assembly[] nullAssemblies = null!;

        Assert.Throws<ArgumentNullException>(() =>
            nullServices.AddTcjDependencyInjection());
        Assert.Throws<ArgumentNullException>(() =>
            nullServices.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly));
        Assert.Throws<ArgumentNullException>(() =>
            services.AddTcjDependencyInjection(nullAssemblies));
        Assert.Throws<ArgumentNullException>(() =>
            services.AddTcjDependencyInjection((Action<TCJ.DependencyInjection.Registration.TcjDependencyInjectionOptions>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            services.AddTcjDependencyInjection((TCJ.DependencyInjection.Registration.TcjDependencyInjectionOptions)null!));
    }

    [Fact]
    public void Every_marker_lifetime_is_registered_as_declared()
    {
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        AssertDescriptor<ITransientProbe, TransientProbe>(services, ServiceLifetime.Transient);
        AssertDescriptor<IScopedProbe, ScopedProbe>(services, ServiceLifetime.Scoped);
        AssertDescriptor<ISingletonProbe, SingletonProbe>(services, ServiceLifetime.Singleton);
        AssertDescriptor<SelfTransientProbe, SelfTransientProbe>(services, ServiceLifetime.Transient);
        AssertDescriptor<SelfScopedProbe, SelfScopedProbe>(services, ServiceLifetime.Scoped);
        AssertDescriptor<SelfSingletonProbe, SelfSingletonProbe>(services, ServiceLifetime.Singleton);
    }

    [Fact]
    public void Marker_service_lifetimes_are_observable_from_the_provider()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope firstScope = provider.CreateScope();
        using IServiceScope secondScope = provider.CreateScope();

        Assert.NotSame(
            firstScope.ServiceProvider.GetRequiredService<ITransientProbe>(),
            firstScope.ServiceProvider.GetRequiredService<ITransientProbe>());
        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<IScopedProbe>(),
            firstScope.ServiceProvider.GetRequiredService<IScopedProbe>());
        Assert.NotSame(
            firstScope.ServiceProvider.GetRequiredService<IScopedProbe>(),
            secondScope.ServiceProvider.GetRequiredService<IScopedProbe>());
        Assert.Same(
            firstScope.ServiceProvider.GetRequiredService<ISingletonProbe>(),
            secondScope.ServiceProvider.GetRequiredService<ISingletonProbe>());
    }

    [Fact]
    public void Options_can_disable_framework_services_and_domain_event_handlers()
    {
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection(options =>
        {
            options.RegisterFrameworkServices = false;
            options.RegisterDomainEventHandlers = false;
            options.AddAssemblyContaining<DependencyInjectionBehaviorTests>();
        });

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(TimeProvider));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IGuidGenerator));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IDomainEventDispatcher));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IDomainEventHandler<OrderedDomainEvent>));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IScopedProbe));
    }

    [Fact]
    public void Convention_scanning_skips_nested_public_dependencies_inside_non_public_containers()
    {
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IInaccessibleNestedProbe));
    }

    [Fact]
    public void Convention_scanning_skips_abstract_marked_dependencies()
    {
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(AbstractConventionProbe));
    }

    [Fact]
    public void Repeated_scanning_does_not_duplicate_framework_or_marked_services()
    {
        var services = new ServiceCollection();
        Assembly assembly = typeof(DependencyInjectionBehaviorTests).Assembly;

        services.AddTcjDependencyInjection(assembly, assembly);
        services.AddTcjDependencyInjection(assembly);

        Assert.Single(services.Where(descriptor => descriptor.ServiceType == typeof(TimeProvider)));
        Assert.Single(services.Where(descriptor => descriptor.ServiceType == typeof(IGuidGenerator)));
        Assert.Single(services.Where(descriptor => descriptor.ServiceType == typeof(IDomainEventDispatcher)));
        Assert.Single(services.Where(descriptor => descriptor.ServiceType == typeof(IScopedProbe)));
    }

    [Fact]
    public void Domain_event_handlers_are_registered_as_transient_in_stable_order()
    {
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        ServiceDescriptor[] descriptors = services
            .Where(descriptor => descriptor.ServiceType == typeof(IDomainEventHandler<OrderedDomainEvent>))
            .ToArray();

        Assert.Equal(2, descriptors.Length);
        Assert.All(descriptors, descriptor => Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime));
        Assert.Equal(typeof(FirstOrderedDomainEventHandler), descriptors[0].ImplementationType);
        Assert.Equal(typeof(SecondOrderedDomainEventHandler), descriptors[1].ImplementationType);
    }

    [Fact]
    public void Domain_event_handler_lifetime_markers_do_not_override_handler_pipeline_lifetime()
    {
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        ServiceDescriptor descriptor = Assert.Single(services.Where(
            item => item.ServiceType == typeof(IDomainEventHandler<LifetimeMarkedDomainEvent>)
                && item.ImplementationType == typeof(LifetimeMarkedDomainEventHandler)));

        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(LifetimeMarkedDomainEventHandler));
    }

    [Fact]
    public async Task Dispatcher_processes_multiple_events_and_handlers_sequentially()
    {
        var services = new ServiceCollection();
        services.AddSingleton<OrderedHandlerLog>();
        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();

        await dispatcher.DispatchAsync(
            [
                new OrderedDomainEvent(1, DateTimeOffset.UtcNow),
                new OrderedDomainEvent(2, DateTimeOffset.UtcNow)
            ],
            CancellationToken.None);

        OrderedHandlerLog log = scope.ServiceProvider.GetRequiredService<OrderedHandlerLog>();
        Assert.Equal(new[] { "first:1", "second:1", "first:2", "second:2" }, log.Entries);
    }

    [Fact]
    public async Task Dispatcher_accepts_empty_collection_and_event_without_handlers()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();

        await dispatcher.DispatchAsync([], CancellationToken.None);
        await dispatcher.DispatchAsync(
            [new UnhandledDomainEvent(DateTimeOffset.UtcNow)],
            CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_rejects_null_collection_and_null_item()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            dispatcher.DispatchAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            dispatcher.DispatchAsync([null!], CancellationToken.None));
    }

    [Fact]
    public async Task Dispatcher_observes_cancellation_before_invoking_handlers()
    {
        var services = new ServiceCollection();
        services.AddSingleton<OrderedHandlerLog>();
        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(
                [new OrderedDomainEvent(1, DateTimeOffset.UtcNow)],
                cancellation.Token));

        Assert.Empty(scope.ServiceProvider.GetRequiredService<OrderedHandlerLog>().Entries);
    }

    [Fact]
    public async Task Dispatcher_propagates_handler_exception_and_stops_later_handlers()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FailureHandlerLog>();
        services.AddTcjDependencyInjection(typeof(DependencyInjectionBehaviorTests).Assembly);

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(
                [new FailingDomainEvent(DateTimeOffset.UtcNow)],
                CancellationToken.None));

        Assert.Equal("expected handler failure", exception.Message);
        Assert.Equal(new[] { "throwing" }, scope.ServiceProvider.GetRequiredService<FailureHandlerLog>().Entries);
    }

    private static void AssertDescriptor<TService, TImplementation>(
        IServiceCollection services,
        ServiceLifetime lifetime)
    {
        ServiceDescriptor descriptor = Assert.Single(
            services.Where(item => item.ServiceType == typeof(TService)));
        Assert.Equal(typeof(TImplementation), descriptor.ImplementationType);
        Assert.Equal(lifetime, descriptor.Lifetime);
    }
}

public interface IInaccessibleNestedProbe;

internal static class InaccessibleDependencyContainer
{
    public sealed class NestedProbe : IInaccessibleNestedProbe, ITransientDependency;
}

public interface ITransientProbe;
public sealed class TransientProbe : ITransientProbe, ITransientDependency;

public interface IScopedProbe;
public sealed class ScopedProbe : IScopedProbe, IScopedDependency;

public interface ISingletonProbe;
public sealed class SingletonProbe : ISingletonProbe, ISingletonDependency;

public sealed class SelfTransientProbe : ISelfTransientDependency;
public sealed class SelfScopedProbe : ISelfScopedDependency;
public sealed class SelfSingletonProbe : ISelfSingletonDependency;

public abstract class AbstractConventionProbe : ISelfScopedDependency;

public sealed record OrderedDomainEvent(int Sequence, DateTimeOffset OccurredOn) : IDomainEvent;
public sealed record UnhandledDomainEvent(DateTimeOffset OccurredOn) : IDomainEvent;
public sealed record FailingDomainEvent(DateTimeOffset OccurredOn) : IDomainEvent;
public sealed record LifetimeMarkedDomainEvent(DateTimeOffset OccurredOn) : IDomainEvent;

public sealed class LifetimeMarkedDomainEventHandler
    : IDomainEventHandler<LifetimeMarkedDomainEvent>, IScopedDependency
{
    public Task HandleAsync(
        LifetimeMarkedDomainEvent domainEvent,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class OrderedHandlerLog
{
    public List<string> Entries { get; } = [];
}

public sealed class FirstOrderedDomainEventHandler(OrderedHandlerLog log)
    : IDomainEventHandler<OrderedDomainEvent>
{
    public Task HandleAsync(OrderedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        log.Entries.Add($"first:{domainEvent.Sequence}");
        return Task.CompletedTask;
    }
}

public sealed class SecondOrderedDomainEventHandler(OrderedHandlerLog log)
    : IDomainEventHandler<OrderedDomainEvent>
{
    public Task HandleAsync(OrderedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        log.Entries.Add($"second:{domainEvent.Sequence}");
        return Task.CompletedTask;
    }
}

public sealed class FailureHandlerLog
{
    public List<string> Entries { get; } = [];
}

public sealed class AThrowingDomainEventHandler(FailureHandlerLog log)
    : IDomainEventHandler<FailingDomainEvent>
{
    public Task HandleAsync(FailingDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        log.Entries.Add("throwing");
        throw new InvalidOperationException("expected handler failure");
    }
}

public sealed class ZNeverReachedDomainEventHandler(FailureHandlerLog log)
    : IDomainEventHandler<FailingDomainEvent>
{
    public Task HandleAsync(FailingDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        log.Entries.Add("never-reached");
        return Task.CompletedTask;
    }
}
