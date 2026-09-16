using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Contracts;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Serialization;

var services = new ServiceCollection();
services.AddTcjMessaging();
services.AddTcjMessage("upgrade.contract", 1, UpgradeJsonContext.Default.UpgradeMessage);
using ServiceProvider provider = services.BuildServiceProvider();
MessagingMessageContract contract = provider.GetRequiredService<IMessageContractRegistry>().Resolve("upgrade.contract", 1);
GeneratedMessageContractSchema schema = new MessageContractSchemaGenerator().Generate(contract);
var analyzer = new MessageContractCompatibilityAnalyzer();
ReadOnlyMemory<byte> oldSchema = """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}"""u8.ToArray();
ReadOnlyMemory<byte> newSchema = """{"type":"object","properties":{"value":{"type":"string"},"note":{"type":["string","null"]}},"required":["value"],"additionalProperties":false}"""u8.ToArray();
var behavior = new
{
    schemaVersion = 1,
    scenario = "MessageContractsConsumer",
    checks = new
    {
        fingerprintAlgorithm = schema.Fingerprint.Algorithm == "SHA-256",
        fingerprintLength = schema.Fingerprint.Value.Length == 64,
        backwardCompatible = analyzer.Analyze(oldSchema, newSchema, MessageContractCompatibilityMode.Backward).Status == MessageContractCompatibilityStatus.Compatible,
        runtimeIdentity = contract.MessageType == "upgrade.contract" && contract.MessageVersion == 1
    }
};
string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH") ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(behavior, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("TCJ.Messaging.Contracts target-only upgrade scenario passed");

internal sealed record UpgradeMessage(string Value);
[JsonSerializable(typeof(UpgradeMessage))]
internal sealed partial class UpgradeJsonContext : JsonSerializerContext;
