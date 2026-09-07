using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Connections;
using TCJ.Messaging.AzureServiceBus.Diagnostics;
using TCJ.Messaging.AzureServiceBus.Publishing;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.AzureServiceBus.Receiving;

/// <summary>
/// Settlement lifecycle for messages delivered through <see cref="ServiceBusSessionProcessor"/>.
/// The processor handler remains active until this settlement completes, preserving one
/// in-flight delivery per session while keeping settlement explicit.
/// </summary>
internal sealed class AzureServiceBusSessionMessageSettlement : IMessageSettlement
{
    private readonly ProcessSessionMessageEventArgs _args;
    private readonly ServiceBusReceivedMessage _received;
    private readonly TransportMessageEnvelope _envelope;
    private readonly string _source;
    private readonly AzureServiceBusClientManager _clients;
    private readonly AzureServiceBusMessageMapper _mapper;
    private readonly TcjAzureServiceBusOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _renewalCts = new();
    private readonly SemaphoreSlim _settlementGate = new(1, 1);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _renewalTask;
    private int _settled;
    private int _lockLost;

    internal AzureServiceBusSessionMessageSettlement(
        ProcessSessionMessageEventArgs args,
        ServiceBusReceivedMessage received,
        TransportMessageEnvelope envelope,
        string source,
        AzureServiceBusClientManager clients,
        AzureServiceBusMessageMapper mapper,
        TcjAzureServiceBusOptions options,
        TimeProvider timeProvider)
    {
        _args = args;
        _received = received;
        _envelope = envelope;
        _source = source;
        _clients = clients;
        _mapper = mapper;
        _options = options;
        _timeProvider = timeProvider;
        _renewalTask = RenewSessionLockLoopAsync(_renewalCts.Token);
    }

    internal Task Completion => _completion.Task;
    internal bool LockLost => Volatile.Read(ref _lockLost) != 0;

    internal async ValueTask StopRenewalAsync()
    {
        _renewalCts.Cancel();
        await ObserveRenewalCompletionAsync().ConfigureAwait(false);
    }

    public Task CompleteAsync(CancellationToken cancellationToken = default) =>
        SettleAsync(
            MessageSettlement.Complete,
            cancellationToken,
            static (self, token) =>
                self._args.CompleteMessageAsync(self._received, token));

    public Task AbandonAsync(CancellationToken cancellationToken = default) =>
        SettleAsync(
            MessageSettlement.Abandon,
            cancellationToken,
            static (self, token) =>
                self._args.AbandonMessageAsync(
                    self._received,
                    cancellationToken: token));

    public Task DeferAsync(CancellationToken cancellationToken = default) =>
        SettleAsync(
            MessageSettlement.Defer,
            cancellationToken,
            static (self, token) =>
                self._args.DeferMessageAsync(
                    self._received,
                    cancellationToken: token));

    public Task DeadLetterAsync(
        DeadLetterOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string reason = AzureServiceBusValidation.BoundSafeText(
            options.Reason ?? options.FailureType,
            128,
            "PermanentFailure");
        string description = AzureServiceBusValidation.BoundSafeText(
            options.Description,
            512,
            "TCJ Inbox classified the delivery as a permanent failure.");

        return SettleAsync(
            MessageSettlement.DeadLetter,
            cancellationToken,
            (self, token) =>
                self._args.DeadLetterMessageAsync(
                    self._received,
                    reason,
                    description,
                    token));
    }

