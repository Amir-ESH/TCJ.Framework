using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Publishing;

var services = new ServiceCollection();
services.AddTcjMessaging();
services.AddTcjMessage("compat.publisher.message", 1, PublisherJsonContext.Default.PublisherMessage);
services.AddTcjInMemoryMessaging();
await using ServiceProvider provider = services.BuildServiceProvider();

IMessagePublisher<PublisherMessage> publisher = provider.GetRequiredService<IMessagePublisher<PublisherMessage>>();
MessageEnvelope<PublisherMessage> envelope = MessageEnvelope<PublisherMessage>.Create(
    "compat.publisher.message",
    1,
    new PublisherMessage("ok"));
PublishResult result = await publisher.PublishAsync(envelope);
if (!result.IsSuccess)
    return 1;
Console.WriteLine("TCJ.Messaging publisher consumer passed");
return 0;

internal sealed record PublisherMessage(string Value);

[JsonSerializable(typeof(PublisherMessage))]
internal sealed partial class PublisherJsonContext : JsonSerializerContext;
