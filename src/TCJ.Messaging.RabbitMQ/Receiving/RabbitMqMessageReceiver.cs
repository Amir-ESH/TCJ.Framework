using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.RabbitMQ.Connections;
using TCJ.Messaging.RabbitMQ.Diagnostics;
using TCJ.Messaging.RabbitMQ.Publishing;
using TCJ.Messaging.RabbitMQ.Topology;

namespace TCJ.Messaging.RabbitMQ.Receiving;

internal sealed class RabbitMqMessageReceiver : IMessageReceiver
{
    private readonly RabbitMqConnectionManager _connections;
    private readonly RabbitMqMessageMapper _mapper;
    private readonly RabbitMqTransportPublisher _publisher;
    private readonly TcjRabbitMqOptions _options;
    private readonly TimeProvider _timeProvider;

    internal RabbitMqMessageReceiver(RabbitMqConnectionManager connections, RabbitMqMessageMapper mapper,
        RabbitMqTransportPublisher publisher, TcjRabbitMqOptions options, TimeProvider timeProvider)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async IAsyncEnumerable<ReceivedMessage> ReceiveAsync(ReceiveContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        RabbitMqValidation.ValidateEntityName(context.Source, nameof(context.Source), false);
        _options.Validate();
        RabbitMqQueueOptions? queueOptions = _options.Topology.Queues.SingleOrDefault(x => string.Equals(x.Name, context.Source, StringComparison.Ordinal));
        if (_options.TopologyMode != RabbitMqTopologyMode.Disabled && queueOptions is null)
            throw new InvalidOperationException($"RabbitMQ source queue '{context.Source}' is not declared in adapter topology.");
        RabbitMqRetryTopologyOptions? retry = _options.Topology.RetryTopologies.SingleOrDefault(x => string.Equals(x.SourceQueue, context.Source, StringComparison.Ordinal));

        IConnection connection = await _connections.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        var createOptions = new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false,
            consumerDispatchConcurrency: 1);
        IChannel channel = await connection.CreateChannelAsync(createOptions, cancellationToken).ConfigureAwait(false);
        RabbitMqDiagnostics.ChannelOpened();
        var settlementGate = new SemaphoreSlim(1, 1);
        var tracker = new RabbitMqDeliveryTracker();
        var buffer = Channel.CreateBounded<ReceivedMessage>(new BoundedChannelOptions(_options.PrefetchCount)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            int attempt = RabbitMqMessageMapper.GetDeliveryAttempt(delivery.BasicProperties, _options.MaximumProcessingAttempts);
            TransportMessageEnvelope envelope;
            try
            {
                // RabbitMQ.Client requires body consumption/copy before this callback returns.
                envelope = _mapper.FromDelivery(delivery.BasicProperties, delivery.Body.ToArray());
            }
            catch
            {
                // Malformed metadata is never treated as successful processing. Retry is finite; the terminal copy is publisher-confirmed before ack.
                if (retry is not null && attempt >= _options.MaximumProcessingAttempts)
                {
                    PublishResult terminal = await _publisher.PublishRawDeadLetterAsync(delivery.BasicProperties, delivery.Body.ToArray(), retry, attempt,
                        "InvalidTransportEnvelope", "PermanentSerialization", delivery.CancellationToken).ConfigureAwait(false);
                    if (terminal.IsSuccess)
                    {
                        await settlementGate.WaitAsync(delivery.CancellationToken).ConfigureAwait(false);
                        try { await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, delivery.CancellationToken).ConfigureAwait(false); }
                        finally { settlementGate.Release(); }
                        RabbitMqDiagnostics.MessageAcked();
                        RabbitMqDiagnostics.MessageDeadLettered();
                    }
                    return;
                }

                if (retry is not null || queueOptions?.DeadLetterExchange is not null)
                {
                    await settlementGate.WaitAsync(delivery.CancellationToken).ConfigureAwait(false);
                    try { await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, delivery.CancellationToken).ConfigureAwait(false); }
                    finally { settlementGate.Release(); }
                    RabbitMqDiagnostics.MessageRequeued();
                }
                return;
            }

            tracker.Add();
            RabbitMqDiagnostics.MessageReceived();
            System.Diagnostics.ActivityContext parentContext = RabbitMqDiagnostics.ExtractParent(envelope);
            using System.Diagnostics.Activity? activity = RabbitMqDiagnostics.Start(TcjRabbitMqDiagnosticNames.ReceiveActivity, "receive",
                queue: context.Source, routingKey: delivery.RoutingKey, message: envelope, kind: System.Diagnostics.ActivityKind.Consumer, parentContext: parentContext);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            var settlement = new RabbitMqMessageSettlement(channel, settlementGate, delivery.DeliveryTag, attempt, envelope,
                _publisher, retry, queueOptions, _options, tracker);
            var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rabbitmq.redelivered"] = delivery.Redelivered ? "true" : "false",
                ["rabbitmq.exchange"] = delivery.Exchange,
                ["rabbitmq.routing-key"] = delivery.RoutingKey
            };
            var deliveryContext = new DeliveryContext(envelope.MessageId, attempt, _timeProvider.GetUtcNow(), context.Source,
                context.Subscription, sequenceNumber: delivery.DeliveryTag <= long.MaxValue ? (long)delivery.DeliveryTag : null,
                extensions: extensions);
            try
            {
                await buffer.Writer.WriteAsync(new ReceivedMessage(envelope, deliveryContext, settlement), delivery.CancellationToken).ConfigureAwait(false);
            }
            catch
            {
                tracker.Complete();
                throw;
            }
        };

        string consumerTag = CreateConsumerTag(context.Source);
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, cancellationToken).ConfigureAwait(false);
        consumerTag = await channel.BasicConsumeAsync(context.Source, autoAck: false, consumerTag, noLocal: false, exclusive: false,
            arguments: null, consumer, cancellationToken).ConfigureAwait(false);

        try
        {
            await foreach (ReceivedMessage message in buffer.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return message;
        }
        finally
        {
            try
            {
                if (channel.IsOpen) await channel.BasicCancelAsync(consumerTag, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            try { await tracker.WaitForEmptyAsync(_options.ShutdownTimeout, CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { }
            buffer.Writer.TryComplete();
            try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
            settlementGate.Dispose();
            RabbitMqDiagnostics.ChannelClosed();
        }
    }

    private string CreateConsumerTag(string source)
    {
        string prefix = string.IsNullOrWhiteSpace(_options.ClientProvidedName) ? "tcj" : _options.ClientProvidedName!;
        string value = $"{prefix}:{source}:{Environment.ProcessId}";
        return value.Length <= 255 ? value : value[..255];
    }
}
