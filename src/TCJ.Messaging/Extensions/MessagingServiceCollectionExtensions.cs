using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TCJ.Core.DomainEvents;
using TCJ.Core.Resilience;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.InMemory;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.Serialization;
using TCJ.Messaging.Topology;

namespace TCJ.Messaging.Extensions;

/// <summary>Registers transport-neutral messaging contracts and optional test transport integrations.</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>Registers the neutral messaging layer without changing existing Outbox behavior.</summary>
    /// <param name="services">Application service collection.</param>
    /// <param name="configure">Optional messaging configuration callback.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjMessaging(this IServiceCollection services, Action<TcjMessagingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new TcjMessagingOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MessagingHeaderPolicy>();
        services.TryAddSingleton<IMessageContractRegistry, MessageContractRegistry>();
        services.TryAddSingleton<IMessageSerializer, SystemTextJsonMessageSerializer>();
        services.TryAddSingleton<IMessageTopologyNamingStrategy, DefaultMessageTopologyNamingStrategy>();
        services.TryAddSingleton<IMessagingStartupValidator, MessagingStartupValidator>();
        services.TryAddSingleton<MessagingConsumerState>();
        services.TryAddSingleton<MessagePublisher>();
        services.TryAddSingleton<IMessagePublisher>(static sp => sp.GetRequiredService<MessagePublisher>());
        services.TryAddSingleton<IMessageBatchPublisher, MessageBatchPublisher>();
        services.TryAddSingleton(typeof(IMessagePublisher<>), typeof(TypedMessagePublisher<>));
        if (options.EnableConsumer)
        {
            services.TryAddTransient<InboxTransportBridge>();
            services.TryAddTransient<IMessageConsumerRunner, MessageConsumerRunner>();
        }
        services.TryAddSingleton<MessagingTransportHealthCheck>();
        services.TryAddSingleton<MessagingPublisherHealthCheck>();
        services.TryAddSingleton<MessagingConsumerHealthCheck>();
        services.TryAddSingleton<MessagingTopologyHealthCheck>();
        return services;
    }

    /// <summary>Explicitly decorates domain-event dispatch so committed Outbox deliveries publish through TCJ.Messaging.</summary>
    /// <param name="services">Service collection that already contains the application's <see cref="IDomainEventDispatcher"/> registration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjMessagingOutboxBridge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(MessagingOutboxBridgeMarker)))
            return services;

        ServiceDescriptor? existing = services.LastOrDefault(static descriptor => descriptor.ServiceType == typeof(IDomainEventDispatcher));
        if (existing is null)
            throw new InvalidOperationException("AddTcjMessagingOutboxBridge must be called after the application's IDomainEventDispatcher registration.");
        if (existing.ImplementationType == typeof(MessagingOutboxDomainEventDispatcher))
            return services;

        services.Remove(existing);
        services.Add(new ServiceDescriptor(
            typeof(IDomainEventDispatcher),
            serviceProvider =>
            {
                IDomainEventDispatcher inner = CreateDispatcher(serviceProvider, existing);
                return ActivatorUtilities.CreateInstance<MessagingOutboxDomainEventDispatcher>(serviceProvider, inner);
            },
            existing.Lifetime));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITransientFailureClassifier, MessagingOutboxTransientFailureClassifier>());
        services.AddSingleton<MessagingOutboxBridgeMarker>();
        return services;
    }

    /// <summary>Registers one explicit logical message contract using source-generated or otherwise explicit JSON metadata.</summary>
    /// <typeparam name="TMessage">CLR message type associated with the logical contract.</typeparam>
    /// <param name="services">Application service collection.</param>
    /// <param name="messageType">Stable logical message type name.</param>
    /// <param name="messageVersion">Positive schema version.</param>
    /// <param name="jsonTypeInfo">Explicit System.Text.Json metadata for the message type.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjMessage<TMessage>(this IServiceCollection services, string messageType, int messageVersion, JsonTypeInfo<TMessage> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        MessagingValidation.ValidateMessageType(messageType, nameof(messageType), 128);
        MessagingValidation.ValidateVersion(messageVersion, nameof(messageVersion));
        services.AddSingleton(new MessagingMessageContract(messageType, messageVersion, typeof(TMessage), jsonTypeInfo));
        return services;
    }

    /// <summary>Registers one explicit schema upcaster.</summary>
    /// <typeparam name="TUpcaster">Upcaster implementation type.</typeparam>
    /// <param name="services">Application service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjMessageUpcaster<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TUpcaster>(this IServiceCollection services) where TUpcaster : class, IMessageUpcaster
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageUpcaster, TUpcaster>());
        return services;
    }

    /// <summary>Registers stable messaging readiness health checks with the application health-check builder.</summary>
    /// <param name="builder">Application health-check builder.</param>
    /// <returns>The same builder for chaining.</returns>
    public static IHealthChecksBuilder AddTcjMessagingHealthChecks(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddCheck<MessagingTransportHealthCheck>(TcjMessagingHealthCheckNames.Transport);
        builder.AddCheck<MessagingPublisherHealthCheck>(TcjMessagingHealthCheckNames.Publisher);
        builder.AddCheck<MessagingConsumerHealthCheck>(TcjMessagingHealthCheckNames.Consumer);
        builder.AddCheck<MessagingTopologyHealthCheck>(TcjMessagingHealthCheckNames.Topology);
        return builder;
    }

    /// <summary>Registers the bounded non-durable in-memory adapter for tests and local development.</summary>
    /// <param name="services">Application service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjInMemoryMessaging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<InMemoryMessagingTransport>();
        services.TryAddSingleton<IMessagingTransportPublisher>(static sp => sp.GetRequiredService<InMemoryMessagingTransport>());
        services.TryAddSingleton<IMessagingTransportBatchPublisher>(static sp => sp.GetRequiredService<InMemoryMessagingTransport>());
        services.TryAddSingleton<IMessageReceiver>(static sp => sp.GetRequiredService<InMemoryMessagingTransport>());
        services.TryAddSingleton<IMessagingTransportHealthProbe>(static sp => sp.GetRequiredService<InMemoryMessagingTransport>());
        services.TryAddSingleton(new MessagingTransportDescriptor
        {
            Name = "in-memory",
            Version = "1",
            Capabilities = new MessagingTransportCapabilities
            {
                SupportsBatchPublish = true,
                SupportsDeadLetter = true,
                SupportsPeekLock = true,
                OrderingGuarantee = MessagingOrderingGuarantee.None,
                MaximumPayloadBytes = 16 * 1024 * 1024,
                MaximumHeaderBytes = 64 * 1024,
                MaximumBatchSize = 256
            }
        });
        return services;
    }
    private static IDomainEventDispatcher CreateDispatcher(IServiceProvider serviceProvider, ServiceDescriptor descriptor)
    {
        object? instance = descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(serviceProvider)
            ?? (descriptor.ImplementationType is null
                ? null
                : ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType));
        return instance as IDomainEventDispatcher
            ?? throw new InvalidOperationException("The existing IDomainEventDispatcher registration could not be materialized.");
    }

    private sealed class MessagingOutboxBridgeMarker;

}
