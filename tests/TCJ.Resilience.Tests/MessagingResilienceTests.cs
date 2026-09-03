using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.InMemory;
using TCJ.Messaging.Publishing;

namespace TCJ.Resilience.Tests;

public sealed class MessagingResilienceTests
{
    [Fact]
    public async Task Messaging_transient_transport_failure_is_returned_without_hidden_retry()
    {
        using ServiceProvider provider = CreateProvider();
        InMemoryMessagingTransport transport = provider.GetRequiredService<InMemoryMessagingTransport>();
        transport.EnqueuePublishResult(new PublishResult(
            PublishOutcome.TransientFailure,
            FailureCategory: MessagingFailureCategory.TransientConnection,
            FailureType: "TransientConnection"));

        PublishResult first = await provider.GetRequiredService<IMessagePublisher>().PublishAsync(
            Envelope("first"),
            new PublishContext { Destination = "resilience" });
        PublishResult second = await provider.GetRequiredService<IMessagePublisher>().PublishAsync(
            Envelope("second"),
            new PublishContext { Destination = "resilience" });

        Assert.Equal(PublishOutcome.TransientFailure, first.Outcome);
        Assert.True(first.IsRetryable);
        Assert.True(second.IsSuccess);
    }

    [Fact]
    public async Task Messaging_permanent_transport_failure_is_never_classified_retryable()
    {
        using ServiceProvider provider = CreateProvider();
        provider.GetRequiredService<InMemoryMessagingTransport>().EnqueuePublishResult(new PublishResult(
            PublishOutcome.PermanentFailure,
            FailureCategory: MessagingFailureCategory.PermanentTopology,
            FailureType: "PermanentTopology"));

        PublishResult result = await provider.GetRequiredService<IMessagePublisher>().PublishAsync(
            Envelope("permanent"),
            new PublishContext { Destination = "resilience" });

        Assert.Equal(PublishOutcome.PermanentFailure, result.Outcome);
        Assert.False(result.IsRetryable);
    }

    [Fact]
    public async Task Messaging_timeout_is_deterministic_and_classified_transient()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddTcjMessaging(options => options.PublishTimeout = TimeSpan.FromSeconds(5));
        services.AddTcjInMemoryMessaging();
        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<InMemoryMessagingTransport>().PublishDelay = TimeSpan.FromMinutes(1);

        Task<PublishResult> publish = provider.GetRequiredService<IMessagePublisher>().PublishAsync(
            Envelope("timeout"),
            new PublishContext { Destination = "resilience" });
        time.Advance(TimeSpan.FromSeconds(6));

        PublishResult result = await publish;
        Assert.Equal(PublishOutcome.TimedOut, result.Outcome);
        Assert.Equal(MessagingFailureCategory.TransientTimeout, result.FailureCategory);
        Assert.True(result.IsRetryable);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjInMemoryMessaging();
        return services.BuildServiceProvider();
    }

    private static TransportMessageEnvelope Envelope(string id) => new(
        id,
        "resilience.message",
        1,
        Encoding.UTF8.GetBytes("{\"value\":1}"),
        "application/json",
        DateTimeOffset.UnixEpoch);
}