    public async Task RetryAsync(
        RetrySettlementOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        switch (_options.RetrySettlementStrategy)
        {
            case AzureServiceBusRetrySettlementStrategy.Abandon:
                await AbandonAsync(cancellationToken).ConfigureAwait(false);
                return;

            case AzureServiceBusRetrySettlementStrategy.Defer:
                await DeferAsync(cancellationToken).ConfigureAwait(false);
                return;

            case AzureServiceBusRetrySettlementStrategy.ScheduledClone:
                await SettleAsync(
                    MessageSettlement.Retry,
                    cancellationToken,
                    async (self, token) =>
                    {
                        TimeSpan delay =
                            options.Delay is { } explicitDelay &&
                            explicitDelay > TimeSpan.Zero
                                ? explicitDelay
                                : self._options.DefaultRetryDelay;

                        if (delay > TimeSpan.FromHours(1))
                        {
                            throw new ArgumentOutOfRangeException(
                                nameof(options),
                                "Retry delay exceeds the adapter maximum.");
                        }

                        string transportId = CreateRetryTransportMessageId(
                            self._envelope.MessageId,
                            self._received.DeliveryCount + 1);

                        var publishContext = new PublishContext
                        {
                            Destination = self._source,
                            PartitionKey = self._envelope.PartitionKey,
                            OrderingKey = self._envelope.OrderingKey
                        };

                        ServiceBusMessage retry = self._mapper.ToServiceBusMessage(
                            self._envelope,
                            publishContext,
                            transportId);

                        ServiceBusSender sender = await self._clients
                            .GetSenderAsync(self._source, token)
                            .ConfigureAwait(false);

                        DateTimeOffset scheduledAt =
                            self._timeProvider.GetUtcNow().Add(delay);

                        _ = await sender
                            .ScheduleMessageAsync(retry, scheduledAt, token)
                            .ConfigureAwait(false);

                        AzureServiceBusDiagnostics.MessageScheduled();

                        await self._args
                            .CompleteMessageAsync(self._received, token)
                            .ConfigureAwait(false);
                    }).ConfigureAwait(false);
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(_options.RetrySettlementStrategy));
        }
    }

    private async Task SettleAsync(
        MessageSettlement settlement,
        CancellationToken cancellationToken,
        Func<AzureServiceBusSessionMessageSettlement, CancellationToken, Task> operation)
    {
        await _settlementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _settled) != 0)
            {
                throw new InvalidOperationException(
                    "Azure Service Bus delivery was already settled.");
            }

            if (LockLost)
            {
                throw new InvalidOperationException(
                    "Azure Service Bus session lock was lost before settlement; no completion was attempted.");
            }

            _renewalCts.Cancel();
            await ObserveRenewalCompletionAsync().ConfigureAwait(false);

            if (LockLost)
            {
                throw new InvalidOperationException(
                    "Azure Service Bus session lock was lost before settlement; no completion was attempted.");
            }

            string operationName = settlement.ToString().ToLowerInvariant();
            string activityName = settlement switch
            {
                MessageSettlement.Complete =>
                    TcjAzureServiceBusDiagnosticNames.CompleteActivity,
                MessageSettlement.Abandon =>
                    TcjAzureServiceBusDiagnosticNames.AbandonActivity,
                MessageSettlement.Defer =>
                    TcjAzureServiceBusDiagnosticNames.DeferActivity,
                MessageSettlement.DeadLetter =>
                    TcjAzureServiceBusDiagnosticNames.DeadLetterActivity,
                MessageSettlement.Retry =>
                    TcjAzureServiceBusDiagnosticNames.ScheduleActivity,
                _ => TcjAzureServiceBusDiagnosticNames.CompleteActivity
            };

            using Activity? activity = AzureServiceBusDiagnostics.Start(
                activityName,
                operationName,
                _source,
                sessionEnabled: true,
                settlement: settlement.ToString(),
                message: _envelope);

            try
            {
                await operation(this, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _settled, 1);
                activity?.SetStatus(ActivityStatusCode.Ok);

                switch (settlement)
                {
                    case MessageSettlement.Complete:
                        AzureServiceBusDiagnostics.MessageCompleted();
                        break;
                    case MessageSettlement.Abandon:
                        AzureServiceBusDiagnostics.MessageAbandoned();
                        break;
                    case MessageSettlement.Defer:
                        AzureServiceBusDiagnostics.MessageDeferred();
                        break;
                    case MessageSettlement.DeadLetter:
                        AzureServiceBusDiagnostics.MessageDeadLettered();
                        break;
                    case MessageSettlement.Retry:
                        AzureServiceBusDiagnostics.MessageCompleted();
                        break;
                }

                _completion.TrySetResult();
            }
            catch (Exception exception)
            {
                if (AzureServiceBusFailureClassifier.IsLockLost(exception))
                {
                    Volatile.Write(ref _lockLost, 1);
                    AzureServiceBusDiagnostics.LockLost();
                }

                activity?.SetStatus(
                    ActivityStatusCode.Error,
                    AzureServiceBusFailureClassifier.FailureType(exception));
                _completion.TrySetException(exception);
                throw;
            }
        }
        finally
        {
            _settlementGate.Release();
        }
    }

    private async Task ObserveRenewalCompletionAsync()
    {
        try
        {
            await _renewalTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_renewalCts.IsCancellationRequested)
        {
        }
    }

    private async Task RenewSessionLockLoopAsync(CancellationToken token)
    {
        long started = _timeProvider.GetTimestamp();

        try
        {
            while (!token.IsCancellationRequested &&
                   _timeProvider.GetElapsedTime(started) <
                   _options.MaxAutoLockRenewalDuration)
            {
                TimeSpan remaining =
                    _args.SessionLockedUntil - _timeProvider.GetUtcNow();

                TimeSpan delay = remaining <= TimeSpan.FromSeconds(4)
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromTicks(
                        Math.Min(
                            TimeSpan.FromSeconds(20).Ticks,
                            remaining.Ticks / 2));

                await Task.Delay(delay, _timeProvider, token)
                    .ConfigureAwait(false);

                using Activity? activity = AzureServiceBusDiagnostics.Start(
                    TcjAzureServiceBusDiagnosticNames.LockRenewActivity,
                    "lock_renew",
                    _source,
                    sessionEnabled: true,
                    message: _envelope);

                try
                {
                    await _args.RenewSessionLockAsync(token).ConfigureAwait(false);
                    AzureServiceBusDiagnostics.LockRenewed();
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (AzureServiceBusFailureClassifier.IsLockLost(exception))
                    {
                        Volatile.Write(ref _lockLost, 1);
                        AzureServiceBusDiagnostics.LockLost();
                    }

                    activity?.SetStatus(
                        ActivityStatusCode.Error,
                        AzureServiceBusFailureClassifier.FailureType(exception));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private static string CreateRetryTransportMessageId(
        string logicalMessageId,
        int attempt)
    {
        string suffix = $":retry:{Math.Max(1, attempt)}";
        int prefixLength = Math.Max(1, 128 - suffix.Length);

        return logicalMessageId.Length <= prefixLength
            ? logicalMessageId + suffix
            : logicalMessageId[..prefixLength] + suffix;
    }
}
