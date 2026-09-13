using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Publishing;
#if InMemory
using TCJ.Messaging.Receiving;
#elif RabbitMQ
using TCJ.Messaging.RabbitMQ.Extensions;
using TCJ.Messaging.RabbitMQ.Topology;
#elif AzureServiceBus
using TCJ.Messaging.AzureServiceBus.Extensions;
#elif Kafka
using TCJ.Messaging.Kafka.Extensions;
#endif

var services = new ServiceCollection();
services.AddTcjMessaging();
#if InMemory
services.AddTcjInMemoryMessaging();
#elif RabbitMQ
services.AddTcjRabbitMq(o => o.TopologyMode = RabbitMqTopologyMode.Disabled);
#elif AzureServiceBus
services.AddTcjAzureServiceBus("Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");
#elif Kafka
services.AddTcjKafka(o => o.BootstrapServers = "localhost:9092");
#endif
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
var descriptor = provider.GetRequiredService<MessagingTransportDescriptor>();
#if InMemory
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var envelope = new TransportMessageEnvelope("package-message", "package.message", 1, Encoding.UTF8.GetBytes("{\"value\":1}"), "application/json", DateTimeOffset.UtcNow);
var result = await provider.GetRequiredService<IMessagePublisher>().PublishAsync(envelope, new PublishContext { Destination = "package" }, cancellation.Token);
if (!result.IsSuccess) return 1;
await using var receiver = provider.GetRequiredService<IMessageReceiver>().ReceiveAsync(new ReceiveContext { Source = "package" }, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
if (!await receiver.MoveNextAsync() || receiver.Current.Envelope.MessageId != envelope.MessageId) return 1;
using var expected = JsonDocument.Parse(envelope.Body);
using var actual = JsonDocument.Parse(receiver.Current.Envelope.Body);
if (!JsonElement.DeepEquals(expected.RootElement, actual.RootElement)) return 1;
await receiver.Current.Settlement.CompleteAsync(cancellation.Token);
#endif
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
Console.WriteLine(JsonSerializer.Serialize(descriptor, options));
return 0;
