using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Inbox;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

var services = new ServiceCollection();
services.AddSingleton(new TcjInboxOptions { ConsumerName = "compat-messaging-worker" });
services.AddSingleton<IInboxPipeline, CompatibilityInboxPipeline>();
services.AddTcjMessaging(options => options.EnableConsumer = true);
services.AddTcjInMemoryMessaging();
await using ServiceProvider provider = services.BuildServiceProvider();

var envelope = new TransportMessageEnvelope(
    "compat-consumer-message",
    "compat.consumer.message",
    1,
    Encoding.UTF8.GetBytes("{\"value\":\"ok\"}"),
    "application/json",
    DateTimeOffset.UtcNow);

IMessagePublisher publisher = provider.GetRequiredService<IMessagePublisher>();
PublishResult published = await publisher.PublishAsync(
    envelope,
    new PublishContext { Destination = "compat-consumer" });
if (!published.IsSuccess)
    return 1;

IMessageReceiver receiver = provider.GetRequiredService<IMessageReceiver>();
await using IAsyncEnumerator<ReceivedMessage> enumerator = receiver
    .ReceiveAsync(new ReceiveContext { Source = "compat-consumer" })
    .GetAsyncEnumerator();
if (!await enumerator.MoveNextAsync())
    return 2;

InboxTransportBridge bridge = provider.GetRequiredService<InboxTransportBridge>();
InboxTransportBridgeResult handled = await bridge.ProcessAsync(enumerator.Current);
if (handled.Settlement != MessageSettlement.Complete)
    return 3;
Console.WriteLine("TCJ.Messaging consumer worker passed");
return 0;

internal sealed class CompatibilityInboxPipeline : IInboxPipeline
{
    public Task<InboxHandlingResult> ProcessAsync(
        IncomingMessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge, 1));
    }
}
