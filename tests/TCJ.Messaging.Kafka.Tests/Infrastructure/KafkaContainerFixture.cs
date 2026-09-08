using Confluent.Kafka;
using Testcontainers.Kafka;
namespace TCJ.Messaging.Kafka.Tests.Infrastructure;

public sealed class KafkaContainerFixture : IAsyncLifetime
{
    internal const string ContainerImage="confluentinc/cp-kafka:7.5.12";
    private KafkaContainer? _container;
    internal string BootstrapServers=>_container?.GetBootstrapAddress()??throw new InvalidOperationException("Kafka container has not started.");
    public async ValueTask InitializeAsync(){_container=new KafkaBuilder(ContainerImage).WithKRaft().WithLabel("tcj.kafka.integration","true").WithCleanUp(true).Build();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));await _container.StartAsync(timeout.Token).ConfigureAwait(false);using IAdminClient admin=new AdminClientBuilder(new AdminClientConfig{BootstrapServers=BootstrapServers}).Build();Metadata metadata=admin.GetMetadata(TimeSpan.FromSeconds(10));if(metadata.Brokers.Count==0)throw new InvalidOperationException("Kafka broker did not become ready.");}
    public async ValueTask DisposeAsync(){if(_container is not null)await _container.DisposeAsync().ConfigureAwait(false);_container=null;}
}
[CollectionDefinition(Name,DisableParallelization=true)]
public sealed class KafkaIntegrationCollection:ICollectionFixture<KafkaContainerFixture>{public const string Name="Kafka integration";}
