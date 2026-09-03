using System.Collections.Concurrent;
using System.Text;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.InMemory;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Concurrency.Tests.Tests;

public sealed class MessagingStressTests
{
    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task Concurrent_publishers_and_consumers_preserve_all_message_ids_under_backpressure()
    {
        const int messageCount = 128;
        var options = new TcjMessagingOptions { MaximumBufferedMessages = 8 };
        var transport = new InMemoryMessagingTransport(options, TimeProvider.System);
        var received = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        Task consumer = ConsumeAsync();

        async Task ConsumeAsync()
        {
            await foreach (ReceivedMessage delivery in transport.ReceiveAsync(new ReceiveContext { Source = "stress" }))
            {
                Assert.True(received.TryAdd(delivery.Envelope.MessageId, 0));
                await delivery.Settlement.CompleteAsync();
                if (received.Count == messageCount)
                    break;
            }
        }

        Task<PublishResult>[] publishers = Enumerable.Range(0, messageCount)
            .Select(index => transport.PublishAsync(
                Envelope($"stress-{index:D4}"),
                new PublishContext { Destination = "stress" }))
            .ToArray();

        PublishResult[] results = await Task.WhenAll(publishers);
        await consumer;

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(messageCount, received.Count);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task Concurrent_duplicate_injection_keeps_logical_identity_stable()
    {
        var transport = new InMemoryMessagingTransport(
            new TcjMessagingOptions { MaximumBufferedMessages = 16 },
            TimeProvider.System);
        TransportMessageEnvelope envelope = Envelope("duplicate-stable");

        await transport.InjectDuplicateAsync(envelope, "duplicates");

        await using IAsyncEnumerator<ReceivedMessage> receiver = transport
            .ReceiveAsync(new ReceiveContext { Source = "duplicates" })
            .GetAsyncEnumerator();
        Assert.True(await receiver.MoveNextAsync());
        string first = receiver.Current.Envelope.MessageId;
        await receiver.Current.Settlement.CompleteAsync();
        Assert.True(await receiver.MoveNextAsync());
        string second = receiver.Current.Envelope.MessageId;
        await receiver.Current.Settlement.CompleteAsync();

        Assert.Equal("duplicate-stable", first);
        Assert.Equal(first, second);
    }

    private static TransportMessageEnvelope Envelope(string id) => new(
        id,
        "stress.message",
        1,
        Encoding.UTF8.GetBytes("{\"value\":1}"),
        "application/json",
        DateTimeOffset.UnixEpoch);
}
