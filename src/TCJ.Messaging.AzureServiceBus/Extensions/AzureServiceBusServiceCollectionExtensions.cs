using System.Reflection;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Connections;
using TCJ.Messaging.AzureServiceBus.HealthChecks;
using TCJ.Messaging.AzureServiceBus.Publishing;
using TCJ.Messaging.AzureServiceBus.Receiving;
using TCJ.Messaging.AzureServiceBus.Topology;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.AzureServiceBus.Extensions;

/// <summary>Registers the production Azure Service Bus adapter for TCJ Messaging.</summary>
public static class AzureServiceBusServiceCollectionExtensions
{
    /// <summary>Registers Azure Service Bus using an explicit connection string.</summary>
    public static IServiceCollection AddTcjAzureServiceBus(this IServiceCollection services, string connectionString, Action<TcjAzureServiceBusOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return AddCore(services, credential: null, options => { configure?.Invoke(options); options.ConnectionString = connectionString; options.FullyQualifiedNamespace = null; });
    }

    /// <summary>Registers Azure Service Bus using a namespace and token credential. Managed identity/workload identity/service principals are supplied through <see cref="TokenCredential"/>.</summary>
    public static IServiceCollection AddTcjAzureServiceBus(this IServiceCollection services, string fullyQualifiedNamespace, TokenCredential credential, Action<TcjAzureServiceBusOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedNamespace); ArgumentNullException.ThrowIfNull(credential);
        return AddCore(services, credential, options => { configure?.Invoke(options); options.FullyQualifiedNamespace = fullyQualifiedNamespace; options.ConnectionString = null; });
    }

    /// <summary>Registers Azure Service Bus using adapter options. This overload is connection-string based; token credentials use the dedicated overload.</summary>
    public static IServiceCollection AddTcjAzureServiceBus(this IServiceCollection services, Action<TcjAzureServiceBusOptions> configure)
    { ArgumentNullException.ThrowIfNull(configure); return AddCore(services, credential: null, configure); }

    /// <summary>Adds explicit runtime topology metadata/declaration expectations.</summary>
    public static IServiceCollection AddTcjAzureServiceBusTopology(this IServiceCollection services, Action<AzureServiceBusTopologyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(configure);
        TcjAzureServiceBusOptions options = services.LastOrDefault(static x => x.ServiceType == typeof(TcjAzureServiceBusOptions))?.ImplementationInstance as TcjAzureServiceBusOptions
            ?? throw new InvalidOperationException("AddTcjAzureServiceBusTopology must be called after AddTcjAzureServiceBus.");
        configure(new AzureServiceBusTopologyBuilder(options.Topology));
        return services;
    }

    /// <summary>Registers Azure Service Bus readiness checks. Dependency-independent liveness should remain separate.</summary>
    public static IHealthChecksBuilder AddTcjAzureServiceBusHealthChecks(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(static descriptor => descriptor.ServiceType == typeof(AzureServiceBusHealthChecksMarker)))
            return builder;
        builder.Services.AddSingleton(new AzureServiceBusHealthChecksMarker());
        string[] tags = ["ready", "azure-service-bus"];
        builder.AddCheck<AzureServiceBusClientHealthCheck>(TcjAzureServiceBusHealthCheckNames.Client, failureStatus: HealthStatus.Unhealthy, tags: tags);
        builder.AddCheck<AzureServiceBusSenderHealthCheck>(TcjAzureServiceBusHealthCheckNames.Sender, failureStatus: HealthStatus.Unhealthy, tags: tags);
        builder.AddCheck<AzureServiceBusProcessorHealthCheck>(TcjAzureServiceBusHealthCheckNames.Processor, failureStatus: HealthStatus.Unhealthy, tags: tags);
        builder.AddCheck<AzureServiceBusTopologyHealthCheck>(TcjAzureServiceBusHealthCheckNames.Topology, failureStatus: HealthStatus.Unhealthy, tags: tags);
        builder.AddCheck<AzureServiceBusSessionProcessorHealthCheck>(TcjAzureServiceBusHealthCheckNames.SessionProcessor, failureStatus: HealthStatus.Unhealthy, tags: tags);
        return builder;
    }

    private static IServiceCollection AddCore(IServiceCollection services, TokenCredential? credential, Action<TcjAzureServiceBusOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(configure);
        TcjMessagingOptions messaging = services.LastOrDefault(static x => x.ServiceType == typeof(TcjMessagingOptions))?.ImplementationInstance as TcjMessagingOptions
            ?? throw new InvalidOperationException("AddTcjAzureServiceBus must be called after AddTcjMessaging.");
        if (services.Any(static x => x.ServiceType == typeof(IMessagingTransportPublisher) || x.ServiceType == typeof(MessagingTransportDescriptor)))
            throw new InvalidOperationException("A messaging transport is already registered. TCJ allows exactly one default transport registration.");

        var options = new TcjAzureServiceBusOptions(); configure(options); options.Validate(credential is not null);
        services.AddSingleton(options);
        services.AddSingleton(new AzureServiceBusAuthentication(credential));
        services.TryAddSingleton<IAzureServiceBusClientFactory>(static _ => new DefaultAzureServiceBusClientFactory());
        services.TryAddSingleton<AzureServiceBusClientManager>(static sp => new AzureServiceBusClientManager(
            sp.GetRequiredService<TcjAzureServiceBusOptions>(),
            sp.GetRequiredService<AzureServiceBusAuthentication>(),
            sp.GetRequiredService<IAzureServiceBusClientFactory>()));
        services.TryAddSingleton<AzureServiceBusMessageMapper>(static sp => new AzureServiceBusMessageMapper(
            sp.GetRequiredService<MessagingHeaderPolicy>(),
            sp.GetRequiredService<TcjAzureServiceBusOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<AzureServiceBusTopologyManager>(static sp => new AzureServiceBusTopologyManager(
            sp.GetRequiredService<TcjAzureServiceBusOptions>(),
            sp.GetRequiredService<AzureServiceBusClientManager>()));
        services.TryAddSingleton<AzureServiceBusTransportPublisher>(static sp => new AzureServiceBusTransportPublisher(
            sp.GetRequiredService<AzureServiceBusClientManager>(),
            sp.GetRequiredService<AzureServiceBusMessageMapper>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMessagingTransportPublisher>(static sp => sp.GetRequiredService<AzureServiceBusTransportPublisher>());
        services.AddSingleton<IMessagingTransportBatchPublisher>(static sp => sp.GetRequiredService<AzureServiceBusTransportPublisher>());
        services.TryAddSingleton<AzureServiceBusMessageReceiver>(static sp => new AzureServiceBusMessageReceiver(
            sp.GetRequiredService<AzureServiceBusClientManager>(),
            sp.GetRequiredService<AzureServiceBusMessageMapper>(),
            sp.GetRequiredService<TcjAzureServiceBusOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMessageReceiver>(static sp => sp.GetRequiredService<AzureServiceBusMessageReceiver>());
        services.TryAddSingleton<AzureServiceBusTransportHealthProbe>(static sp => new AzureServiceBusTransportHealthProbe(
            sp.GetRequiredService<AzureServiceBusClientManager>(),
            sp.GetRequiredService<TcjAzureServiceBusOptions>()));
        services.AddSingleton<IMessagingTransportHealthProbe>(static sp => sp.GetRequiredService<AzureServiceBusTransportHealthProbe>());

        services.AddSingleton(new MessagingTransportDescriptor
        {
            Name = "AzureServiceBus",
            Version = GetPackageVersion(),
            Capabilities = new MessagingTransportCapabilities
            {
                SupportsBatchPublish = true,
                SupportsScheduling = true,
                SupportsTimeToLive = true,
                SupportsDeadLetter = true,
                SupportsDefer = true,
                SupportsOrderedDelivery = true,
                SupportsPartitioning = true,
                SupportsTransactions = false,
                SupportsPeekLock = true,
                OrderingGuarantee = MessagingOrderingGuarantee.PerSession,
                MaximumPayloadBytes = null,
                MaximumHeaderBytes = null,
                MaximumBatchSize = 1000
            }
        });

        services.TryAddSingleton<MessagingStartupValidator>();
        services.RemoveAll<IMessagingStartupValidator>();
        services.TryAddSingleton<AzureServiceBusStartupValidator>(static sp => new AzureServiceBusStartupValidator(
            sp.GetRequiredService<MessagingStartupValidator>(),
            sp.GetRequiredService<AzureServiceBusTopologyManager>(),
            sp.GetRequiredService<AzureServiceBusClientManager>(),
            sp.GetRequiredService<TcjAzureServiceBusOptions>(),
            sp.GetRequiredService<TcjMessagingOptions>(),
            sp.GetRequiredService<AzureServiceBusAuthentication>()));
        services.AddSingleton<IMessagingStartupValidator>(static sp => sp.GetRequiredService<AzureServiceBusStartupValidator>());
        if (messaging.EnableConsumer)
        {
            services.RemoveAll<IMessageConsumerRunner>();
            services.AddTransient<AzureServiceBusMessageConsumerRunner>(static sp => new AzureServiceBusMessageConsumerRunner(
                sp.GetRequiredService<IMessageReceiver>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IMessagingStartupValidator>(),
                sp.GetRequiredService<TcjAzureServiceBusOptions>(),
                sp.GetRequiredService<MessagingConsumerState>(),
                sp.GetRequiredService<TimeProvider>()));
            services.AddTransient<IMessageConsumerRunner>(static sp => sp.GetRequiredService<AzureServiceBusMessageConsumerRunner>());
        }
        services.TryAddSingleton<AzureServiceBusClientHealthCheck>(static sp => new AzureServiceBusClientHealthCheck(
            sp.GetRequiredService<AzureServiceBusClientManager>(),
            sp.GetRequiredService<TcjAzureServiceBusOptions>()));
        services.TryAddSingleton<AzureServiceBusSenderHealthCheck>(static sp => new AzureServiceBusSenderHealthCheck(
            sp.GetRequiredService<AzureServiceBusClientManager>()));
        services.TryAddSingleton<AzureServiceBusProcessorHealthCheck>(static sp => new AzureServiceBusProcessorHealthCheck(
            sp.GetRequiredService<TcjMessagingOptions>(),
            sp.GetRequiredService<MessagingConsumerState>()));
        services.TryAddSingleton<AzureServiceBusTopologyHealthCheck>(static sp => new AzureServiceBusTopologyHealthCheck(
            sp.GetRequiredService<IMessagingStartupValidator>()));
        services.TryAddSingleton<AzureServiceBusSessionProcessorHealthCheck>(static sp => new AzureServiceBusSessionProcessorHealthCheck(
            sp.GetRequiredService<TcjAzureServiceBusOptions>()));
        return services;
    }


    private sealed class AzureServiceBusHealthChecksMarker;

    private static string GetPackageVersion()
    {
        string? informational = typeof(AzureServiceBusServiceCollectionExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? "0.1.0-preview.5" : informational.Split('+', 2)[0];
    }
}
