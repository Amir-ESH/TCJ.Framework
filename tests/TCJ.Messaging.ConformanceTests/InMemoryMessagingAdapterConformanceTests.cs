using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.InMemory;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.ConformanceTests;

public sealed class InMemoryMessagingAdapterConformanceTests : MessagingAdapterConformanceTests
{
    protected override ValueTask<MessagingAdapterHarness> CreateHarnessAsync()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fakeTime);
        services.AddTcjMessaging(options =>
        {
            options.PublishTimeout = TimeSpan.FromSeconds(30);
            options.AdditionalAllowedHeaders.Add("custom-safe");
        });
        services.AddTcjInMemoryMessaging();
        ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        return ValueTask.FromResult(new MessagingAdapterHarness(
            provider.GetRequiredService<IMessagePublisher>(),
            provider.GetRequiredService<IMessageBatchPublisher>(),
            provider.GetRequiredService<IMessageReceiver>(),
            provider.GetRequiredService<MessagingTransportDescriptor>(),
            provider.GetRequiredService<IMessagingTransportHealthProbe>(),
            fakeTime,
            "conformance",
            provider));
    }

    protected override void EnqueuePublishResult(MessagingAdapterHarness harness, PublishResult result) =>
        GetTransport(harness).EnqueuePublishResult(result);

    protected override void SetPublishDelay(MessagingAdapterHarness harness, TimeSpan delay) =>
        GetTransport(harness).PublishDelay = delay;

    protected override void AdvanceTime(MessagingAdapterHarness harness, TimeSpan duration) =>
        ((FakeTimeProvider)harness.TimeProvider).Advance(duration);

    protected override Task InjectDuplicateAsync(
        MessagingAdapterHarness harness,
        TCJ.Messaging.Envelopes.TransportMessageEnvelope message,
        CancellationToken cancellationToken = default) =>
        GetTransport(harness).InjectDuplicateAsync(message, harness.Source, cancellationToken);

    protected override void SetAvailability(MessagingAdapterHarness harness, bool available) =>
        GetTransport(harness).IsAvailable = available;

    private static InMemoryMessagingTransport GetTransport(MessagingAdapterHarness harness)
    {
        var publisher = Assert.IsType<MessagePublisher>(harness.Publisher);
        _ = publisher;
        // The harness lifetime is the service provider; resolve through the receiver, which is the same singleton adapter.
        return Assert.IsType<InMemoryMessagingTransport>(harness.Receiver);
    }
}
