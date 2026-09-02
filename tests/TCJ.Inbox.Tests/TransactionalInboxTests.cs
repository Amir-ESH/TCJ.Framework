using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Inbox;
using TCJ.Core.Outbox;
using TCJ.EntityFrameworkCore.Inbox;
using TCJ.EntityFrameworkCore.Inbox.Storage;

namespace TCJ.Inbox.Tests;

[Collection(InboxSqlServerCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "SqlServer")]
[Trait("Category", "Inbox")]
public sealed class TransactionalInboxTests(InboxSqlServerFixture fixture)
{
    [Fact]
    public async Task First_inline_delivery_commits_one_business_effect_and_outbox_message()
    {
        await fixture.ResetAsync();
        var envelope = fixture.Envelope("msg-first");
        InboxHandlingResult result = await Pipeline(fixture.InlineProvider).ProcessAsync(envelope);
        Assert.Equal(InboxHandlingOutcome.Acknowledge, result.Outcome);
        await using AsyncServiceScope scope = fixture.InlineProvider.CreateAsyncScope();
        InboxTestDbContext db = scope.ServiceProvider.GetRequiredService<InboxTestDbContext>();
        Assert.Equal(1, await db.BusinessRows.CountAsync());
        Assert.Equal(1, await db.Set<TCJ.EntityFrameworkCore.Outbox.OutboxMessage>().CountAsync());
        InboxMessage row = await db.Set<InboxMessage>().SingleAsync();
        Assert.Equal(InboxMessageStatus.Processed, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal(1, fixture.Behavior.Calls("msg-first"));
    }

    [Fact]
    public async Task Duplicate_after_success_does_not_invoke_handler_again()
    {
        await fixture.ResetAsync();
        var envelope = fixture.Envelope("msg-duplicate");
        Assert.Equal(InboxHandlingOutcome.Acknowledge, (await Pipeline(fixture.InlineProvider).ProcessAsync(envelope)).Outcome);
        InboxHandlingResult duplicate = await Pipeline(fixture.InlineProvider).ProcessAsync(envelope);
        Assert.Equal(InboxHandlingOutcome.IgnoreDuplicate, duplicate.Outcome);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(1, fixture.Behavior.Calls("msg-duplicate"));
        await AssertCountsAsync(fixture.InlineProvider, 1, 1, 1);
    }

    [Fact]
    public async Task Concurrent_duplicate_delivery_invokes_one_active_handler()
    {
        await fixture.ResetAsync();
        var envelope = fixture.Envelope("msg-concurrent");
        Task<InboxHandlingResult>[] calls = Enumerable.Range(0, 8).Select(_ => Pipeline(fixture.InlineProvider).ProcessAsync(envelope)).ToArray();
        InboxHandlingResult[] results = await Task.WhenAll(calls);
        Assert.Equal(1, results.Count(result => result.Outcome == InboxHandlingOutcome.Acknowledge));
        Assert.Equal(7, results.Count(result => result.Outcome == InboxHandlingOutcome.IgnoreDuplicate));
        Assert.Equal(1, fixture.Behavior.Calls("msg-concurrent"));
        await AssertCountsAsync(fixture.InlineProvider, 1, 1, 1);
    }

    [Fact]
    public async Task Same_identity_with_different_payload_fails_safely()
    {
        await fixture.ResetAsync();
        Assert.Equal(InboxHandlingOutcome.Acknowledge, (await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-conflict", "one"))).Outcome);
        InboxHandlingResult conflict = await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-conflict", "two"));
        Assert.Equal(InboxHandlingOutcome.DeadLetter, conflict.Outcome);
        Assert.Equal(InboxFailureType.PayloadConflict, conflict.FailureType);
        Assert.Equal(1, fixture.Behavior.Calls("msg-conflict"));
        await AssertCountsAsync(fixture.InlineProvider, 1, 1, 1);
        await using AsyncServiceScope scope = fixture.InlineProvider.CreateAsyncScope();
        InboxMessage row = await scope.ServiceProvider.GetRequiredService<InboxTestDbContext>().Set<InboxMessage>().SingleAsync();
        Assert.Equal(InboxFailureType.PayloadConflict.ToString(), row.LastErrorType);
        Assert.DoesNotContain("two", row.LastError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consumer_boundary_mismatch_is_rejected_before_persistence()
    {
        await fixture.ResetAsync();
        var envelope = new IncomingMessageEnvelope("msg-consumer", "test.command", 1, "other-api", "{}", fixture.Time.GetUtcNow());
        await Assert.ThrowsAsync<ArgumentException>(() => Pipeline(fixture.InlineProvider).ProcessAsync(envelope));
        await AssertCountsAsync(fixture.InlineProvider, 0, 0, 0);
    }

    [Fact]
    public async Task Unknown_message_type_is_dead_lettered_without_handler_invocation()
    {
        await fixture.ResetAsync();
        InboxHandlingResult result = await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-unknown-type", type: "unknown.command"));
        Assert.Equal(InboxHandlingOutcome.DeadLetter, result.Outcome);
        Assert.Equal(InboxFailureType.UnknownMessageType, result.FailureType);
        Assert.Equal(0, fixture.Behavior.Calls("msg-unknown-type"));
        await AssertStatusAsync(fixture.InlineProvider, InboxMessageStatus.DeadLettered);
    }

    [Fact]
    public async Task Unknown_message_version_is_dead_lettered_without_handler_invocation()
    {
        await fixture.ResetAsync();
        InboxHandlingResult result = await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-unknown-version", version: 2));
        Assert.Equal(InboxHandlingOutcome.DeadLetter, result.Outcome);
        Assert.Equal(InboxFailureType.UnknownMessageVersion, result.FailureType);
        Assert.Equal(0, fixture.Behavior.Calls("msg-unknown-version"));
        await AssertStatusAsync(fixture.InlineProvider, InboxMessageStatus.DeadLettered);
    }

    [Fact]
    public async Task Permanent_handler_failure_is_not_retried_automatically()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailPermanently("poison");
        InboxHandlingResult result = await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-poison", "poison"));
        Assert.Equal(InboxHandlingOutcome.DeadLetter, result.Outcome);
        await AssertStatusAsync(fixture.InlineProvider, InboxMessageStatus.DeadLettered);
        await AssertCountsAsync(fixture.InlineProvider, 1, 0, 0);
    }

    [Fact]
    public async Task Transient_handler_failure_is_scheduled_with_bounded_retry()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailTransiently("transient", 1);
        InboxHandlingResult result = await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-transient", "transient"));
        Assert.Equal(InboxHandlingOutcome.Retry, result.Outcome);
        await AssertStatusAsync(fixture.InlineProvider, InboxMessageStatus.RetryScheduled);
        await AssertCountsAsync(fixture.InlineProvider, 1, 0, 0);
    }

    [Fact]
    public async Task Transient_retry_eventually_commits_one_logical_result()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailTransiently("eventual", 1);
        var envelope = fixture.Envelope("msg-eventual", "eventual");
        Assert.Equal(InboxHandlingOutcome.Retry, (await Pipeline(fixture.InlineProvider).ProcessAsync(envelope)).Outcome);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(InboxHandlingOutcome.Acknowledge, (await Pipeline(fixture.InlineProvider).ProcessAsync(envelope)).Outcome);
        Assert.Equal(2, fixture.Behavior.Calls("msg-eventual"));
        await AssertCountsAsync(fixture.InlineProvider, 1, 1, 1);
    }

    [Fact]
    public async Task Failure_after_save_changes_rolls_back_business_inbox_processing_and_outbox()
    {
        await fixture.ResetAsync();
        fixture.Behavior.SaveThenFail("rollback");
        InboxHandlingResult result = await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-rollback", "rollback"));
        Assert.Equal(InboxHandlingOutcome.Retry, result.Outcome);
        await using AsyncServiceScope scope = fixture.InlineProvider.CreateAsyncScope();
        InboxTestDbContext db = scope.ServiceProvider.GetRequiredService<InboxTestDbContext>();
        Assert.Equal(0, await db.BusinessRows.CountAsync());
        Assert.Equal(0, await db.Set<TCJ.EntityFrameworkCore.Outbox.OutboxMessage>().CountAsync());
        InboxMessage row = await db.Set<InboxMessage>().SingleAsync();
        Assert.Equal(InboxMessageStatus.RetryScheduled, row.Status);
        Assert.Null(row.ProcessedAtUtc);
    }

    [Fact]
    public async Task Redelivery_after_uncertain_acknowledgement_is_detected_as_duplicate()
    {
        await fixture.ResetAsync();
        var envelope = fixture.Envelope("msg-ack");
        Assert.Equal(InboxHandlingOutcome.Acknowledge, (await Pipeline(fixture.InlineProvider).ProcessAsync(envelope)).Outcome);
        InboxHandlingResult redelivery = await Pipeline(fixture.InlineProvider).ProcessAsync(envelope);
        Assert.Equal(InboxHandlingOutcome.IgnoreDuplicate, redelivery.Outcome);
        await AssertCountsAsync(fixture.InlineProvider, 1, 1, 1);
    }

    [Fact]
    public async Task Cancellation_during_handler_rolls_back_and_remains_redeliverable()
    {
        await fixture.ResetAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Behavior.AfterCalled = _ => cancellation.Cancel();
        var envelope = fixture.Envelope("msg-cancel");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline(fixture.InlineProvider).ProcessAsync(envelope, cancellation.Token));
        await AssertCountsAsync(fixture.InlineProvider, 0, 0, 0);
        fixture.Behavior.AfterCalled = null;
        Assert.Equal(InboxHandlingOutcome.Acknowledge, (await Pipeline(fixture.InlineProvider).ProcessAsync(envelope)).Outcome);
        await AssertCountsAsync(fixture.InlineProvider, 1, 1, 1);
    }

    [Fact]
    public async Task Deferred_receive_commits_receipt_before_handler_execution()
    {
        await fixture.ResetAsync();
        InboxHandlingResult result = await Pipeline(fixture.DeferredProvider).ProcessAsync(fixture.Envelope("msg-deferred-receive"));
        Assert.Equal(InboxHandlingOutcome.Acknowledge, result.Outcome);
        Assert.Equal(0, fixture.Behavior.Calls("msg-deferred-receive"));
        await AssertStatusAsync(fixture.DeferredProvider, InboxMessageStatus.Received);
    }

    [Fact]
    public async Task Deferred_processor_commits_business_and_outbox_with_final_inbox_state()
    {
        await fixture.ResetAsync();
        await Pipeline(fixture.DeferredProvider).ProcessAsync(fixture.Envelope("msg-deferred-process"));
        InboxProcessingResult processed = await Processor(fixture.DeferredProvider).ProcessBatchAsync();
        Assert.Equal(1, processed.ProcessedCount);
        await AssertCountsAsync(fixture.DeferredProvider, 1, 1, 1);
        await AssertStatusAsync(fixture.DeferredProvider, InboxMessageStatus.Processed);
    }

    [Fact]
    public async Task Deferred_duplicate_before_processing_does_not_create_second_record()
    {
        await fixture.ResetAsync();
        var envelope = fixture.Envelope("msg-deferred-duplicate");
        await Pipeline(fixture.DeferredProvider).ProcessAsync(envelope);
        InboxHandlingResult duplicate = await Pipeline(fixture.DeferredProvider).ProcessAsync(envelope);
        Assert.Equal(InboxHandlingOutcome.IgnoreDuplicate, duplicate.Outcome);
        await using AsyncServiceScope scope = fixture.DeferredProvider.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<InboxTestDbContext>().Set<InboxMessage>().CountAsync());
    }

