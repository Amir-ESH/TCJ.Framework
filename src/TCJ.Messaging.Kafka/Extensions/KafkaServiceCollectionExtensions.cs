using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.Kafka.Configuration;
using TCJ.Messaging.Kafka.HealthChecks;
using TCJ.Messaging.Kafka.Publishing;
using TCJ.Messaging.Kafka.Receiving;
using TCJ.Messaging.Kafka.Topology;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.Kafka.Extensions;

/// <summary>Registers the Apache Kafka transport adapter for TCJ Messaging.</summary>
public static class KafkaServiceCollectionExtensions
{
    /// <summary>Registers one Kafka transport. <c>AddTcjMessaging</c> must be called first.</summary>
    public static IServiceCollection AddTcjKafka(this IServiceCollection services,Action<TcjKafkaOptions>? configure=null)
    {
        ArgumentNullException.ThrowIfNull(services); TcjMessagingOptions messaging=services.LastOrDefault(static x=>x.ServiceType==typeof(TcjMessagingOptions))?.ImplementationInstance as TcjMessagingOptions??throw new InvalidOperationException("AddTcjKafka must be called after AddTcjMessaging.");
        if(services.Any(static x=>x.ServiceType==typeof(IMessagingTransportPublisher)||x.ServiceType==typeof(MessagingTransportDescriptor)))throw new InvalidOperationException("A messaging transport is already registered. TCJ allows exactly one default transport registration.");
        var options=new TcjKafkaOptions();configure?.Invoke(options);services.AddSingleton(options);services.TryAddSingleton<KafkaMessageMapper>(static sp=>new KafkaMessageMapper(sp.GetRequiredService<MessagingHeaderPolicy>()));services.TryAddSingleton<KafkaProducerManager>(static sp=>new KafkaProducerManager(sp.GetRequiredService<TcjKafkaOptions>()));services.TryAddSingleton<KafkaTransportPublisher>(static sp=>new KafkaTransportPublisher(sp.GetRequiredService<KafkaProducerManager>(),sp.GetRequiredService<KafkaMessageMapper>(),sp.GetRequiredService<TcjKafkaOptions>()));services.AddSingleton<IMessagingTransportPublisher>(static sp=>sp.GetRequiredService<KafkaTransportPublisher>());services.AddSingleton<IMessagingTransportBatchPublisher>(static sp=>sp.GetRequiredService<KafkaTransportPublisher>());services.TryAddSingleton<KafkaMessageReceiver>(static sp=>new KafkaMessageReceiver(sp.GetRequiredService<TcjKafkaOptions>(),sp.GetRequiredService<KafkaMessageMapper>(),sp.GetRequiredService<KafkaTransportPublisher>(),sp.GetRequiredService<TimeProvider>()));services.AddSingleton<IMessageReceiver>(static sp=>sp.GetRequiredService<KafkaMessageReceiver>());services.TryAddSingleton<KafkaTopologyManager>(static sp=>new KafkaTopologyManager(sp.GetRequiredService<TcjKafkaOptions>()));services.TryAddSingleton<KafkaTransportHealthProbe>(static sp=>new KafkaTransportHealthProbe(sp.GetRequiredService<TcjKafkaOptions>()));services.AddSingleton<IMessagingTransportHealthProbe>(static sp=>sp.GetRequiredService<KafkaTransportHealthProbe>());
        services.AddSingleton(new MessagingTransportDescriptor{Name="Kafka",Version=GetPackageVersion(),Capabilities=new MessagingTransportCapabilities{SupportsBatchPublish=true,SupportsScheduling=false,SupportsTimeToLive=false,SupportsDeadLetter=true,SupportsDefer=false,SupportsOrderedDelivery=true,SupportsPartitioning=true,SupportsTransactions=false,SupportsPeekLock=false,OrderingGuarantee=MessagingOrderingGuarantee.PerPartition,MaximumPayloadBytes=1024*1024,MaximumHeaderBytes=64*1024,MaximumBatchSize=options.MaximumBatchSize}});
        services.TryAddSingleton<MessagingStartupValidator>();services.RemoveAll<IMessagingStartupValidator>();services.TryAddSingleton<KafkaStartupValidator>(static sp=>new KafkaStartupValidator(sp.GetRequiredService<MessagingStartupValidator>(),sp.GetRequiredService<KafkaTopologyManager>(),sp.GetRequiredService<TcjKafkaOptions>()));services.AddSingleton<IMessagingStartupValidator>(static sp=>sp.GetRequiredService<KafkaStartupValidator>());
        if(messaging.EnableConsumer){services.RemoveAll<IMessageConsumerRunner>();services.AddTransient<KafkaMessageConsumerRunner>(static sp=>new KafkaMessageConsumerRunner(sp.GetRequiredService<IMessageReceiver>(),sp.GetRequiredService<IServiceScopeFactory>(),sp.GetRequiredService<IMessagingStartupValidator>(),sp.GetRequiredService<TcjKafkaOptions>(),sp.GetRequiredService<MessagingConsumerState>(),sp.GetRequiredService<TimeProvider>()));services.AddTransient<IMessageConsumerRunner>(static sp=>sp.GetRequiredService<KafkaMessageConsumerRunner>());}services.TryAddSingleton<KafkaReadinessHealthCheck>(static sp=>new KafkaReadinessHealthCheck(sp.GetRequiredService<KafkaTransportHealthProbe>()));return services;
    }
    /// <summary>Registers Kafka readiness. Liveness should remain dependency-independent.</summary>
    public static IHealthChecksBuilder AddTcjKafkaHealthChecks(this IHealthChecksBuilder builder){ArgumentNullException.ThrowIfNull(builder);builder.AddCheck<KafkaReadinessHealthCheck>(TcjKafkaHealthCheckNames.Transport,HealthStatus.Unhealthy,tags:["ready","kafka"]);return builder;}
    private static string GetPackageVersion(){string? info=typeof(KafkaServiceCollectionExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;return string.IsNullOrWhiteSpace(info)?"0.1.0-preview.5":info.Split('+',2)[0];}
}
