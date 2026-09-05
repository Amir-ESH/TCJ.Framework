using System.Diagnostics;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.RabbitMQ.Connections;
using TCJ.Messaging.RabbitMQ.Diagnostics;
using TCJ.Messaging.RabbitMQ.Topology;

namespace TCJ.Messaging.RabbitMQ.Publishing;

internal sealed class RabbitMqTransportPublisher : IMessagingTransportPublisher, IAsyncDisposable
{
    private readonly RabbitMqConnectionManager _connections;
    private readonly RabbitMqMessageMapper _mapper;
    private readonly IRabbitMqRoutingKeyStrategy _routing;
    private readonly TcjRabbitMqOptions _options;
    private readonly SemaphoreSlim _channelGate = new(1, 1);
    private IChannel? _channel;
    private bool _disposed;

    internal RabbitMqTransportPublisher(RabbitMqConnectionManager connections, RabbitMqMessageMapper mapper,
        IRabbitMqRoutingKeyStrategy routing, TcjRabbitMqOptions options)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<PublishResult> PublishAsync(TransportMessageEnvelope message, PublishContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        string exchange = context.Destination ?? _options.DefaultExchange;
        RabbitMqValidation.ValidateEntityName(exchange, nameof(context.Destination), false);
        string routingKey = _routing.GetRoutingKey(message.MessageType, message.MessageVersion, message);
        return PublishCoreAsync(message, exchange, routingKey, context.TimeToLive, null, cancellationToken);
    }

    internal Task<PublishResult> PublishDeadLetterAsync(TransportMessageEnvelope message, RabbitMqRetryTopologyOptions retry,
        int attempt, string? reason, string? failureType, CancellationToken cancellationToken)
    {
        var headers = CreateDeadLetterHeaders(attempt, reason, failureType);
        return PublishCoreAsync(message, retry.DeadLetterExchange, retry.DeadLetterRoutingKey, null, headers, cancellationToken);
    }

    internal Task<PublishResult> PublishRawDeadLetterAsync(IReadOnlyBasicProperties originalProperties, ReadOnlyMemory<byte> body,
        RabbitMqRetryTopologyOptions retry, int attempt, string? reason, string? failureType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(originalProperties);
        ArgumentNullException.ThrowIfNull(retry);
        var properties = new BasicProperties
        {
            MessageId = originalProperties.MessageId,
            Type = originalProperties.Type,
            ContentType = originalProperties.ContentType,
            CorrelationId = originalProperties.CorrelationId,
            Timestamp = originalProperties.Timestamp,
            Headers = _mapper.ToSafeHeaders(originalProperties),
            Persistent = true
        };
        var headers = properties.Headers!;
        foreach ((string key, string value) in CreateDeadLetterHeaders(attempt, reason, failureType))
            headers[key] = System.Text.Encoding.UTF8.GetBytes(value);
        properties.Headers = headers;
        return PublishMappedAsync(properties, body, retry.DeadLetterExchange, retry.DeadLetterRoutingKey,
            transportMessageId: originalProperties.MessageId, message: null, cancellationToken: cancellationToken);
    }

