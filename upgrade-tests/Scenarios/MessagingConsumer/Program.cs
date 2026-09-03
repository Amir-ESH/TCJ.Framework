using System.Text.Json;
using TCJ.Messaging.Envelopes;

var sourceHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["x-upgrade"] = "original",
};

var envelope = new MessageEnvelope<UpgradeMessage>(
    messageId: "upgrade-message-001",
    messageType: "upgrade.messaging.sample",
    messageVersion: 1,
    message: new UpgradeMessage("payload"),
    createdAtUtc: DateTimeOffset.UnixEpoch,
    correlationId: "correlation-001",
    causationId: "causation-001",
    partitionKey: "partition-a",
    orderingKey: "order-a",
    headers: sourceHeaders);

sourceHeaders["x-upgrade"] = "mutated";

var behavior = new
{
    schemaVersion = 1,
    scenario = "MessagingConsumer",
    checks = new
    {
        stableMessageId = envelope.MessageId == "upgrade-message-001",
        logicalMessageType = envelope.MessageType == "upgrade.messaging.sample",
        positiveSchemaVersion = envelope.MessageVersion == 1,
        correlationPreserved = envelope.CorrelationId == "correlation-001",
        causationPreserved = envelope.CausationId == "causation-001",
        partitionHintPreserved = envelope.PartitionKey == "partition-a",
        orderingHintPreserved = envelope.OrderingKey == "order-a",
        immutableHeaders = envelope.Headers["x-upgrade"] == "original",
        jsonContentType = envelope.ContentType == "application/json",
    },
};

await WriteBehaviorAsync(behavior);
Console.WriteLine("TCJ.Messaging target-only upgrade scenario passed");

static async Task WriteBehaviorAsync<T>(T value)
{
    string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH")
        ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Behavior path has no directory."));
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

internal sealed record UpgradeMessage(string Value);
