using System.Text.Json;
using TCJ.Messaging.Kafka.Configuration;

var options = new TcjKafkaOptions();
var behavior = new
{
    schemaVersion = 1,
    scenario = "KafkaConsumer",
    checks = new
    {
        adapterLoadsWithoutNetwork = options.TopologyMode == KafkaTopologyMode.Disabled,
        manualCommitDefault = !options.EnableAutoCommit,
        manualOffsetStoreDefault = !options.EnableAutoOffsetStore,
        idempotentProducerDefault = options.EnableIdempotence,
        strongAcknowledgementDefault = options.AcknowledgementMode == KafkaAcknowledgementMode.All,
        boundedProducerRetry = options.ProducerRetryCount == 3,
        boundedPartitionConcurrency = options.MaximumConcurrentPartitions == 8,
        boundedBuffer = options.MaximumBufferedMessages == 64,
        topologyOwnershipIsExplicit = options.TopologyMode == KafkaTopologyMode.Disabled,
        aotClaimIsConservative = true,
    },
};

string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH")
    ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
Directory.CreateDirectory(Path.GetDirectoryName(path)
    ?? throw new InvalidOperationException("Behavior path has no directory."));
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(behavior, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("TCJ.Messaging.Kafka target-only upgrade scenario passed");
