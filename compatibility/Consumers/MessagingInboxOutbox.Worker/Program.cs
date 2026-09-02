using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Outbox;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

var services = new ServiceCollection();
services.AddSingleton<CompatibilityOutboxContextAccessor>();
services.AddSingleton<IOutboxMessageContextAccessor>(sp => sp.GetRequiredService<CompatibilityOutboxContextAccessor>());
services.AddSingleton<IDomainEventDispatcher, CompatibilityDomainEventDispatcher>();
services.AddTcjMessaging();
services.AddTcjMessage("compat.outbox.message", 1, OutboxJsonContext.Default.CompatibilityDomainEvent);
services.AddTcjInMemoryMessaging();
services.AddTcjMessagingOutboxBridge();
await using ServiceProvider provider = services.BuildServiceProvider();

CompatibilityOutboxContextAccessor accessor = provider.GetRequiredService<CompatibilityOutboxContextAccessor>();
Guid messageId = Guid.NewGuid();
accessor.Current = new OutboxMessageContext(
    messageId,
    "compat.outbox.message.v1",
    1,
    "compat-correlation",
    "compat-causation");

IDomainEventDispatcher dispatcher = provider.GetRequiredService<IDomainEventDispatcher>();
await dispatcher.DispatchAsync([
    new CompatibilityDomainEvent("ok", DateTimeOffset.UtcNow)
]);

IMessageReceiver receiver = provider.GetRequiredService<IMessageReceiver>();
await using IAsyncEnumerator<ReceivedMessage> enumerator = receiver
    .ReceiveAsync(new ReceiveContext { Source = "compat.outbox.message.v1" })
    .GetAsyncEnumerator();
if (!await enumerator.MoveNextAsync())
    return 1;

ReceivedMessage received = enumerator.Current;
await received.Settlement.CompleteAsync();
if (received.Envelope.MessageId != messageId.ToString("D"))
    return 2;
Console.WriteLine("TCJ.Messaging Inbox/Outbox consumer passed");
return 0;

internal sealed record CompatibilityDomainEvent(string Value, DateTimeOffset OccurredOn) : IDomainEvent;

internal sealed class CompatibilityOutboxContextAccessor : IOutboxMessageContextAccessor
{
    public OutboxMessageContext? Current { get; set; }
}

internal sealed class CompatibilityDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(CompatibilityDomainEvent))]
internal sealed partial class OutboxJsonContext : JsonSerializerContext;
