using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Contracts;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Serialization;

var services = new ServiceCollection();
services.AddTcjMessaging();
services.AddTcjMessage("compat.contract", 1, ConsumerJsonContext.Default.ConsumerMessage);
using ServiceProvider provider = services.BuildServiceProvider();
MessagingMessageContract contract = provider.GetRequiredService<IMessageContractRegistry>().Resolve("compat.contract", 1);
GeneratedMessageContractSchema generated = new MessageContractSchemaGenerator().Generate(contract);
if (generated.Fingerprint.Algorithm != "SHA-256" || generated.Fingerprint.Value.Length != 64)
    return 1;

ReadOnlyMemory<byte> previous = """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":true}"""u8.ToArray();
ReadOnlyMemory<byte> breaking = """{"type":"object","properties":{"value":{"type":"string"},"requiredValue":{"type":"string"}},"required":["value","requiredValue"],"additionalProperties":true}"""u8.ToArray();
if (new MessageContractCompatibilityAnalyzer().Analyze(previous, breaking, MessageContractCompatibilityMode.Backward).Status != MessageContractCompatibilityStatus.Breaking)
    return 2;

Console.WriteLine("TCJ.Messaging.Contracts package-only consumer passed");
return 0;

internal sealed record ConsumerMessage(string Value);
[JsonSerializable(typeof(ConsumerMessage))]
internal sealed partial class ConsumerJsonContext : JsonSerializerContext;
