using System.Diagnostics;
using RabbitMQ.Client;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.RabbitMQ.Diagnostics;
using TCJ.Messaging.RabbitMQ.Publishing;
using TCJ.Messaging.RabbitMQ.Topology;
using TCJ.Messaging.Envelopes;

namespace TCJ.Messaging.RabbitMQ.Receiving;

internal sealed class RabbitMqMessageSettlement : IMessageSettlement
{
    private readonly IChannel _channel;
    private readonly SemaphoreSlim _channelGate;
    private readonly ulong _deliveryTag;
    private readonly int _attempt;
    private readonly TransportMessageEnvelope _message;
    private readonly RabbitMqTransportPublisher _publisher;
    private readonly RabbitMqRetryTopologyOptions? _retry;
    private readonly RabbitMqQueueOptions? _queue;
    private readonly TcjRabbitMqOptions _options;
    private readonly RabbitMqDeliveryTracker _tracker;
    private readonly SemaphoreSlim _once = new(1, 1);
    private bool _settled;

    internal RabbitMqMessageSettlement(IChannel channel, SemaphoreSlim channelGate, ulong deliveryTag, int attempt,
        TransportMessageEnvelope message, RabbitMqTransportPublisher publisher, RabbitMqRetryTopologyOptions? retry,
        RabbitMqQueueOptions? queue, TcjRabbitMqOptions options, RabbitMqDeliveryTracker tracker)
    {
        _channel = channel; _channelGate = channelGate; _deliveryTag = deliveryTag; _attempt = attempt; _message = message;
        _publisher = publisher; _retry = retry; _queue = queue; _options = options; _tracker = tracker;
    }

    public Task CompleteAsync(CancellationToken cancellationToken = default) => ExecuteOnceAsync("complete", AckAsync, cancellationToken);

    public Task RetryAsync(RetrySettlementOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ExecuteOnceAsync("retry", async token =>
        {
            if (_attempt >= _options.MaximumProcessingAttempts)
            {
                await DeadLetterCoreAsync(new DeadLetterOptions { Reason = "MaximumAttemptsExceeded", FailureType = "PoisonMessage", Attempt = _attempt }, token).ConfigureAwait(false);
                return;
            }
            if (_retry is null) throw new MessagingCapabilityException("RetryTopology");
            if (options.Delay is { } delay && delay > _retry.RetryDelay)
                throw new MessagingCapabilityException("RetryDelayOverride");
            await NackAsync(requeue: false, token).ConfigureAwait(false);
            RabbitMqDiagnostics.MessageRequeued();
        }, cancellationToken);
    }

    public Task DeadLetterAsync(DeadLetterOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ExecuteOnceAsync("dead_letter", token => DeadLetterCoreAsync(options, token), cancellationToken);
    }

    public Task AbandonAsync(CancellationToken cancellationToken = default) => ExecuteOnceAsync("abandon", async token =>
    {
        await NackAsync(requeue: true, token).ConfigureAwait(false);
        RabbitMqDiagnostics.MessageRequeued();
    }, cancellationToken);

    public Task DeferAsync(CancellationToken cancellationToken = default) => throw new MessagingCapabilityException("Defer");

    private async Task DeadLetterCoreAsync(DeadLetterOptions options, CancellationToken cancellationToken)
    {
        if (_retry is not null)
        {
            PublishResult result = await _publisher.PublishDeadLetterAsync(_message, _retry, options.Attempt ?? _attempt,
                options.Reason, options.FailureType, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) throw new InvalidOperationException("RabbitMQ terminal dead-letter publication was not confirmed.");
            await AckAsync(cancellationToken).ConfigureAwait(false);
            RabbitMqDiagnostics.MessageDeadLettered();
            return;
        }
        if (_queue?.DeadLetterExchange is null) throw new MessagingCapabilityException("DeadLetterTopology");
        await NackAsync(requeue: false, cancellationToken).ConfigureAwait(false);
        RabbitMqDiagnostics.MessageDeadLettered();
    }

    private async Task AckAsync(CancellationToken cancellationToken)
    {
        await _channelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _channel.BasicAckAsync(_deliveryTag, multiple: false, cancellationToken).ConfigureAwait(false); }
        finally { _channelGate.Release(); }
        RabbitMqDiagnostics.MessageAcked();
    }

    private async Task NackAsync(bool requeue, CancellationToken cancellationToken)
    {
        await _channelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _channel.BasicNackAsync(_deliveryTag, multiple: false, requeue, cancellationToken).ConfigureAwait(false); }
        finally { _channelGate.Release(); }
    }

    private async Task ExecuteOnceAsync(string operation, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await _once.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settled) throw new InvalidOperationException("RabbitMQ delivery has already been settled.");
            using Activity? activity = RabbitMqDiagnostics.Start(TcjRabbitMqDiagnosticNames.SettleActivity, operation);
            await action(cancellationToken).ConfigureAwait(false);
            _settled = true;
            _tracker.Complete();
            activity?.SetTag(TcjRabbitMqDiagnosticNames.Tags.Outcome, operation);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        finally { _once.Release(); }
    }
}
