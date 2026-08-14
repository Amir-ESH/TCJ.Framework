using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TCJ.Core.Identifiers;
using TCJ.Core.Outbox;
using TCJ.Core.Resilience;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Outbox.HealthChecks;
using TCJ.EntityFrameworkCore.Outbox.Interceptors;
using TCJ.EntityFrameworkCore.Outbox.Processing;
using TCJ.EntityFrameworkCore.Outbox.Serialization;

namespace TCJ.EntityFrameworkCore.Outbox.Extensions;

/// <summary>Registers provider-independent transactional-outbox services.</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Enables transactional-outbox capture and processing abstractions for one TCJ DbContext.
    /// A provider-specific <see cref="IOutboxStorage"/> must also be registered.
    /// </summary>
    /// <typeparam name="TDbContext">TCJ Entity Framework Core context that owns the outbox table.</typeparam>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configure">Optional bounded outbox configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjOutbox<TDbContext>(
        this IServiceCollection services,
        Action<TcjOutboxOptions>? configure = null)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        EnsureSingleContext<TDbContext>(services);

        var options = new TcjOutboxOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IGuidGenerator, GuidGenerator>();
        services.TryAddSingleton<ITransientFailureDetector, TransientFailureDetector>();
        services.TryAddSingleton<OutboxProcessorState>();
        services.TryAddSingleton<OutboxCaptureTracker>();
        services.TryAddSingleton<OutboxMessageContextAccessor>();
        services.TryAddSingleton<IOutboxMessageContextAccessor>(static provider => provider.GetRequiredService<OutboxMessageContextAccessor>());
        services.TryAddSingleton<IOutboxSerializer, SystemTextJsonOutboxSerializer>();
        services.TryAddSingleton<IOutboxEventTypeResolver, OutboxEventTypeResolver>();
        services.TryAddScoped<OutboxSaveChangesInterceptor>();
        services.TryAddScoped<OutboxTransactionInterceptor>();
        services.TryAddScoped<IOutboxStartupValidator, OutboxStartupValidator<TDbContext>>();
        services.TryAddScoped<OutboxProcessor>();
        services.TryAddScoped<IOutboxProcessor>(static provider => provider.GetRequiredService<OutboxProcessor>());
        services.TryAddScoped<IOutboxReplayService>(static provider => provider.GetRequiredService<OutboxProcessor>());
        services.TryAddScoped<IOutboxCleanupService>(static provider => provider.GetRequiredService<OutboxProcessor>());
        services.AddHealthChecks().AddTcjOutbox();
        return services;
    }

    /// <summary>Registers an explicit stable, versioned logical name for a domain-event contract.</summary>
    /// <typeparam name="TEvent">Domain-event CLR type associated with the stable logical name.</typeparam>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="eventName">Stable logical event name, such as <c>order.completed.v1</c>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjOutboxEvent<TEvent>(this IServiceCollection services, string eventName)
        where TEvent : TCJ.Core.DomainEvents.IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(services);
        OutboxEventTypeResolver.ValidateName(eventName);
        services.AddSingleton(new OutboxEventRegistration(typeof(TEvent), eventName));
        return services;
    }

    private static void EnsureSingleContext<TDbContext>(IServiceCollection services)
        where TDbContext : DbContext
    {
        lock (services)
        {
            ServiceDescriptor? existing = services.FirstOrDefault(static descriptor => descriptor.ServiceType == typeof(OutboxContextMarker));
            if (existing?.ImplementationInstance is OutboxContextMarker marker)
            {
                if (marker.DbContextType != typeof(TDbContext))
                {
                    throw new InvalidOperationException($"TCJ transactional outbox is already registered for DbContext '{marker.DbContextType.Name}'. Register only one outbox processor per service container.");
                }
                return;
            }

            services.AddSingleton(new OutboxContextMarker(typeof(TDbContext)));
        }
    }

    private sealed record OutboxContextMarker(Type DbContextType);
}
