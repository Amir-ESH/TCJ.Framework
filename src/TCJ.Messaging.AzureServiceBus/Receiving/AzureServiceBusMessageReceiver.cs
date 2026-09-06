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
        if (context.Subscription is not null)
            AzureServiceBusValidation.ValidateEntityName(context.Subscription, nameof(context.Subscription), 50);

        if (IsSessionRequired(context))
        {
            await foreach (ReceivedMessage message in ReceiveSessionsAsync(context, cancellationToken).ConfigureAwait(false))
                yield return message;
            yield break;
        }

        await using ServiceBusReceiver receiver = await _clients
            .CreateReceiverAsync(context.Source, context.Subscription, cancellationToken)
            .ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            ServiceBusReceivedMessage? brokerMessage;
            try
            {
                brokerMessage = await receiver
                    .ReceiveMessageAsync(_options.ReceiveWaitTime, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _clients.RecordFailure(exception);
                AzureServiceBusDiagnostics.ProcessorError();
                throw;
            }

            if (brokerMessage is null)
                continue;

            ReceivedMessage? mapped = await TryMapAsync(receiver, brokerMessage, context, cancellationToken)
                .ConfigureAwait(false);
            if (mapped is not null)
                yield return mapped;
        }
    }

    private async IAsyncEnumerable<ReceivedMessage> ReceiveSessionsAsync(
        ReceiveContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int capacity = Math.Max(
            _options.MaximumConcurrentSessions * 2,
            Math.Min(512, Math.Max(1, _options.PrefetchCount)));

        var channel = Channel.CreateBounded<ReceivedMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ServiceBusSessionProcessor processor = await CreateSessionProcessorAsync(context, cancellationToken)
            .ConfigureAwait(false);

        processor.ProcessMessageAsync += args =>
            ProcessSessionMessageAsync(args, context, channel.Writer, receiveCancellation.Token);
        processor.ProcessErrorAsync += args => ProcessSessionErrorAsync(args, channel.Writer);
        processor.SessionInitializingAsync += args =>
        {
            using Activity? activity = AzureServiceBusDiagnostics.Start(
                TcjAzureServiceBusDiagnosticNames.SessionAcceptActivity,
                "session.accept",
                context.Source,
                context.Subscription is null ? "queue" : "subscription",
                sessionEnabled: true);
            AzureServiceBusDiagnostics.SessionAccepted();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return Task.CompletedTask;
        };
        processor.SessionClosingAsync += _ =>
        {
            AzureServiceBusDiagnostics.SessionReleased();
            return Task.CompletedTask;
        };

        try
        {
            await processor.StartProcessingAsync(cancellationToken).ConfigureAwait(false);

            await foreach (ReceivedMessage message in channel.Reader
                .ReadAllAsync(receiveCancellation.Token)
                .ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            receiveCancellation.Cancel();
            channel.Writer.TryComplete();

            Task stopTask = processor.StopProcessingAsync(CancellationToken.None);
            try
            {
                await stopTask
                    .WaitAsync(_options.ShutdownTimeout, _timeProvider, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = ObserveDetachedAsync(stopTask);
            }

            Task closeTask = processor.CloseAsync(CancellationToken.None);
            try
            {
                await closeTask
                    .WaitAsync(_options.ShutdownTimeout, _timeProvider, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = ObserveDetachedAsync(closeTask);
            }
        }
    }

    private async ValueTask<ServiceBusSessionProcessor> CreateSessionProcessorAsync(
        ReceiveContext context,
        CancellationToken cancellationToken)
    {
        ServiceBusClient client = await _clients.GetClientAsync(cancellationToken).ConfigureAwait(false);
        var options = new ServiceBusSessionProcessorOptions
        {
            AutoCompleteMessages = false,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            PrefetchCount = _options.PrefetchCount,
            MaxConcurrentSessions = _options.MaximumConcurrentSessions,
            MaxConcurrentCallsPerSession = _options.MaximumConcurrentCallsPerSession,
            // TCJ owns explicit bounded session-lock renewal so that lock loss and renewal
            // telemetry use the same settlement lifecycle as non-session Peek-Lock messages.
            MaxAutoLockRenewalDuration = TimeSpan.Zero,
            SessionIdleTimeout = _options.ReceiveWaitTime
        };

        return context.Subscription is null
            ? client.CreateSessionProcessor(context.Source, options)
            : client.CreateSessionProcessor(context.Source, context.Subscription, options);
    }

    private async Task ProcessSessionMessageAsync(
        ProcessSessionMessageEventArgs args,
        ReceiveContext context,
        ChannelWriter<ReceivedMessage> writer,
        CancellationToken receiveCancellation)
    {
        ReceivedMessage? mapped = await TryMapSessionAsync(args, context).ConfigureAwait(false);
        if (mapped is null)
            return;

        var settlement = (AzureServiceBusSessionMessageSettlement)mapped.Settlement;
        try
        {
            await writer.WriteAsync(mapped, receiveCancellation).ConfigureAwait(false);
            await settlement.Completion.WaitAsync(args.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (receiveCancellation.IsCancellationRequested ||
                  args.CancellationToken.IsCancellationRequested)
        {
            await settlement.StopRenewalAsync().ConfigureAwait(false);
        }
    }

    private async Task<ReceivedMessage?> TryMapSessionAsync(
        ProcessSessionMessageEventArgs args,
        ReceiveContext context)
    {
        try
        {
            TransportMessageEnvelope envelope = _mapper.FromReceived(args.Message);
            DeliveryContext delivery = _mapper.ToDeliveryContext(args.Message, context);
            var settlement = new AzureServiceBusSessionMessageSettlement(
                args,
                args.Message,
                envelope,
                context.Source,
                _clients,
                _mapper,
                _options,
                _timeProvider);

            AzureServiceBusDiagnostics.MessageReceived();
            using Activity? receive = AzureServiceBusDiagnostics.Start(
                TcjAzureServiceBusDiagnosticNames.ReceiveActivity,
                "receive",
                context.Source,
                context.Subscription is null ? "queue" : "subscription",
                sessionEnabled: true,
                message: envelope,
                kind: ActivityKind.Consumer,
                parentContext: AzureServiceBusDiagnostics.ExtractParent(envelope));
            receive?.SetStatus(ActivityStatusCode.Ok);

            return new ReceivedMessage(envelope, delivery, settlement);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            const string reason = "PermanentSerialization";
            const string description =
                "TCJ rejected invalid Azure Service Bus message metadata.";

            try
            {
                await args.DeadLetterMessageAsync(
                    args.Message,
                    reason,
                    description,
                    args.CancellationToken).ConfigureAwait(false);
                AzureServiceBusDiagnostics.MessageDeadLettered();
            }
            catch (ServiceBusException settlementFailure)
                when (AzureServiceBusFailureClassifier.IsLockLost(settlementFailure))
            {
                AzureServiceBusDiagnostics.LockLost();
            }

            return null;
        }
    }

    private Task ProcessSessionErrorAsync(
        ProcessErrorEventArgs args,
        ChannelWriter<ReceivedMessage> writer)
    {
        _clients.RecordFailure(args.Exception);
        AzureServiceBusDiagnostics.ProcessorError();

        // The SDK owns retry for transient processor errors. Permanent/unexpected
        // failures must reach the neutral receiver instead of leaving it waiting
        // indefinitely on an empty channel.
        if (args.Exception is not ServiceBusException { IsTransient: true })
            writer.TryComplete(args.Exception);

        return Task.CompletedTask;
    }

    private async Task<ReceivedMessage?> TryMapAsync(
        ServiceBusReceiver receiver,
        ServiceBusReceivedMessage brokerMessage,
        ReceiveContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            TransportMessageEnvelope envelope = _mapper.FromReceived(brokerMessage);
            DeliveryContext delivery = _mapper.ToDeliveryContext(brokerMessage, context);
            var settlement = new AzureServiceBusMessageSettlement(
                receiver,
                brokerMessage,
                envelope,
                context.Source,
                _clients,
                _mapper,
                _options,
                _timeProvider);

            AzureServiceBusDiagnostics.MessageReceived();
            using Activity? receive = AzureServiceBusDiagnostics.Start(
                TcjAzureServiceBusDiagnosticNames.ReceiveActivity,
                "receive",
                context.Source,
                context.Subscription is null ? "queue" : "subscription",
                !string.IsNullOrEmpty(brokerMessage.SessionId),
                message: envelope,
                kind: ActivityKind.Consumer,
                parentContext: AzureServiceBusDiagnostics.ExtractParent(envelope));
            receive?.SetStatus(ActivityStatusCode.Ok);
            return new ReceivedMessage(envelope, delivery, settlement);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            const string reason = "PermanentSerialization";
            const string description = "TCJ rejected invalid Azure Service Bus message metadata.";
            try
            {
                await receiver
                    .DeadLetterMessageAsync(brokerMessage, reason, description, cancellationToken)
                    .ConfigureAwait(false);
                AzureServiceBusDiagnostics.MessageDeadLettered();
            }
            catch (ServiceBusException settlementFailure)
                when (AzureServiceBusFailureClassifier.IsLockLost(settlementFailure))
            {
                AzureServiceBusDiagnostics.LockLost();
            }

            return null;
        }
    }

    private bool IsSessionRequired(ReceiveContext context)
    {
        if (context.Subscription is not null &&
            _options.Topology.TryGetSubscription(
                context.Source,
                context.Subscription,
                out AzureServiceBusSubscriptionOptions? subscription))
        {
            return subscription!.RequiresSession;
        }

        if (context.Subscription is null &&
            _options.Topology.TryGetQueue(
                context.Source,
                out AzureServiceBusQueueOptions? queue))
        {
            return queue!.RequiresSession;
        }

        return false;
    }

    private static async Task ObserveDetachedAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
