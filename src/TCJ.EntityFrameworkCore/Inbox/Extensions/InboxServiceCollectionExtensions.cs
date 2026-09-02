using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TCJ.Core.Inbox;
using TCJ.Core.Resilience;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Inbox.HealthChecks;
using TCJ.EntityFrameworkCore.Inbox.Processing;
using TCJ.EntityFrameworkCore.Inbox.Serialization;

namespace TCJ.EntityFrameworkCore.Inbox.Extensions;

/// <summary>Registers provider-independent transactional Inbox services and stable message contracts.</summary>
public static class InboxServiceCollectionExtensions
{
    /// <summary>Enables one opt-in transactional Inbox boundary for the specified DbContext.</summary>
    /// <typeparam name="TDbContext">Consumer DbContext that owns Inbox and business state.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Required configuration that must set a stable consumer name.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjInbox<TDbContext>(this IServiceCollection services, Action<TcjInboxOptions> configure)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        EnsureSingleContext<TDbContext>(services);
        var options = new TcjInboxOptions();
        configure(options);
        options.Validate();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ITransientFailureDetector, TransientFailureDetector>();
        services.TryAddSingleton<InboxProcessorState>();
        services.TryAddSingleton<InboxMessageContextAccessor>();
        services.TryAddSingleton<IInboxMessageContextAccessor>(static sp => sp.GetRequiredService<InboxMessageContextAccessor>());
        services.TryAddSingleton<IInboxSerializer, SystemTextJsonInboxSerializer>();
        services.TryAddSingleton<InboxMessageRegistry>();
        services.TryAddScoped<IInboxStartupValidator, InboxStartupValidator<TDbContext>>();
        services.TryAddSingleton<InboxCoordinator<TDbContext>>();
        services.TryAddSingleton<IInboxPipeline>(static sp => sp.GetRequiredService<InboxCoordinator<TDbContext>>());
        services.TryAddSingleton<IInboxDeferredProcessor>(static sp => sp.GetRequiredService<InboxCoordinator<TDbContext>>());
        services.TryAddSingleton<IInboxReplayService>(static sp => sp.GetRequiredService<InboxCoordinator<TDbContext>>());
        services.TryAddSingleton<IInboxCleanupService>(static sp => sp.GetRequiredService<InboxCoordinator<TDbContext>>());
        services.AddHealthChecks().AddTcjInbox();
        return services;
    }

    /// <summary>Registers a stable logical message type and positive schema version.</summary>
    /// <typeparam name="TMessage">CLR message contract.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="messageType">Stable wire-contract name.</param>
    /// <param name="version">Positive schema version.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjInboxMessage<TMessage>(this IServiceCollection services, string messageType, int version)
    {
        ArgumentNullException.ThrowIfNull(services);
        InboxMessageRegistry.ValidateMessageName(messageType);
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version), "Inbox schema version must be greater than zero.");
        lock (services)
        {
            InboxMessageRegistration? sameContract = services.Where(static d => d.ServiceType == typeof(InboxMessageRegistration)).Select(static d => d.ImplementationInstance).OfType<InboxMessageRegistration>().FirstOrDefault(r => string.Equals(r.MessageName, messageType, StringComparison.Ordinal) && r.Version == version);
            if (sameContract is not null)
            {
                if (sameContract.MessageType != typeof(TMessage)) throw new InvalidOperationException($"Inbox contract '{messageType}' v{version} is already registered for '{sameContract.MessageType.FullName}'.");
                return services;
            }
            InboxMessageRegistration? sameType = services.Where(static d => d.ServiceType == typeof(InboxMessageRegistration)).Select(static d => d.ImplementationInstance).OfType<InboxMessageRegistration>().FirstOrDefault(r => r.MessageType == typeof(TMessage));
            if (sameType is not null) throw new InvalidOperationException($"Inbox CLR type '{typeof(TMessage).FullName}' is already registered as '{sameType.MessageName}' v{sameType.Version}.");
            services.AddSingleton(new InboxMessageRegistration(typeof(TMessage), messageType, version));
        }
        return services;
    }

    /// <summary>Registers exactly one handler for a previously registered Inbox CLR message type.</summary>
    /// <typeparam name="TMessage">Registered CLR message contract.</typeparam>
    /// <typeparam name="THandler">Scoped handler implementation.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjInboxHandler<TMessage, THandler>(this IServiceCollection services)
        where THandler : class, IInboxMessageHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (services)
        {
            InboxHandlerRegistration? existing = services.Where(static d => d.ServiceType == typeof(InboxHandlerRegistration)).Select(static d => d.ImplementationInstance).OfType<InboxHandlerRegistration>().FirstOrDefault(r => r.MessageType == typeof(TMessage));
            if (existing is not null)
            {
                if (existing.HandlerType != typeof(THandler)) throw new InvalidOperationException($"Inbox message CLR type '{typeof(TMessage).FullName}' already has handler '{existing.HandlerType.FullName}'. Ambiguous handlers are not allowed.");
                return services;
            }
            services.TryAddScoped<THandler>();
            services.TryAddScoped<IInboxMessageHandler<TMessage>>(static sp => sp.GetRequiredService<THandler>());
            services.AddSingleton(new InboxHandlerRegistration(
                typeof(TMessage),
                typeof(THandler),
                static async (provider, message, context, token) =>
                {
                    THandler handler = provider.GetRequiredService<THandler>();
                    await handler.HandleAsync((TMessage)message, context, token).ConfigureAwait(false);
                }));
        }
        return services;
    }

    private static void EnsureSingleContext<TDbContext>(IServiceCollection services) where TDbContext : DbContext
    {
        lock (services)
        {
            ServiceDescriptor? descriptor = services.FirstOrDefault(static d => d.ServiceType == typeof(InboxContextMarker));
            if (descriptor?.ImplementationInstance is InboxContextMarker marker)
            {
                if (marker.DbContextType != typeof(TDbContext)) throw new InvalidOperationException($"TCJ Inbox is already registered for DbContext '{marker.DbContextType.Name}'. Register one Inbox DbContext per service container.");
                return;
            }
            services.AddSingleton(new InboxContextMarker(typeof(TDbContext)));
        }
    }
    private sealed record InboxContextMarker(Type DbContextType);
}
