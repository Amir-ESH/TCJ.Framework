using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.RabbitMQ.Connections;
using TCJ.Messaging.RabbitMQ.HealthChecks;
using TCJ.Messaging.RabbitMQ.Publishing;
using TCJ.Messaging.RabbitMQ.Receiving;
using TCJ.Messaging.RabbitMQ.Topology;
using TCJ.Messaging.Topology;

namespace TCJ.Messaging.RabbitMQ.Extensions;

/// <summary>Registers the production RabbitMQ adapter for TCJ Messaging.</summary>
public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>Registers one RabbitMQ transport. <c>AddTcjMessaging</c> must be called first.</summary>
    /// <param name="services">Application service collection.</param>
    /// <param name="configure">RabbitMQ connection, topology, and transport configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjRabbitMq(this IServiceCollection services, Action<TcjRabbitMqOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        TcjMessagingOptions messagingOptions = services.LastOrDefault(static x => x.ServiceType == typeof(TcjMessagingOptions))?.ImplementationInstance as TcjMessagingOptions
            ?? throw new InvalidOperationException("AddTcjRabbitMq must be called after AddTcjMessaging.");
        if (services.Any(static x => x.ServiceType == typeof(IMessagingTransportPublisher) || x.ServiceType == typeof(MessagingTransportDescriptor)))
            throw new InvalidOperationException("A messaging transport is already registered. TCJ allows exactly one default transport registration.");

        var options = new TcjRabbitMqOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        ServiceDescriptor? topologyRegistration = services.LastOrDefault(static x => x.ServiceType == typeof(IMessageTopologyNamingStrategy));
        if (topologyRegistration?.ImplementationType == typeof(DefaultMessageTopologyNamingStrategy))
        {
            services.Remove(topologyRegistration);
            services.AddSingleton<IMessageTopologyNamingStrategy>(static sp => new RabbitMqMessageTopologyNamingStrategy(sp.GetRequiredService<TcjRabbitMqOptions>()));
        }

        services.TryAddSingleton<IRabbitMqRoutingKeyStrategy, DefaultRabbitMqRoutingKeyStrategy>();
        services.TryAddSingleton<RabbitMqConnectionManager>();
        services.TryAddSingleton<RabbitMqMessageMapper>();
        services.TryAddSingleton<RabbitMqTopologyManager>();
        services.TryAddSingleton<RabbitMqTransportPublisher>();
        services.AddSingleton<IMessagingTransportPublisher>(static sp => sp.GetRequiredService<RabbitMqTransportPublisher>());
        services.TryAddSingleton<RabbitMqMessageReceiver>();
        services.AddSingleton<IMessageReceiver>(static sp => sp.GetRequiredService<RabbitMqMessageReceiver>());
        services.TryAddSingleton<RabbitMqTransportHealthProbe>();
        services.AddSingleton<IMessagingTransportHealthProbe>(static sp => sp.GetRequiredService<RabbitMqTransportHealthProbe>());

        services.AddSingleton(new MessagingTransportDescriptor
        {
            Name = "RabbitMQ",
            Version = GetPackageVersion(),
            Capabilities = new MessagingTransportCapabilities
            {
                SupportsBatchPublish = false,
                SupportsScheduling = false,
                SupportsTimeToLive = true,
                SupportsDeadLetter = true,
                SupportsDefer = false,
                SupportsOrderedDelivery = true,
                SupportsPartitioning = true,
                SupportsTransactions = false,
                SupportsPeekLock = false,
                OrderingGuarantee = MessagingOrderingGuarantee.BestEffort,
                MaximumPayloadBytes = 64 * 1024 * 1024,
                MaximumHeaderBytes = 64 * 1024,
                MaximumBatchSize = null
            }
        });

        services.TryAddSingleton<MessagingStartupValidator>();
        services.RemoveAll<IMessagingStartupValidator>();
        services.TryAddSingleton<RabbitMqStartupValidator>();
        services.AddSingleton<IMessagingStartupValidator>(static sp => sp.GetRequiredService<RabbitMqStartupValidator>());

        if (messagingOptions.EnableConsumer)
        {
            services.RemoveAll<IMessageConsumerRunner>();
            services.AddTransient<RabbitMqMessageConsumerRunner>();
            services.AddTransient<IMessageConsumerRunner>(static sp => sp.GetRequiredService<RabbitMqMessageConsumerRunner>());
        }

        services.TryAddSingleton<RabbitMqConnectionHealthCheck>();
        services.TryAddSingleton<RabbitMqPublisherHealthCheck>();
        services.TryAddSingleton<RabbitMqConsumerHealthCheck>();
        services.TryAddSingleton<RabbitMqTopologyHealthCheck>();
        return services;
    }

    /// <summary>Adds explicit RabbitMQ topology using a fluent builder. Call after <see cref="AddTcjRabbitMq(IServiceCollection, Action{TcjRabbitMqOptions}?)"/>.</summary>
    /// <param name="services">Application service collection.</param>
    /// <param name="configure">Topology declaration callback.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjRabbitMqTopology(this IServiceCollection services, Action<RabbitMqTopologyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        TcjRabbitMqOptions options = services.LastOrDefault(static x => x.ServiceType == typeof(TcjRabbitMqOptions))?.ImplementationInstance as TcjRabbitMqOptions
            ?? throw new InvalidOperationException("AddTcjRabbitMqTopology must be called after AddTcjRabbitMq.");
        configure(new RabbitMqTopologyBuilder(options.Topology));
        return services;
    }

    /// <summary>Registers RabbitMQ readiness checks. These checks are intended for readiness, not dependency-independent liveness.</summary>
    /// <param name="builder">Application health-check builder.</param>
    /// <returns>The same health-check builder.</returns>
    public static IHealthChecksBuilder AddTcjRabbitMqHealthChecks(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        string[] readiness = ["ready", "rabbitmq"];
        builder.AddCheck<RabbitMqConnectionHealthCheck>(TcjRabbitMqHealthCheckNames.Connection, failureStatus: HealthStatus.Unhealthy, tags: readiness);
        builder.AddCheck<RabbitMqPublisherHealthCheck>(TcjRabbitMqHealthCheckNames.Publisher, failureStatus: HealthStatus.Unhealthy, tags: readiness);
        builder.AddCheck<RabbitMqConsumerHealthCheck>(TcjRabbitMqHealthCheckNames.Consumer, failureStatus: HealthStatus.Unhealthy, tags: readiness);
        builder.AddCheck<RabbitMqTopologyHealthCheck>(TcjRabbitMqHealthCheckNames.Topology, failureStatus: HealthStatus.Unhealthy, tags: readiness);
        return builder;
    }

    private static string GetPackageVersion()
    {
        string? informational = typeof(RabbitMqServiceCollectionExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string value = string.IsNullOrWhiteSpace(informational) ? "0.1.0-preview.5" : informational.Split('+', 2)[0];
        return value;
    }
}
