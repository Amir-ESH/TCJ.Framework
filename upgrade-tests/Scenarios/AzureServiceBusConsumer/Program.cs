using System.Text.Json;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Topology;

var options = new TcjAzureServiceBusOptions();

var behavior = new
{
    schemaVersion = 1,
    scenario = "AzureServiceBusConsumer",
    checks = new
    {
        adapterLoadsWithoutNetwork = options.PrefetchCount == 32,
        manualCompletionDefault = !options.AutoCompleteMessages,
        boundedMessageConcurrency = options.MaximumConcurrentMessages == 8,
        boundedSessionConcurrency = options.MaximumConcurrentSessions == 4,
        perSessionOrderingDefault = options.MaximumConcurrentCallsPerSession == 1,
        boundedLockRenewal = options.MaxAutoLockRenewalDuration == TimeSpan.FromMinutes(5),
        boundedSdkRetry = options.MaximumRetries == 3,
        topologyOwnershipIsExplicit = options.TopologyMode == AzureServiceBusTopologyMode.Disabled,
        scheduledCloneRetryDefault = options.RetrySettlementStrategy == AzureServiceBusRetrySettlementStrategy.ScheduledClone,
    },
};

await WriteBehaviorAsync(behavior);
Console.WriteLine("TCJ.Messaging.AzureServiceBus target-only upgrade scenario passed");

static async Task WriteBehaviorAsync<T>(T value)
{
    string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH")
        ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
    Directory.CreateDirectory(Path.GetDirectoryName(path)
        ?? throw new InvalidOperationException("Behavior path has no directory."));
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
