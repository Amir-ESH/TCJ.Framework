using TCJ.Core.Inbox;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.Tests;

public sealed class InboxBridgeTests
{
    [Fact]
    public async Task Complete_occurs_only_after_inbox_pipeline_returns()
    {
        var gate = new TaskCompletionSource<InboxHandlingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new StubInboxPipeline((_, _) => gate.Task);
        var settlement = new RecordingSettlement();
        InboxTransportBridge bridge = CreateBridge(pipeline);
        Task<InboxTransportBridgeResult> processing = bridge.ProcessAsync(Received(TestServices.Envelope(), settlement));
        Assert.Equal(0, settlement.CompleteCount);
        gate.SetResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge));
        InboxTransportBridgeResult result = await processing;
        Assert.Equal(MessageSettlement.Complete, result.Settlement);
        Assert.Equal(1, settlement.CompleteCount);
    }

    [Fact]
    public async Task Duplicate_delivery_completes_without_retry()
    {
        var pipeline = new StubInboxPipeline((_, _) => Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.IgnoreDuplicate, IsDuplicate: true)));
        var settlement = new RecordingSettlement();
        InboxTransportBridgeResult result = await CreateBridge(pipeline).ProcessAsync(Received(TestServices.Envelope(), settlement));
        Assert.Equal(MessageSettlement.Complete, result.Settlement);
        Assert.Equal(1, settlement.CompleteCount);
        Assert.Equal(0, settlement.RetryCount);
    }

    [Fact]
    public async Task Retry_outcome_maps_to_retry_settlement()
    {
        var pipeline = new StubInboxPipeline((_, _) => Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.Retry, 2, InboxFailureType.TransientInfrastructure)));
        var settlement = new RecordingSettlement();
        InboxTransportBridgeResult result = await CreateBridge(pipeline).ProcessAsync(Received(TestServices.Envelope(), settlement));
        Assert.Equal(MessageSettlement.Retry, result.Settlement);
        Assert.Equal(1, settlement.RetryCount);
    }

    [Fact]
    public async Task Permanent_failure_dead_letters_when_supported()
    {
        var pipeline = new StubInboxPipeline((_, _) => Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.DeadLetter, 3, InboxFailureType.PermanentValidation)));
        var settlement = new RecordingSettlement();
        InboxTransportBridgeResult result = await CreateBridge(pipeline, supportsDeadLetter: true).ProcessAsync(Received(TestServices.Envelope(), settlement));
        Assert.Equal(MessageSettlement.DeadLetter, result.Settlement);
        Assert.Equal(1, settlement.DeadLetterCount);
    }

    [Fact]
    public async Task Permanent_failure_abandons_when_dead_letter_not_supported()
    {
        var pipeline = new StubInboxPipeline((_, _) => Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.DeadLetter, 3, InboxFailureType.PermanentValidation)));
        var settlement = new RecordingSettlement();
        InboxTransportBridgeResult result = await CreateBridge(pipeline, supportsDeadLetter: false).ProcessAsync(Received(TestServices.Envelope(), settlement));
        Assert.Equal(MessageSettlement.Abandon, result.Settlement);
        Assert.Equal(1, settlement.AbandonCount);
    }

    [Fact]
    public async Task Unsupported_content_type_is_dead_lettered_without_invoking_inbox()
    {
        int calls = 0;
        var pipeline = new StubInboxPipeline((_, _) => { calls++; return Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge)); });
        var settlement = new RecordingSettlement();
        InboxTransportBridgeResult result = await CreateBridge(pipeline).ProcessAsync(Received(TestServices.Envelope(contentType: "application/xml"), settlement));
        Assert.Equal(0, calls);
        Assert.Equal(MessageSettlement.DeadLetter, result.Settlement);
    }

    [Fact]
    public async Task Invalid_utf8_is_dead_lettered_without_invoking_inbox()
    {
        int calls = 0;
        var pipeline = new StubInboxPipeline((_, _) => { calls++; return Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge)); });
        var settlement = new RecordingSettlement();
        var envelope = new TCJ.Messaging.Envelopes.TransportMessageEnvelope("id", "test.message", 1, new byte[] { 0xC3, 0x28 }, "application/json", DateTimeOffset.UtcNow);
        InboxTransportBridgeResult result = await CreateBridge(pipeline).ProcessAsync(Received(envelope, settlement));
        Assert.Equal(0, calls);
        Assert.Equal(MessageSettlement.DeadLetter, result.Settlement);
    }

    [Fact]
    public async Task Forbidden_headers_are_removed_before_inbox_persistence()
    {
        var pipeline = new StubInboxPipeline((_, _) => Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge)));
        var settlement = new RecordingSettlement();
        var envelope = TestServices.Envelope(headers: new Dictionary<string, string>
        {
            ["authorization"] = "Bearer secret",
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        });
        await CreateBridge(pipeline).ProcessAsync(Received(envelope, settlement));
        Assert.NotNull(pipeline.LastEnvelope);
        Assert.False(pipeline.LastEnvelope!.Headers.ContainsKey("authorization"));
        Assert.True(pipeline.LastEnvelope.Headers.ContainsKey("traceparent"));
    }

    [Fact]
    public async Task Malformed_traceparent_is_ignored_before_inbox()
    {
        var pipeline = new StubInboxPipeline((_, _) => Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge)));
        await CreateBridge(pipeline).ProcessAsync(Received(TestServices.Envelope(headers: new Dictionary<string, string> { ["traceparent"] = "bad" }), new RecordingSettlement()));
        Assert.False(pipeline.LastEnvelope!.Headers.ContainsKey("traceparent"));
    }

    [Fact]
    public async Task Payload_limit_is_enforced_before_inbox()
    {
        int calls = 0;
        var pipeline = new StubInboxPipeline((_, _) => { calls++; return Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge)); });
        var settlement = new RecordingSettlement();
        var messagingOptions = new TcjMessagingOptions { MaximumPayloadBytes = 128 };
        InboxTransportBridge bridge = CreateBridge(pipeline, messagingOptions: messagingOptions);
        var envelope = new TCJ.Messaging.Envelopes.TransportMessageEnvelope("id", "test.message", 1, new byte[129], "application/json", DateTimeOffset.UtcNow);
        InboxTransportBridgeResult result = await bridge.ProcessAsync(Received(envelope, settlement));
        Assert.Equal(0, calls);
        Assert.Equal(MessageSettlement.DeadLetter, result.Settlement);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_false_settlement()
    {
        var pipeline = new StubInboxPipeline(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new InboxHandlingResult(InboxHandlingOutcome.Acknowledge);
        });
        var settlement = new RecordingSettlement();
        using var cts = new CancellationTokenSource();
        Task<InboxTransportBridgeResult> task = CreateBridge(pipeline).ProcessAsync(Received(TestServices.Envelope(), settlement), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, settlement.CompleteCount + settlement.RetryCount + settlement.DeadLetterCount + settlement.AbandonCount);
    }

    private static InboxTransportBridge CreateBridge(StubInboxPipeline pipeline, bool supportsDeadLetter = true, TcjMessagingOptions? messagingOptions = null)
    {
        var inboxOptions = new TcjInboxOptions { ConsumerName = "test-consumer" };
        return new InboxTransportBridge(
            pipeline,
            inboxOptions,
            messagingOptions ?? new TcjMessagingOptions(),
            new MessagingTransportDescriptor
            {
                Name = "test",
                Version = "1",
                Capabilities = new MessagingTransportCapabilities
                {
                    SupportsDeadLetter = supportsDeadLetter,
                    MaximumPayloadBytes = 16 * 1024 * 1024,
                    MaximumHeaderBytes = 64 * 1024
                }
            },
            new MessagingHeaderPolicy(messagingOptions ?? new TcjMessagingOptions()),
            TimeProvider.System);
    }

    private static ReceivedMessage Received(TCJ.Messaging.Envelopes.TransportMessageEnvelope envelope, RecordingSettlement settlement) =>
        new(envelope, new DeliveryContext("delivery-1", 1, DateTimeOffset.UtcNow, "source"), settlement);
}
