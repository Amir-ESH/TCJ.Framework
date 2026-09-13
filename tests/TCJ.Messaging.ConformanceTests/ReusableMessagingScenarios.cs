using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Publishing;

namespace TCJ.Messaging.ConformanceTests;

/// <summary>Composes the existing assertions for real brokers. Fault scenarios stay owned by adapter fixtures.</summary>
public static class ReusableMessagingScenarios
{
    public static MessagingAdapterConformanceTests Create(Func<ValueTask<MessagingAdapterHarness>> create) => new Scenarios(create);

private sealed class Scenarios(Func<ValueTask<MessagingAdapterHarness>> create) : MessagingAdapterConformanceTests
{
    protected override ValueTask<MessagingAdapterHarness> CreateHarnessAsync() => create();
    protected override void EnqueuePublishResult(MessagingAdapterHarness harness, PublishResult result) => throw new NotSupportedException("Use the adapter fault fixture.");
    protected override void SetPublishDelay(MessagingAdapterHarness harness, TimeSpan delay) => throw new NotSupportedException("Use the adapter timeout fixture.");
    protected override void AdvanceTime(MessagingAdapterHarness harness, TimeSpan duration) => throw new NotSupportedException("Use the adapter clock fixture.");
    protected override void SetAvailability(MessagingAdapterHarness harness, bool available) => throw new NotSupportedException("Use the adapter availability fixture.");
    protected override async Task InjectDuplicateAsync(MessagingAdapterHarness harness, TransportMessageEnvelope message, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < 2; i++)
            Assert.True((await harness.Publisher.PublishAsync(message, new PublishContext { Destination = harness.PublishDestination }, cancellationToken)).IsSuccess);
    }
}
}
