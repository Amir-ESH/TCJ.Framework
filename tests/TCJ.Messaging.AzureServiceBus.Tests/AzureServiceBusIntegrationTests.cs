using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.AzureServiceBus.HealthChecks;
using TCJ.Messaging.AzureServiceBus.Tests.Infrastructure;
using TCJ.Messaging.AzureServiceBus.Topology;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.AzureServiceBus.Tests;

public sealed class AzureServiceBusIntegrationTests
{
    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Queue_publish_and_receive_preserve_message_id_type_version_and_correlation()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue);
        PublishResult result = await Publish(provider, queue, Envelope("m1")); Assert.True(result.IsSuccess);
        ReceivedMessage received = await ReceiveOne(provider, queue); Assert.Equal("m1", received.Envelope.MessageId); Assert.Equal("tcj.test", received.Envelope.MessageType); Assert.Equal(1, received.Envelope.MessageVersion); Assert.Equal("corr", received.Envelope.CorrelationId); await received.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task PeekLock_manual_completion_removes_message_only_after_complete()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue);
        await Publish(provider, queue, Envelope("m2")); ReceivedMessage received = await ReceiveOne(provider, queue); Assert.NotNull(received.Delivery.LockExpiresAtUtc); await received.Settlement.CompleteAsync();
        await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue); Assert.Null(await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Abandon_redelivers_and_delivery_count_increases()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue);
        await Publish(provider, queue, Envelope("m3")); ReceivedMessage first = await ReceiveOne(provider, queue); int attempt = first.Delivery.DeliveryAttempt; await first.Settlement.AbandonAsync(); ReceivedMessage second = await ReceiveOne(provider, queue); Assert.Equal("m3", second.Envelope.MessageId); Assert.True(second.Delivery.DeliveryAttempt > attempt); await second.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task DeadLetter_moves_message_to_dead_letter_subqueue_without_payload_in_reason()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue);
        await Publish(provider, queue, Envelope("m4", body: "sensitive-payload")); ReceivedMessage received = await ReceiveOne(provider, queue); await received.Settlement.DeadLetterAsync(new DeadLetterOptions { Reason = "invalid-contract", Description = "bounded" });
        await using ServiceBusReceiver dlq = env.Client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter }); ServiceBusReceivedMessage? dead = await dlq.ReceiveMessageAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(dead); Assert.Equal("invalid-contract", dead.DeadLetterReason); Assert.DoesNotContain("sensitive-payload", dead.DeadLetterErrorDescription ?? string.Empty); await dlq.CompleteMessageAsync(dead);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Defer_removes_normal_delivery_and_explicit_sequence_retrieval_succeeds()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue, retryStrategy: AzureServiceBusRetrySettlementStrategy.Defer);
        await Publish(provider, queue, Envelope("m5")); ReceivedMessage received = await ReceiveOne(provider, queue); Assert.True(received.Delivery.SequenceNumber.HasValue); long sequence = received.Delivery.SequenceNumber.Value; await received.Settlement.DeferAsync();
        await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue); Assert.Null(await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1))); ServiceBusReceivedMessage deferred = await receiver.ReceiveDeferredMessageAsync(sequence); Assert.Equal("m5", deferred.MessageId); await receiver.CompleteMessageAsync(deferred);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Scheduled_delivery_is_not_available_early_and_preserves_stable_id()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue);
        PublishResult result = await provider.GetRequiredService<IMessagePublisher>().PublishAsync(Envelope("m6"), new PublishContext { Destination = queue, ScheduledAtUtc = DateTimeOffset.UtcNow.AddSeconds(2) }); Assert.True(result.IsSuccess);
        await using ServiceBusReceiver direct = env.Client.CreateReceiver(queue); Assert.Null(await direct.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500))); ServiceBusReceivedMessage? scheduled = await direct.ReceiveMessageAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(scheduled); Assert.Equal("m6", scheduled.MessageId); await direct.CompleteMessageAsync(scheduled);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Per_message_time_to_live_is_applied()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(deadLetterOnExpiration: true); await using ServiceProvider provider = CreateProvider(env, queue);
        PublishResult result = await provider.GetRequiredService<IMessagePublisher>().PublishAsync(Envelope("m7"), new PublishContext { Destination = queue, TimeToLive = TimeSpan.FromSeconds(1) });
        Assert.True(result.IsSuccess);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue);
        Assert.Null(await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1)));
        await using ServiceBusReceiver deadLetter = env.Client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        ServiceBusReceivedMessage? expired = await deadLetter.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(expired);
        Assert.Equal("m7", expired.MessageId);
        await deadLetter.CompleteMessageAsync(expired);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Broker_aware_batch_publish_preserves_input_identity()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue);
        IReadOnlyList<PublishResult> results = await provider.GetRequiredService<IMessageBatchPublisher>().PublishBatchAsync([Envelope("b1"), Envelope("b2"), Envelope("b3")], new PublishContext { Destination = queue }); Assert.All(results, static result => Assert.True(result.IsSuccess));
        await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue); IReadOnlyList<ServiceBusReceivedMessage> received = await receiver.ReceiveMessagesAsync(3, TimeSpan.FromSeconds(5)); Assert.Equal(3, received.Count); Assert.Equal(["b1", "b2", "b3"], received.Select(static x => x.MessageId).ToArray()); foreach (ServiceBusReceivedMessage item in received) await receiver.CompleteMessageAsync(item);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Oversized_batch_item_fails_permanently_without_republishing_valid_items()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue);
        TransportMessageEnvelope oversized = Envelope("oversized", new string('x', 1024 * 1024));
        TransportMessageEnvelope valid = Envelope("valid-after-oversized");

        IReadOnlyList<PublishResult> results = await provider.GetRequiredService<IMessageBatchPublisher>()
            .PublishBatchAsync([oversized, valid], new PublishContext { Destination = queue });

        Assert.Equal(2, results.Count);
        Assert.Equal(PublishOutcome.PermanentFailure, results[0].Outcome);
        Assert.Equal(MessagingFailureCategory.PayloadTooLarge, results[0].FailureCategory);
        Assert.True(results[1].IsSuccess);
        await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue);
        IReadOnlyList<ServiceBusReceivedMessage> received = await receiver.ReceiveMessagesAsync(2, TimeSpan.FromSeconds(5));
        Assert.Single(received);
        Assert.Equal("valid-after-oversized", received[0].MessageId);
        await receiver.CompleteMessageAsync(received[0]);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Duplicate_detection_suppresses_same_transport_message_id_within_window()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(duplicateDetection: true); await using ServiceBusSender sender = env.Client.CreateSender(queue); await sender.SendMessageAsync(AzureServiceBusIntegrationEnvironment.Message("dup-1")); await sender.SendMessageAsync(AzureServiceBusIntegrationEnvironment.Message("dup-1"));
        await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue); IReadOnlyList<ServiceBusReceivedMessage> received = await receiver.ReceiveMessagesAsync(2, TimeSpan.FromSeconds(2)); Assert.Single(received); await receiver.CompleteMessageAsync(received[0]);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Duplicate_detection_does_not_change_logical_message_identity()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(duplicateDetection: true); await using ServiceProvider provider = CreateProvider(env, queue); PublishResult result = await Publish(provider, queue, Envelope("logical-dup")); Assert.Equal("logical-dup", result.TransportMessageId); ReceivedMessage received = await ReceiveOne(provider, queue); Assert.Equal("logical-dup", received.Envelope.MessageId); await received.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Topic_and_subscription_receive_published_message()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        (string topic, string subscription) = await env.CreateTopicSubscriptionAsync(); await using ServiceProvider provider = CreateProvider(env, topic, subscription: subscription);
        await Publish(provider, topic, Envelope("t1")); ReceivedMessage received = await ReceiveOne(provider, topic, subscription); Assert.Equal("t1", received.Envelope.MessageId); await received.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Topic_subscription_manual_abandon_redelivers()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        (string topic, string subscription) = await env.CreateTopicSubscriptionAsync(); await using ServiceProvider provider = CreateProvider(env, topic, subscription: subscription);
        await Publish(provider, topic, Envelope("t2")); ReceivedMessage first = await ReceiveOne(provider, topic, subscription); await first.Settlement.AbandonAsync(); ReceivedMessage second = await ReceiveOne(provider, topic, subscription); Assert.Equal("t2", second.Envelope.MessageId); await second.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Session_queue_preserves_session_id_mapping()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(sessions: true); await using ServiceProvider provider = CreateProvider(env, queue, sessions: true);
        TransportMessageEnvelope envelope = Envelope("s1", orderingKey: "order-1"); await Publish(provider, queue, envelope, orderingKey: "order-1"); ReceivedMessage received = await ReceiveOne(provider, queue); Assert.Equal("order-1", received.Envelope.OrderingKey); await received.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Session_processing_order_is_single_in_flight_per_session()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(sessions: true); await using ServiceProvider provider = CreateProvider(env, queue, sessions: true);
        await Publish(provider, queue, Envelope("s2-1", orderingKey: "order-2"), orderingKey: "order-2"); await Publish(provider, queue, Envelope("s2-2", orderingKey: "order-2"), orderingKey: "order-2");
        await using IAsyncEnumerator<ReceivedMessage> e = provider.GetRequiredService<IMessageReceiver>().ReceiveAsync(new ReceiveContext { Source = queue }).GetAsyncEnumerator(); Assert.True(await e.MoveNextAsync()); Assert.Equal("s2-1", e.Current.Envelope.MessageId); await e.Current.Settlement.CompleteAsync(); Assert.True(await e.MoveNextAsync()); Assert.Equal("s2-2", e.Current.Envelope.MessageId); await e.Current.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Different_sessions_can_be_accepted_concurrently_with_bounded_limit()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(sessions: true); await using ServiceBusSender sender = env.Client.CreateSender(queue); await sender.SendMessagesAsync([new ServiceBusMessage("1") { MessageId = "cs1", SessionId = "a" }, new ServiceBusMessage("2") { MessageId = "cs2", SessionId = "b" }]);
        await using ServiceBusSessionReceiver a = await env.Client.AcceptSessionAsync(queue, "a"); await using ServiceBusSessionReceiver b = await env.Client.AcceptSessionAsync(queue, "b"); Assert.NotNull(await a.ReceiveMessageAsync(TimeSpan.FromSeconds(2))); Assert.NotNull(await b.ReceiveMessageAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Explicit_lock_renewal_keeps_peeklock_delivery_settleable()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceBusSender sender = env.Client.CreateSender(queue); await sender.SendMessageAsync(AzureServiceBusIntegrationEnvironment.Message("lock-renew")); await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue); ServiceBusReceivedMessage? message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(message); await receiver.RenewMessageLockAsync(message); await receiver.CompleteMessageAsync(message);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Completing_then_renewing_reports_lock_loss_or_invalid_operation()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceBusSender sender = env.Client.CreateSender(queue); await sender.SendMessageAsync(AzureServiceBusIntegrationEnvironment.Message("lock-loss")); await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue); ServiceBusReceivedMessage? message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(message); await receiver.CompleteMessageAsync(message); await Assert.ThrowsAnyAsync<Exception>(() => receiver.RenewMessageLockAsync(message));
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Trace_context_round_trips_through_application_properties()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); var envelope = Envelope("trace", headers: new Dictionary<string,string> { ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", ["tracestate"] = "vendor=value" }); await Publish(provider, queue, envelope); ReceivedMessage received = await ReceiveOne(provider, queue); Assert.Equal(envelope.Headers["traceparent"], received.Envelope.Headers["traceparent"]); Assert.Equal("vendor=value", received.Envelope.Headers["tracestate"]); await received.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task ReplyTo_header_maps_to_service_bus_reply_to_and_back()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); await Publish(provider, queue, Envelope("reply", headers: new Dictionary<string,string> { ["tcj-reply-to"] = "replies" })); ReceivedMessage received = await ReceiveOne(provider, queue); Assert.Equal("replies", received.Envelope.Headers["tcj-reply-to"]); await received.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Forbidden_headers_do_not_reach_broker_application_properties()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); await Publish(provider, queue, Envelope("secret-filter", headers: new Dictionary<string,string> { ["authorization"] = "secret-value", ["custom-safe"] = "ok" })); await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue); ServiceBusReceivedMessage? message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(message); Assert.False(message.ApplicationProperties.ContainsKey("authorization")); Assert.Equal("ok", message.ApplicationProperties["custom-safe"]); await receiver.CompleteMessageAsync(message);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Delivery_count_maps_to_transport_delivery_attempt()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); await Publish(provider, queue, Envelope("delivery-count")); ReceivedMessage first = await ReceiveOne(provider, queue); await first.Settlement.AbandonAsync(); ReceivedMessage second = await ReceiveOne(provider, queue); Assert.True(second.Delivery.DeliveryAttempt >= 2); await second.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Readiness_health_probe_opens_sender_link_without_publishing_message()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); Assert.True(await provider.GetRequiredService<IMessagingTransportHealthProbe>().IsReadyAsync()); await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue); Assert.Null(await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Topology_declare_creates_queue_idempotently()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = $"tcj-s48-declare-{Guid.NewGuid():N}"[..48]; await using ServiceProvider provider = CreateProvider(env, queue, topologyMode: AzureServiceBusTopologyMode.Declare); IMessagingStartupValidator validator = provider.GetRequiredService<IMessagingStartupValidator>(); await validator.ValidateAsync(); await validator.ValidateAsync(); Assert.True((await env.Administration.QueueExistsAsync(queue)).Value); await env.Administration.DeleteQueueAsync(queue);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Topology_validate_only_accepts_matching_queue()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue, topologyMode: AzureServiceBusTopologyMode.ValidateOnly); await provider.GetRequiredService<IMessagingStartupValidator>().ValidateAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Topology_validate_only_rejects_missing_queue_as_permanent_topology()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = $"tcj-s48-missing-{Guid.NewGuid():N}"[..48]; await using ServiceProvider provider = CreateProvider(env, queue, topologyMode: AzureServiceBusTopologyMode.ValidateOnly); InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IMessagingStartupValidator>().ValidateAsync()); Assert.Contains("PermanentTopology", exception.Message, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Topology_conflict_on_session_requirement_fails_safely()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(sessions: false); await using ServiceProvider provider = CreateProvider(env, queue, sessions: true, topologyMode: AzureServiceBusTopologyMode.ValidateOnly); InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IMessagingStartupValidator>().ValidateAsync()); Assert.Contains("PermanentTopology", exception.Message, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Scheduled_retry_clone_preserves_logical_id_and_completes_original_after_schedule()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue, retryStrategy: AzureServiceBusRetrySettlementStrategy.ScheduledClone); await Publish(provider, queue, Envelope("retry-logical")); ReceivedMessage first = await ReceiveOne(provider, queue); await first.Settlement.RetryAsync(new RetrySettlementOptions { Delay = TimeSpan.FromSeconds(1) }); await Task.Delay(TimeSpan.FromSeconds(2)); ReceivedMessage retry = await ReceiveOne(provider, queue); Assert.Equal("retry-logical", retry.Envelope.MessageId); Assert.NotEqual("retry-logical", retry.Delivery.DeliveryId); await retry.Settlement.CompleteAsync();
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Cancellation_stops_receive_poll_without_receive_and_delete_semantics()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)); await using IAsyncEnumerator<ReceivedMessage> e = provider.GetRequiredService<IMessageReceiver>().ReceiveAsync(new ReceiveContext { Source = queue }, cts.Token).GetAsyncEnumerator(cts.Token); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.MoveNextAsync().AsTask());
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Entity_deleted_after_registration_is_classified_as_permanent_topology_on_publish()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); await env.Administration.DeleteQueueAsync(queue); PublishResult result = await Publish(provider, queue, Envelope("missing-entity")); Assert.Equal(PublishOutcome.PermanentFailure, result.Outcome); Assert.Equal(MessagingFailureCategory.PermanentTopology, result.FailureCategory);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Batch_results_remain_in_input_order()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); TransportMessageEnvelope[] input = Enumerable.Range(0, 10).Select(i => Envelope($"order-{i:D2}")).ToArray(); IReadOnlyList<PublishResult> results = await provider.GetRequiredService<IMessageBatchPublisher>().PublishBatchAsync(input, new PublishContext { Destination = queue }); Assert.Equal(input.Select(static m => m.MessageId), results.Select(static r => r.TransportMessageId));
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Multiple_concurrent_publishers_reuse_transport_and_deliver_all_messages()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); IMessagePublisher publisher = provider.GetRequiredService<IMessagePublisher>(); PublishResult[] results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => publisher.PublishAsync(Envelope($"parallel-{i}"), new PublishContext { Destination = queue }))); Assert.All(results, static x => Assert.True(x.IsSuccess)); await using ServiceBusReceiver receiver = env.Client.CreateReceiver(queue); IReadOnlyList<ServiceBusReceivedMessage> received = await receiver.ReceiveMessagesAsync(16, TimeSpan.FromSeconds(5)); Assert.Equal(16, received.Count); foreach (ServiceBusReceivedMessage item in received) await receiver.CompleteMessageAsync(item);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Graceful_receiver_disposal_leaves_unsettled_message_available_for_redelivery()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); await Publish(provider, queue, Envelope("shutdown")); await using (IAsyncEnumerator<ReceivedMessage> e = provider.GetRequiredService<IMessageReceiver>().ReceiveAsync(new ReceiveContext { Source = queue }).GetAsyncEnumerator()) { Assert.True(await e.MoveNextAsync()); Assert.Equal("shutdown", e.Current.Envelope.MessageId); }
        await using ServiceBusReceiver direct = env.Client.CreateReceiver(queue); ServiceBusReceivedMessage? redelivered = await direct.ReceiveMessageAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(redelivered); await direct.CompleteMessageAsync(redelivered);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Session_lock_can_be_renewed_explicitly_and_message_completed_once()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(sessions: true); await using ServiceBusSender sender = env.Client.CreateSender(queue); await sender.SendMessageAsync(new ServiceBusMessage("{}") { MessageId = "session-renew", SessionId = "session-r" }); await using ServiceBusSessionReceiver receiver = await env.Client.AcceptSessionAsync(queue, "session-r"); ServiceBusReceivedMessage? message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5)); Assert.NotNull(message); await receiver.RenewSessionLockAsync(); await receiver.CompleteMessageAsync(message);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Topic_subscription_delivery_does_not_duplicate_without_multiple_subscriptions()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        (string topic, string subscription) = await env.CreateTopicSubscriptionAsync(); await using ServiceBusSender sender = env.Client.CreateSender(topic); await sender.SendMessageAsync(AzureServiceBusIntegrationEnvironment.Message("topic-single")); await using ServiceBusReceiver receiver = env.Client.CreateReceiver(topic, subscription); IReadOnlyList<ServiceBusReceivedMessage> messages = await receiver.ReceiveMessagesAsync(2, TimeSpan.FromSeconds(2)); Assert.Single(messages); await receiver.CompleteMessageAsync(messages[0]);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Queue_property_duplicate_detection_expectation_is_validated()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(duplicateDetection: true); await using ServiceProvider provider = CreateProvider(env, queue, duplicateDetection: true, topologyMode: AzureServiceBusTopologyMode.ValidateOnly); await provider.GetRequiredService<IMessagingStartupValidator>().ValidateAsync(); QueueProperties properties = (await env.Administration.GetQueueAsync(queue)).Value; Assert.True(properties.RequiresDuplicateDetection);
    }

    [Fact, Trait("Category", "AzureServiceBusIntegration")]
    public async Task Liveness_contract_is_not_coupled_to_service_bus_readiness_probe()
    {
        await using AzureServiceBusIntegrationEnvironment? env = await AzureServiceBusIntegrationEnvironment.CreateAsync(); if (env is null) return;
        string queue = await env.CreateQueueAsync(); await using ServiceProvider provider = CreateProvider(env, queue); Assert.True(await provider.GetRequiredService<IMessagingTransportHealthProbe>().IsReadyAsync()); Assert.Equal("tcj.azure_service_bus.client", TcjAzureServiceBusHealthCheckNames.Client);
    }

    private static ServiceProvider CreateProvider(AzureServiceBusIntegrationEnvironment env, string entity, string? subscription = null,
        bool sessions = false, bool duplicateDetection = false, AzureServiceBusTopologyMode topologyMode = AzureServiceBusTopologyMode.Disabled,
        AzureServiceBusRetrySettlementStrategy retryStrategy = AzureServiceBusRetrySettlementStrategy.ScheduledClone)
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging(options =>
        {
            options.MaximumConcurrentMessages = 8;
            options.AdditionalAllowedHeaders.Add("custom-safe");
        });
        services.AddTcjAzureServiceBus(env.ConnectionString, options =>
        {
            options.ManagementConnectionString = env.ManagementConnectionString;
            options.TopologyMode = topologyMode;
            options.ReadinessDestination = entity;
            options.PrefetchCount = 8;
            options.MaximumConcurrentMessages = 8;
            options.MaximumConcurrentSessions = 4;
            options.MaximumConcurrentCallsPerSession = 1;
            options.RetrySettlementStrategy = retryStrategy;
            if (subscription is null)
                options.Topology.Queues.Add(new AzureServiceBusQueueOptions { Name = entity, RequiresSession = sessions, RequiresDuplicateDetection = duplicateDetection, DuplicateDetectionHistoryTimeWindow = duplicateDetection ? TimeSpan.FromSeconds(20) : null });
            else
            {
                options.Topology.Topics.Add(new AzureServiceBusTopicOptions { Name = entity });
                options.Topology.Subscriptions.Add(new AzureServiceBusSubscriptionOptions { TopicName = entity, SubscriptionName = subscription, RequiresSession = sessions });
            }
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static TransportMessageEnvelope Envelope(string id, string body = "{}", string? orderingKey = null, IReadOnlyDictionary<string,string>? headers = null) =>
        new(id, "tcj.test", 1, System.Text.Encoding.UTF8.GetBytes(body), "application/json", DateTimeOffset.UtcNow, "corr", "cause", orderingKey: orderingKey, headers: headers);

    private static Task<PublishResult> Publish(ServiceProvider provider, string destination, TransportMessageEnvelope envelope, string? orderingKey = null) =>
        provider.GetRequiredService<IMessagePublisher>().PublishAsync(envelope, new PublishContext { Destination = destination, OrderingKey = orderingKey });

    private static async Task<ReceivedMessage> ReceiveOne(ServiceProvider provider, string source, string? subscription = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using IAsyncEnumerator<ReceivedMessage> e = provider.GetRequiredService<IMessageReceiver>().ReceiveAsync(new ReceiveContext { Source = source, Subscription = subscription }, cts.Token).GetAsyncEnumerator(cts.Token);
        if (!await e.MoveNextAsync()) throw new InvalidOperationException("Expected Azure Service Bus delivery.");
        return e.Current;
    }
}
