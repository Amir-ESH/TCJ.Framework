using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Connections;
using TCJ.Messaging.AzureServiceBus.Diagnostics;
using TCJ.Messaging.AzureServiceBus.Publishing;
using TCJ.Messaging.AzureServiceBus.Topology;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.AzureServiceBus.Receiving;

internal sealed class AzureServiceBusMessageReceiver : IMessageReceiver
{
    private readonly AzureServiceBusClientManager _clients;
    private readonly AzureServiceBusMessageMapper _mapper;
    private readonly TcjAzureServiceBusOptions _options;
    private readonly TimeProvider _timeProvider;

    internal AzureServiceBusMessageReceiver(AzureServiceBusClientManager clients, AzureServiceBusMessageMapper mapper,
        TcjAzureServiceBusOptions options, TimeProvider timeProvider)
    { _clients = clients; _mapper = mapper; _options = options; _timeProvider = timeProvider; }

    public async IAsyncEnumerable<ReceivedMessage> ReceiveAsync(ReceiveContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        AzureServiceBusValidation.ValidateEntityName(context.Source, nameof(context.Source));
        if (context.Subscription is not null) AzureServiceBusValidation.ValidateEntityName(context.Subscription, nameof(context.Subscription), 50);
        bool sessionRequired = IsSessionRequired(context);
        if (sessionRequired)
        {
            await foreach (ReceivedMessage message in ReceiveSessionsAsync(context, cancellationToken).ConfigureAwait(false))
                yield return message;
            yield break;
        }

        await using ServiceBusReceiver receiver = await _clients.CreateReceiverAsync(context.Source, context.Subscription, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            ServiceBusReceivedMessage? brokerMessage;
            try { brokerMessage = await receiver.ReceiveMessageAsync(_options.ReceiveWaitTime, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { yield break; }
            catch (Exception exception)
            {
                _clients.RecordFailure(exception);
                AzureServiceBusDiagnostics.ProcessorError();
                throw;
            }
            if (brokerMessage is null) continue;
            ReceivedMessage? mapped = await TryMapAsync(receiver, brokerMessage, context, cancellationToken).ConfigureAwait(false);
            if (mapped is not null) yield return mapped;
        }
    }

    private async IAsyncEnumerable<ReceivedMessage> ReceiveSessionsAsync(ReceiveContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int capacity = Math.Max(_options.MaximumConcurrentSessions * 2, Math.Min(512, Math.Max(1, _options.PrefetchCount)));
        var channel = Channel.CreateBounded<ReceivedMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        Task[] workers = Enumerable.Range(0, _options.MaximumConcurrentSessions)
            .Select(_ => SessionWorkerAsync(context, channel.Writer, cancellationToken)).ToArray();
        _ = CompleteChannelAsync(workers, channel.Writer);
        await foreach (ReceivedMessage message in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return message;
    }

    private async Task SessionWorkerAsync(ReceiveContext context, ChannelWriter<ReceivedMessage> writer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ServiceBusSessionReceiver? receiver = null;
            try
            {
                using Activity? accept = AzureServiceBusDiagnostics.Start(TcjAzureServiceBusDiagnosticNames.SessionAcceptActivity,
                    "session.accept", context.Source, context.Subscription is null ? "queue" : "subscription", sessionEnabled: true);
                receiver = await _clients.AcceptNextSessionAsync(context.Source, context.Subscription, cancellationToken).ConfigureAwait(false);
                AzureServiceBusDiagnostics.SessionAccepted();
                accept?.SetStatus(ActivityStatusCode.Ok);
                await using ServiceBusSessionReceiver sessionReceiver = receiver;
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        ServiceBusReceivedMessage? brokerMessage = await sessionReceiver.ReceiveMessageAsync(_options.ReceiveWaitTime, cancellationToken).ConfigureAwait(false);
                        if (brokerMessage is null) break;
                        ReceivedMessage? mapped = await TryMapAsync(sessionReceiver, brokerMessage, context, cancellationToken).ConfigureAwait(false);
                        if (mapped is null) continue;
                        await writer.WriteAsync(mapped, cancellationToken).ConfigureAwait(false);
                        if (mapped.Settlement is AzureServiceBusMessageSettlement settlement)
                            await settlement.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (ServiceBusException exception) when (exception.Reason == ServiceBusFailureReason.ServiceTimeout && !cancellationToken.IsCancellationRequested)
            {
                // No unlocked active session is currently available; bounded SDK TryTimeout owns this poll.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                _clients.RecordFailure(exception);
                AzureServiceBusDiagnostics.ProcessorError();
                await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (receiver is not null) AzureServiceBusDiagnostics.SessionReleased();
            }
        }
    }

    private async Task<ReceivedMessage?> TryMapAsync(ServiceBusReceiver receiver, ServiceBusReceivedMessage brokerMessage,
        ReceiveContext context, CancellationToken cancellationToken)
    {
        try
        {
            TransportMessageEnvelope envelope = _mapper.FromReceived(brokerMessage);
            DeliveryContext delivery = _mapper.ToDeliveryContext(brokerMessage, context);
            var settlement = new AzureServiceBusMessageSettlement(receiver, brokerMessage, envelope, context.Source,
                _clients, _mapper, _options, _timeProvider);
            AzureServiceBusDiagnostics.MessageReceived();
            using Activity? receive = AzureServiceBusDiagnostics.Start(TcjAzureServiceBusDiagnosticNames.ReceiveActivity, "receive", context.Source,
                context.Subscription is null ? "queue" : "subscription", !string.IsNullOrEmpty(brokerMessage.SessionId), message: envelope,
                kind: ActivityKind.Consumer, parentContext: AzureServiceBusDiagnostics.ExtractParent(envelope));
            receive?.SetStatus(ActivityStatusCode.Ok);
            return new ReceivedMessage(envelope, delivery, settlement);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            string reason = "PermanentSerialization";
            string description = "TCJ rejected invalid Azure Service Bus message metadata.";
            try { await receiver.DeadLetterMessageAsync(brokerMessage, reason, description, cancellationToken).ConfigureAwait(false); AzureServiceBusDiagnostics.MessageDeadLettered(); }
            catch (ServiceBusException settlementFailure) when (AzureServiceBusFailureClassifier.IsLockLost(settlementFailure)) { AzureServiceBusDiagnostics.LockLost(); }
            return null;
        }
    }

    private bool IsSessionRequired(ReceiveContext context)
    {
        if (context.Subscription is not null && _options.Topology.TryGetSubscription(context.Source, context.Subscription, out AzureServiceBusSubscriptionOptions? subscription))
            return subscription!.RequiresSession;
        if (context.Subscription is null && _options.Topology.TryGetQueue(context.Source, out AzureServiceBusQueueOptions? queue))
            return queue!.RequiresSession;
        return false;
    }

    private static async Task CompleteChannelAsync(Task[] workers, ChannelWriter<ReceivedMessage> writer)
    {
        try { await Task.WhenAll(workers).ConfigureAwait(false); writer.TryComplete(); }
        catch (Exception exception) { writer.TryComplete(exception); }
    }
}
