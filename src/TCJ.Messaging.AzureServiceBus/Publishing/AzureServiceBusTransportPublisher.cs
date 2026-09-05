using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using TCJ.Messaging.AzureServiceBus.Connections;
using TCJ.Messaging.AzureServiceBus.Diagnostics;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Publishing;

namespace TCJ.Messaging.AzureServiceBus.Publishing;

internal sealed class AzureServiceBusTransportPublisher : IMessagingTransportPublisher, IMessagingTransportBatchPublisher
{
    private readonly AzureServiceBusClientManager _clients;
    private readonly AzureServiceBusMessageMapper _mapper;
    private readonly TimeProvider _timeProvider;

    internal AzureServiceBusTransportPublisher(AzureServiceBusClientManager clients, AzureServiceBusMessageMapper mapper, TimeProvider timeProvider)
    { _clients = clients; _mapper = mapper; _timeProvider = timeProvider; }

    public async Task<PublishResult> PublishAsync(TransportMessageEnvelope message, PublishContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        string destination = context.Destination ?? throw new ArgumentException("A resolved destination is required.", nameof(context));
        long started = _timeProvider.GetTimestamp();
        ActivityContext parent = AzureServiceBusDiagnostics.ExtractParent(message);
        bool scheduled = context.ScheduledAtUtc is not null;
        using Activity? activity = AzureServiceBusDiagnostics.Start(
            scheduled ? TcjAzureServiceBusDiagnosticNames.ScheduleActivity : TcjAzureServiceBusDiagnosticNames.PublishActivity,
            scheduled ? "schedule" : "publish", destination, message: message, parentContext: parent);
        try
        {
            ServiceBusMessage brokerMessage = _mapper.ToServiceBusMessage(message, context);
            ServiceBusSender sender = await _clients.GetSenderAsync(destination, cancellationToken).ConfigureAwait(false);
            if (context.ScheduledAtUtc is { } scheduledAt)
            {
                _ = await sender.ScheduleMessageAsync(brokerMessage, scheduledAt.ToUniversalTime(), cancellationToken).ConfigureAwait(false);
                AzureServiceBusDiagnostics.MessageScheduled();
            }
            else
            {
                await sender.SendMessageAsync(brokerMessage, cancellationToken).ConfigureAwait(false);
                AzureServiceBusDiagnostics.MessagePublished();
            }
            _clients.RecordSuccess();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return PublishResult.Published(brokerMessage.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Canceled");
            return new PublishResult(PublishOutcome.Canceled, FailureCategory: MessagingFailureCategory.Canceled, FailureType: "Canceled");
        }
        catch (ArgumentException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "PermanentSerialization");
            return new PublishResult(PublishOutcome.PermanentFailure, FailureCategory: MessagingFailureCategory.PermanentSerialization, FailureType: "PermanentSerialization");
        }
        catch (Exception exception)
        {
            _clients.RecordFailure(exception);
            (PublishOutcome outcome, MessagingFailureCategory category, string failureType) = AzureServiceBusFailureClassifier.ClassifyPublish(exception);
            activity?.SetTag(TcjAzureServiceBusDiagnosticNames.Tags.FailureType, failureType);
            activity?.SetStatus(ActivityStatusCode.Error, failureType);
            return new PublishResult(outcome, FailureCategory: category, FailureType: failureType);
        }
        finally { AzureServiceBusDiagnostics.RecordPublishDuration(_timeProvider.GetElapsedTime(started).TotalMilliseconds); }
    }

    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<TransportMessageEnvelope> messages, PublishContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(context);
        if (messages.Count == 0) return [];
        string destination = context.Destination ?? throw new ArgumentException("A resolved destination is required.", nameof(context));
        if (context.ScheduledAtUtc is not null)
        {
            var scheduledResults = new PublishResult[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    for (; i < messages.Count; i++) scheduledResults[i] = new PublishResult(PublishOutcome.Canceled, FailureCategory: MessagingFailureCategory.Canceled, FailureType: "Canceled");
                    break;
                }
                scheduledResults[i] = await PublishAsync(messages[i], context, cancellationToken).ConfigureAwait(false);
            }
            return scheduledResults;
        }

        ServiceBusSender sender;
        try { sender = await _clients.GetSenderAsync(destination, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception)
        {
            (PublishOutcome outcome, MessagingFailureCategory category, string failureType) = AzureServiceBusFailureClassifier.ClassifyPublish(exception);
            return Enumerable.Range(0, messages.Count).Select(_ => new PublishResult(outcome, FailureCategory: category, FailureType: failureType)).ToArray();
        }

        var results = new PublishResult[messages.Count];
        int index = 0;
        while (index < messages.Count)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                for (; index < messages.Count; index++)
                    results[index] = new PublishResult(PublishOutcome.Canceled, FailureCategory: MessagingFailureCategory.Canceled, FailureType: "Canceled");
                break;
            }
            ServiceBusMessageBatch batch;
            try { batch = await sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                for (; index < messages.Count; index++)
                    results[index] = new PublishResult(PublishOutcome.Canceled, FailureCategory: MessagingFailureCategory.Canceled, FailureType: "Canceled");
                break;
            }
            using (batch)
            {
            var batchIndices = new List<int>();
            while (index < messages.Count)
            {
                ServiceBusMessage brokerMessage;
                try { brokerMessage = _mapper.ToServiceBusMessage(messages[index], context); }
                catch (ArgumentException)
                {
                    results[index++] = new PublishResult(PublishOutcome.PermanentFailure, FailureCategory: MessagingFailureCategory.PermanentSerialization, FailureType: "PermanentSerialization");
                    continue;
                }
                if (!batch.TryAddMessage(brokerMessage))
                {
                    if (batchIndices.Count == 0)
                    {
                        results[index++] = new PublishResult(PublishOutcome.PermanentFailure, FailureCategory: MessagingFailureCategory.PayloadTooLarge, FailureType: "PayloadTooLarge");
                        continue;
                    }
                    break;
                }
                batchIndices.Add(index++);
            }
            if (batchIndices.Count == 0) continue;
            try
            {
                await sender.SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
                foreach (int itemIndex in batchIndices)
                {
                    results[itemIndex] = PublishResult.Published(messages[itemIndex].MessageId);
                    AzureServiceBusDiagnostics.MessagePublished();
                }
                _clients.RecordSuccess();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                foreach (int itemIndex in batchIndices)
                    results[itemIndex] = new PublishResult(PublishOutcome.Canceled, FailureCategory: MessagingFailureCategory.Canceled, FailureType: "Canceled");
                for (; index < messages.Count; index++)
                    results[index] = new PublishResult(PublishOutcome.Canceled, FailureCategory: MessagingFailureCategory.Canceled, FailureType: "Canceled");
                break;
            }
            catch (Exception exception)
            {
                _clients.RecordFailure(exception);
                (PublishOutcome outcome, MessagingFailureCategory category, string failureType) = AzureServiceBusFailureClassifier.ClassifyPublish(exception);
                foreach (int itemIndex in batchIndices)
                    results[itemIndex] = new PublishResult(outcome, FailureCategory: category, FailureType: failureType);
            }
            }
        }
        return results;
    }
}