    [Fact]
    public async Task Deferred_transient_failure_retries_and_eventually_succeeds()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailTransiently("deferred-retry", 1);
        await Pipeline(fixture.DeferredProvider).ProcessAsync(fixture.Envelope("msg-deferred-retry", "deferred-retry"));
        Assert.Equal(1, (await Processor(fixture.DeferredProvider).ProcessBatchAsync()).RetryScheduledCount);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, (await Processor(fixture.DeferredProvider).ProcessBatchAsync()).ProcessedCount);
        await AssertCountsAsync(fixture.DeferredProvider, 1, 1, 1);
    }

    [Fact]
    public async Task Expired_deferred_lease_is_reclaimed_safely()
    {
        await fixture.ResetAsync();
        await Pipeline(fixture.DeferredProvider).ProcessAsync(fixture.Envelope("msg-lease"));
        await using (AsyncServiceScope scope = fixture.DeferredProvider.CreateAsyncScope())
        {
            IReadOnlyList<InboxMessage> claimed = await scope.ServiceProvider.GetRequiredService<IInboxStorage>().ClaimBatchAsync(fixture.Time.GetUtcNow(), default);
            Assert.Single(claimed);
        }
        fixture.Time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, (await Processor(fixture.DeferredProvider).ProcessBatchAsync()).ProcessedCount);
        Assert.Equal(1, fixture.Behavior.Calls("msg-lease"));
        await AssertCountsAsync(fixture.DeferredProvider, 1, 1, 1);
    }

    [Fact]
    public async Task Replay_preserves_original_identity_and_increments_replay_count()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailPermanently("replay");
        var envelope = fixture.Envelope("msg-replay", "replay");
        await Pipeline(fixture.InlineProvider).ProcessAsync(envelope);
        Guid id;
        await using (AsyncServiceScope scope = fixture.InlineProvider.CreateAsyncScope()) id = (await scope.ServiceProvider.GetRequiredService<InboxTestDbContext>().Set<InboxMessage>().SingleAsync()).Id;
        fixture.Behavior.Reset();
        InboxReplayResult replay = await fixture.InlineProvider.GetRequiredService<IInboxReplayService>().ReplayAsync(id);
        Assert.True(replay.Replayed);
        Assert.Equal(InboxHandlingOutcome.Acknowledge, (await Pipeline(fixture.InlineProvider).ProcessAsync(envelope)).Outcome);
        await using AsyncServiceScope verify = fixture.InlineProvider.CreateAsyncScope();
        InboxMessage row = await verify.ServiceProvider.GetRequiredService<InboxTestDbContext>().Set<InboxMessage>().SingleAsync();
        Assert.Equal(id, row.Id);
        Assert.Equal(1, row.ReplayCount);
    }

    [Fact]
    public async Task Replay_is_rejected_for_processed_message()
    {
        await fixture.ResetAsync();
        await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-replay-processed"));
        await using AsyncServiceScope scope = fixture.InlineProvider.CreateAsyncScope();
        Guid id = (await scope.ServiceProvider.GetRequiredService<InboxTestDbContext>().Set<InboxMessage>().SingleAsync()).Id;
        Assert.False((await fixture.InlineProvider.GetRequiredService<IInboxReplayService>().ReplayAsync(id)).Replayed);
    }

    [Fact]
    public async Task Cleanup_removes_only_old_processed_records()
    {
        await fixture.ResetAsync();
        await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-cleanup"));
        fixture.Time.Advance(TimeSpan.FromHours(2));
        InboxCleanupResult cleanup = await fixture.InlineProvider.GetRequiredService<IInboxCleanupService>().CleanupAsync();
        Assert.Equal(1, cleanup.DeletedCount);
        await AssertCountsAsync(fixture.InlineProvider, 0, 1, 0);
    }

    [Fact]
    public async Task Cleanup_preserves_retryable_and_dead_lettered_records()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailTransiently("retryable", 1);
        await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-cleanup-retry", "retryable"));
        fixture.Behavior.FailPermanently("dead-cleanup");
        await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-cleanup-dead", "dead-cleanup"));
        fixture.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, (await fixture.InlineProvider.GetRequiredService<IInboxCleanupService>().CleanupAsync()).DeletedCount);
        await using AsyncServiceScope scope = fixture.InlineProvider.CreateAsyncScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<InboxTestDbContext>().Set<InboxMessage>().CountAsync());
    }

    [Fact]
    public async Task Sensitive_headers_are_not_persisted_and_allowlisted_trace_header_is_retained()
    {
        await fixture.ResetAsync();
        var headers = new Dictionary<string, string> { ["authorization"] = "Bearer TCJ_SECRET_MARKER", ["api-key"] = "TCJ_SECRET_MARKER", ["traceparent"] = "00-11111111111111111111111111111111-2222222222222222-01" };
        await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-headers", headers: headers));
        await using AsyncServiceScope scope = fixture.InlineProvider.CreateAsyncScope();
        string json = (await scope.ServiceProvider.GetRequiredService<InboxTestDbContext>().Set<InboxMessage>().SingleAsync()).HeadersJson!;
        Assert.Contains("traceparent", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api-key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TCJ_SECRET_MARKER", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trace_context_from_allowlisted_headers_parents_consumer_activity()
    {
        await fixture.ResetAsync();
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "TCJ.EntityFrameworkCore",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "tcj.inbox.receive") stopped.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);

        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = "00-11111111111111111111111111111111-2222222222222222-01",
            ["tracestate"] = "vendor=value"
        };
        InboxHandlingResult result = await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-trace", headers: headers));

        Assert.Equal(InboxHandlingOutcome.Acknowledge, result.Outcome);
        Activity receive = Assert.Single(stopped);
        Assert.Equal("11111111111111111111111111111111", receive.TraceId.ToString());
        Assert.Equal("2222222222222222", receive.ParentSpanId.ToString());
    }

    [Fact]
    public async Task Malformed_trace_context_is_ignored_without_failing_message_processing()
    {
        await fixture.ResetAsync();
        var headers = new Dictionary<string, string> { ["traceparent"] = "malformed-trace-context" };
        InboxHandlingResult result = await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-bad-trace", headers: headers));
        Assert.Equal(InboxHandlingOutcome.Acknowledge, result.Outcome);
        await AssertCountsAsync(fixture.InlineProvider, 1, 1, 1);
    }

    [Fact]
    public async Task Correlation_and_inbound_identity_flow_to_outbox_metadata()
    {
        await fixture.ResetAsync();
        await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-causation", correlationId: "corr-123"));
        await using AsyncServiceScope scope = fixture.InlineProvider.CreateAsyncScope();
        TCJ.EntityFrameworkCore.Outbox.OutboxMessage outbox = await scope.ServiceProvider.GetRequiredService<InboxTestDbContext>().Set<TCJ.EntityFrameworkCore.Outbox.OutboxMessage>().SingleAsync();
        Assert.Equal("corr-123", outbox.CorrelationId);
        Assert.Equal("msg-causation", outbox.CausationId);

        await using AsyncServiceScope deliveryScope = fixture.InlineProvider.CreateAsyncScope();
        await deliveryScope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync();
        OutboxMessageContext delivered = Assert.IsType<OutboxMessageContext>(fixture.Behavior.LastOutboxContext);
        Assert.Equal("corr-123", delivered.CorrelationId);
        Assert.Equal("msg-causation", delivered.CausationId);
    }

    [Fact]
    public async Task Multiple_unique_messages_commit_without_loss()
    {
        await fixture.ResetAsync();
        InboxHandlingResult[] results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope($"msg-{index}", $"v-{index}"))));
        Assert.All(results, result => Assert.Equal(InboxHandlingOutcome.Acknowledge, result.Outcome));
        await AssertCountsAsync(fixture.InlineProvider, 12, 12, 12);
    }

    [Fact]
    public async Task Inbox_health_checks_return_only_aggregate_state()
    {
        await fixture.ResetAsync();
        await Pipeline(fixture.InlineProvider).ProcessAsync(fixture.Envelope("msg-health"));
        HealthReport report = await fixture.InlineProvider.GetRequiredService<HealthCheckService>().CheckHealthAsync(registration => registration.Tags.Contains("inbox"));
        Assert.NotEmpty(report.Entries);
        string rendered = string.Join(" ", report.Entries.SelectMany(entry => entry.Value.Data.Select(data => $"{data.Key}={data.Value}")));
        Assert.DoesNotContain("msg-health", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deferred_unknown_type_isolated_as_poison_message_and_later_message_continues()
    {
        await fixture.ResetAsync();
        await Pipeline(fixture.DeferredProvider).ProcessAsync(fixture.Envelope("msg-poison-unknown", type: "unknown.command"));
        await Pipeline(fixture.DeferredProvider).ProcessAsync(fixture.Envelope("msg-after-poison"));
        InboxProcessingResult result = await Processor(fixture.DeferredProvider).ProcessBatchAsync();
        Assert.Equal(1, result.DeadLetteredCount);
        Assert.Equal(1, result.ProcessedCount);
        Assert.Equal(1, fixture.Behavior.Calls("msg-after-poison"));
    }

    private static IInboxPipeline Pipeline(ServiceProvider provider) => provider.GetRequiredService<IInboxPipeline>();
    private static IInboxDeferredProcessor Processor(ServiceProvider provider) => provider.GetRequiredService<IInboxDeferredProcessor>();

    private static async Task AssertCountsAsync(ServiceProvider provider, int inbox, int business, int outbox)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        InboxTestDbContext db = scope.ServiceProvider.GetRequiredService<InboxTestDbContext>();
        Assert.Equal(inbox, await db.Set<InboxMessage>().CountAsync());
        Assert.Equal(business, await db.BusinessRows.CountAsync());
        Assert.Equal(outbox, await db.Set<TCJ.EntityFrameworkCore.Outbox.OutboxMessage>().CountAsync());
    }

    private static async Task AssertStatusAsync(ServiceProvider provider, InboxMessageStatus status)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        InboxMessage row = await scope.ServiceProvider.GetRequiredService<InboxTestDbContext>().Set<InboxMessage>().SingleAsync();
        Assert.Equal(status, row.Status);
    }
}
