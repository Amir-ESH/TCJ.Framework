using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Kafka.Configuration;
using TCJ.Messaging.Kafka.Extensions;
using TCJ.Messaging.Kafka.Publishing;
using TCJ.Messaging.Publishing;
namespace TCJ.Messaging.Kafka.Tests;

public sealed class KafkaConfigurationTests
{
    [Fact] public void Defaults_are_fail_closed_and_production_safe(){var o=new TcjKafkaOptions();Assert.False(o.EnableAutoCommit);Assert.False(o.EnableAutoOffsetStore);Assert.True(o.EnableIdempotence);Assert.Equal(KafkaAcknowledgementMode.All,o.AcknowledgementMode);Assert.Equal(KafkaTopologyMode.Disabled,o.TopologyMode);Assert.Equal(5,o.MaximumProcessingAttempts);}
    [Fact] public void AddTcjKafka_requires_AddTcjMessaging(){var s=new ServiceCollection();Assert.Throws<InvalidOperationException>(()=>s.AddTcjKafka(o=>o.BootstrapServers="localhost:9092"));}
    [Fact] public void Duplicate_transport_registration_is_rejected(){var s=new ServiceCollection();s.AddTcjMessaging();s.AddTcjKafka(o=>o.BootstrapServers="localhost:9092");Assert.Throws<InvalidOperationException>(()=>s.AddTcjKafka(o=>o.BootstrapServers="localhost:9092"));}
    [Fact] public void Descriptor_matches_Kafka_contract(){var s=new ServiceCollection();s.AddTcjMessaging();s.AddTcjKafka(o=>o.BootstrapServers="localhost:9092");using var p=s.BuildServiceProvider();var d=p.GetRequiredService<MessagingTransportDescriptor>();Assert.Equal("Kafka",d.Name);Assert.True(d.Capabilities.SupportsBatchPublish);Assert.True(d.Capabilities.SupportsPartitioning);Assert.True(d.Capabilities.SupportsOrderedDelivery);Assert.False(d.Capabilities.SupportsTransactions);Assert.False(d.Capabilities.SupportsDefer);Assert.Equal(MessagingOrderingGuarantee.PerPartition,d.Capabilities.OrderingGuarantee);}
    [Fact] public async Task Registration_supports_service_provider_validation(){var s=new ServiceCollection();s.AddTcjMessaging();s.AddTcjKafka(o=>o.BootstrapServers="localhost:9092");await using var p=s.BuildServiceProvider(new ServiceProviderOptions{ValidateOnBuild=true,ValidateScopes=true});Assert.NotNull(p.GetRequiredService<IMessagePublisher>());}
    [Fact] public void PartitionKey_only_maps_to_key(){var m=Envelope(partition:"p");Assert.Equal("p",KafkaMessageMapper.ResolveKey(m,new PublishContext()));}
    [Fact] public void OrderingKey_only_maps_to_key(){var m=Envelope(ordering:"o");Assert.Equal("o",KafkaMessageMapper.ResolveKey(m,new PublishContext()));}
    [Fact] public void Conflicting_PartitionKey_and_OrderingKey_are_rejected(){var m=Envelope(partition:"p",ordering:"o");Assert.Throws<ArgumentException>(()=>KafkaMessageMapper.ResolveKey(m,new PublishContext()));}
    [Fact] public void Weak_configuration_is_rejected(){var o=new TcjKafkaOptions{BootstrapServers="localhost:9092",EnableIdempotence=false};Assert.Throws<ArgumentException>(()=>o.Validate());}
    private static TCJ.Messaging.Envelopes.TransportMessageEnvelope Envelope(string? partition=null,string? ordering=null)=>new("id","test.message",1,new byte[]{1},"application/json",DateTimeOffset.UtcNow,partitionKey:partition,orderingKey:ordering);
}