    private async Task<PublishResult> PublishCoreAsync(TransportMessageEnvelope message, string exchange, string routingKey,
        TimeSpan? ttl, IReadOnlyDictionary<string, string>? additionalHeaders, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        RabbitMqValidation.ValidateEntityName(exchange, nameof(exchange), false);
        RabbitMqValidation.ValidateRoutingKey(routingKey, nameof(routingKey), false);
        BasicProperties properties = _mapper.ToProperties(message, ttl, additionalHeaders);
        return await PublishMappedAsync(properties, message.Body, exchange, routingKey, message.MessageId, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PublishResult> PublishMappedAsync(BasicProperties properties, ReadOnlyMemory<byte> body, string exchange, string routingKey,
        string? transportMessageId, TransportMessageEnvelope? message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        RabbitMqValidation.ValidateEntityName(exchange, nameof(exchange), false);
        RabbitMqValidation.ValidateRoutingKey(routingKey, nameof(routingKey), false);
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = RabbitMqDiagnostics.Start(TcjRabbitMqDiagnosticNames.PublishActivity, "publish",
            exchange: exchange, routingKey: routingKey, message: message, kind: ActivityKind.Producer);
        RabbitMqDiagnostics.PublishStarted();
        await _channelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                IChannel channel = await GetPublisherChannelAsync(cancellationToken).ConfigureAwait(false);
                using var confirmCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                confirmCts.CancelAfter(_options.PublishConfirmTimeout);
                using Activity? confirm = RabbitMqDiagnostics.Start(TcjRabbitMqDiagnosticNames.ConfirmActivity, "confirm",
                    exchange: exchange, routingKey: routingKey, message: message, kind: ActivityKind.Producer);
                await channel.BasicPublishAsync(exchange, routingKey, _options.MandatoryPublish, properties, body, confirmCts.Token).ConfigureAwait(false);
                RabbitMqDiagnostics.PublishConfirmed();
                confirm?.SetStatus(ActivityStatusCode.Ok);
                activity?.SetTag(TcjRabbitMqDiagnosticNames.Tags.Outcome, "published");
                activity?.SetStatus(ActivityStatusCode.Ok);
                return PublishResult.Published(transportMessageId);
            }
            catch (PublishException exception) when (exception.IsReturn)
            {
                RabbitMqDiagnostics.PublishReturned();
                activity?.SetTag(TcjRabbitMqDiagnosticNames.Tags.FailureType, "Unroutable");
                activity?.SetStatus(ActivityStatusCode.Error, "unroutable");
                return new PublishResult(PublishOutcome.PermanentFailure, FailureCategory: MessagingFailureCategory.PermanentTopology, FailureType: "Unroutable");
            }
            catch (PublishException)
            {
                RabbitMqDiagnostics.PublishNacked();
                activity?.SetTag(TcjRabbitMqDiagnosticNames.Tags.FailureType, "PublisherNack");
                activity?.SetStatus(ActivityStatusCode.Error, "publisher_nack");
                return new PublishResult(PublishOutcome.TransientFailure, FailureCategory: MessagingFailureCategory.TransientConnection, FailureType: "PublisherNack");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "canceled");
                return new PublishResult(PublishOutcome.Canceled, FailureCategory: MessagingFailureCategory.Canceled, FailureType: nameof(MessagingFailureCategory.Canceled));
            }
            catch (OperationCanceledException)
            {
                await ResetChannelAsync().ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Error, "confirm_timeout");
                return new PublishResult(PublishOutcome.TimedOut, FailureCategory: MessagingFailureCategory.TransientTimeout, FailureType: "PublisherConfirmTimeout");
            }
            catch (TimeoutException)
            {
                await ResetChannelAsync().ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Error, "timeout");
                return new PublishResult(PublishOutcome.TimedOut, FailureCategory: MessagingFailureCategory.TransientTimeout, FailureType: "RabbitMqTimeout");
            }
            catch (Exception exception) when (IsAuthenticationFailure(exception))
            {
                await ResetChannelAsync().ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Error, "authentication");
                return new PublishResult(PublishOutcome.PermanentFailure, FailureCategory: MessagingFailureCategory.PermanentAuthentication, FailureType: "AuthenticationFailure");
            }
            catch (Exception exception) when (exception is BrokerUnreachableException or AlreadyClosedException or OperationInterruptedException)
            {
                await ResetChannelAsync().ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Error, "connection");
                return new PublishResult(PublishOutcome.TransientFailure, FailureCategory: MessagingFailureCategory.TransientConnection, FailureType: "ConnectionFailure");
            }
        }
        finally
        {
            _channelGate.Release();
            RabbitMqDiagnostics.RecordPublishDuration(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task<IChannel> GetPublisherChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel?.IsOpen == true) return _channel;
        await ResetChannelAsync().ConfigureAwait(false);
        IConnection connection = await _connections.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        var channelOptions = new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);
        _channel = await connection.CreateChannelAsync(channelOptions, cancellationToken).ConfigureAwait(false);
        RabbitMqDiagnostics.ChannelOpened();
        return _channel;
    }

    private async Task ResetChannelAsync()
    {
        IChannel? channel = _channel;
        _channel = null;
        if (channel is null) return;
        try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
        RabbitMqDiagnostics.ChannelClosed();
    }

    private static bool IsAuthenticationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is AuthenticationFailureException) return true;
        return false;
    }

    private static IReadOnlyDictionary<string, string> CreateDeadLetterHeaders(int attempt, string? reason, string? failureType) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tcj-dead-letter-attempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["tcj-dead-letter-reason"] = Sanitize(reason, "dead-letter"),
            ["tcj-dead-letter-failure-type"] = Sanitize(failureType, "permanent")
        };

    private static string Sanitize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string safe = new(value.Where(static c => !char.IsControl(c)).Take(128).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _channelGate.WaitAsync().ConfigureAwait(false);
        try { await ResetChannelAsync().ConfigureAwait(false); }
        finally { _channelGate.Release(); _channelGate.Dispose(); }
    }
}
