using Azure.Messaging.ServiceBus;
using TCJ.Messaging.AzureServiceBus.Connections;
using TCJ.Messaging.Publishing;

namespace TCJ.Resilience.Tests;

public sealed class AzureServiceBusResilienceTests
{
    [Fact]
    [Trait("Category", "AzureServiceBus")]
    public void Service_busy_is_transient_throttle_without_adapter_owned_durable_retry()
    {
        var exception = new ServiceBusException(
            isTransient: true,
            message: "synthetic",
            entityName: "orders",
            reason: ServiceBusFailureReason.ServiceBusy);

        (PublishOutcome outcome, MessagingFailureCategory category, string failureType) =
            AzureServiceBusFailureClassifier.ClassifyPublish(exception);

        Assert.Equal(PublishOutcome.TransientFailure, outcome);
        Assert.Equal(MessagingFailureCategory.TransientThrottle, category);
        Assert.Equal("TransientThrottle", failureType);
    }

    [Fact]
    [Trait("Category", "AzureServiceBus")]
    public void Missing_entity_is_permanent_topology_failure()
    {
        var exception = new ServiceBusException(
            isTransient: false,
            message: "synthetic",
            entityName: "orders",
            reason: ServiceBusFailureReason.MessagingEntityNotFound);

        var classified = AzureServiceBusFailureClassifier.ClassifyPublish(exception);

        Assert.Equal(PublishOutcome.PermanentFailure, classified.Outcome);
        Assert.Equal(MessagingFailureCategory.PermanentTopology, classified.Category);
        Assert.Equal("PermanentTopology", classified.Type);
    }

    [Fact]
    [Trait("Category", "AzureServiceBus")]
    public void Service_timeout_is_classified_for_durable_outbox_retry()
    {
        var exception = new ServiceBusException(
            isTransient: true,
            message: "synthetic",
            entityName: "orders",
            reason: ServiceBusFailureReason.ServiceTimeout);

        var classified = AzureServiceBusFailureClassifier.ClassifyPublish(exception);

        Assert.Equal(PublishOutcome.TimedOut, classified.Outcome);
        Assert.Equal(MessagingFailureCategory.TransientTimeout, classified.Category);
        Assert.Equal("ServiceTimeout", classified.Type);
    }

    [Fact]
    [Trait("Category", "AzureServiceBus")]
    public void Oversized_payload_is_never_retryable()
    {
        var exception = new ServiceBusException(
            isTransient: false,
            message: "synthetic",
            entityName: "orders",
            reason: ServiceBusFailureReason.MessageSizeExceeded);

        var classified = AzureServiceBusFailureClassifier.ClassifyPublish(exception);

        Assert.Equal(PublishOutcome.PermanentFailure, classified.Outcome);
        Assert.Equal(MessagingFailureCategory.PayloadTooLarge, classified.Category);
        Assert.Equal("PayloadTooLarge", classified.Type);
    }

    [Theory]
    [InlineData(ServiceBusFailureReason.MessageLockLost)]
    [InlineData(ServiceBusFailureReason.SessionLockLost)]
    [Trait("Category", "AzureServiceBus")]
    public void Lock_loss_is_detected_explicitly(ServiceBusFailureReason reason)
    {
        var exception = new ServiceBusException(
            isTransient: false,
            message: "synthetic",
            entityName: "orders",
            reason: reason);

        Assert.True(AzureServiceBusFailureClassifier.IsLockLost(exception));
        Assert.Equal("LockLost", AzureServiceBusFailureClassifier.FailureType(exception));
    }
}
