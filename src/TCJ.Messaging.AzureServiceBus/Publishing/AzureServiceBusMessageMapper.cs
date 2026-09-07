using System.Globalization;
using Azure.Messaging.ServiceBus;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.AzureServiceBus.Publishing;

internal sealed class AzureServiceBusMessageMapper
{
    private const string LogicalMessageIdHeader = "tcj-message-id";
    private readonly MessagingHeaderPolicy _headerPolicy;
    private readonly Configuration.TcjAzureServiceBusOptions _options;
    private readonly TimeProvider _timeProvider;

    internal AzureServiceBusMessageMapper(MessagingHeaderPolicy headerPolicy, Configuration.TcjAzureServiceBusOptions options, TimeProvider timeProvider)
    { _headerPolicy = headerPolicy; _options = options; _timeProvider = timeProvider; }

    internal ServiceBusMessage ToServiceBusMessage(TransportMessageEnvelope envelope, PublishContext context, string? transportMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        if (envelope.MessageId.Length > 128) throw new ArgumentException("Azure Service Bus MessageId cannot exceed 128 characters.", nameof(envelope));
        if (context.OrderingKey is { Length: > 128 } || envelope.OrderingKey is { Length: > 128 })
            throw new ArgumentException("Azure Service Bus SessionId cannot exceed 128 characters.", nameof(context));
        if (context.PartitionKey is { Length: > 128 } || envelope.PartitionKey is { Length: > 128 })
            throw new ArgumentException("Azure Service Bus PartitionKey cannot exceed 128 characters.", nameof(context));
        if (context.TimeToLive is { } ttl)
        {
            if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(context), "TimeToLive must be positive.");
            if (_options.MaximumMessageTimeToLive is { } maximum && ttl > maximum)
                throw new ArgumentOutOfRangeException(nameof(context), "TimeToLive exceeds the configured adapter maximum.");
        }
        if (context.ScheduledAtUtc is { } scheduled && scheduled.ToUniversalTime() <= _timeProvider.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(context), "ScheduledAtUtc must be in the future.");

        string? sessionId = context.OrderingKey ?? envelope.OrderingKey;
        string? partitionKey = context.PartitionKey ?? envelope.PartitionKey;
        if (sessionId is not null && partitionKey is not null && !string.Equals(sessionId, partitionKey, StringComparison.Ordinal))
            throw new ArgumentException("When both SessionId/OrderingKey and PartitionKey are supplied, Azure Service Bus requires them to be identical.", nameof(context));

        var message = new ServiceBusMessage(envelope.Body)
        {
            MessageId = transportMessageId ?? envelope.MessageId,
            Subject = envelope.MessageType,
            ContentType = envelope.ContentType,
            CorrelationId = envelope.CorrelationId,
            SessionId = sessionId
        };
        if (partitionKey is not null) message.PartitionKey = partitionKey;
        if (context.TimeToLive is { } ttlValue) message.TimeToLive = ttlValue;
        if (envelope.Headers.TryGetValue("tcj-reply-to", out string? replyTo)) message.ReplyTo = replyTo;

        IReadOnlyDictionary<string, string> safeHeaders = _headerPolicy.Filter(envelope.Headers);
        foreach ((string key, string value) in safeHeaders)
            message.ApplicationProperties[key.ToLowerInvariant()] = value;
        message.ApplicationProperties[LogicalMessageIdHeader] = envelope.MessageId;
        message.ApplicationProperties["tcj-message-type"] = envelope.MessageType;
        message.ApplicationProperties["tcj-message-version"] = envelope.MessageVersion.ToString(CultureInfo.InvariantCulture);
        message.ApplicationProperties["tcj-created-at"] = envelope.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        if (envelope.CorrelationId is not null) message.ApplicationProperties["tcj-correlation-id"] = envelope.CorrelationId;
        if (envelope.CausationId is not null) message.ApplicationProperties["tcj-causation-id"] = envelope.CausationId;
        return message;
    }

    internal TransportMessageEnvelope FromReceived(ServiceBusReceivedMessage received)
    {
        ArgumentNullException.ThrowIfNull(received);
        var rawHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, object value) in received.ApplicationProperties)
        {
            if (!TryConvertApplicationProperty(value, out string? text)) continue;
            rawHeaders[key] = text;
        }
        if (!string.IsNullOrWhiteSpace(received.ReplyTo)) rawHeaders["tcj-reply-to"] = received.ReplyTo;
        IReadOnlyDictionary<string, string> headers = _headerPolicy.Filter(rawHeaders);
        string messageId = headers.GetValueOrDefault(LogicalMessageIdHeader) ?? received.MessageId
            ?? throw new InvalidOperationException("Azure Service Bus delivery is missing a stable logical message ID.");
        string messageType = received.Subject ?? headers.GetValueOrDefault("tcj-message-type")
            ?? throw new InvalidOperationException("Azure Service Bus delivery is missing a logical message type.");
        if (!int.TryParse(headers.GetValueOrDefault("tcj-message-version"), NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version <= 0)
            throw new InvalidOperationException("Azure Service Bus delivery is missing a valid logical message version.");
        string contentType = received.ContentType ?? headers.GetValueOrDefault("content-type")
            ?? throw new InvalidOperationException("Azure Service Bus delivery is missing a content type.");
        DateTimeOffset createdAt = ParseCreatedAt(headers.GetValueOrDefault("tcj-created-at"), received.EnqueuedTime);
        string? correlationId = received.CorrelationId ?? headers.GetValueOrDefault("tcj-correlation-id");
        string? causationId = headers.GetValueOrDefault("tcj-causation-id");
        return new TransportMessageEnvelope(messageId, messageType, version, received.Body.ToMemory(), contentType, createdAt,
            correlationId, causationId, received.PartitionKey, received.SessionId, headers);
    }

    internal DeliveryContext ToDeliveryContext(ServiceBusReceivedMessage received, ReceiveContext context)
    {
        int attempt = Math.Max(1, received.DeliveryCount);
        string deliveryId = string.IsNullOrWhiteSpace(received.LockToken) ? received.SequenceNumber.ToString(CultureInfo.InvariantCulture) : received.LockToken;
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tcj.azure_service_bus.session_enabled"] = (!string.IsNullOrEmpty(received.SessionId)).ToString(CultureInfo.InvariantCulture)
        };
        return new DeliveryContext(deliveryId, attempt, _timeProvider.GetUtcNow(), context.Source, context.Subscription,
            partition: null, sequenceNumber: received.SequenceNumber, lockExpiresAtUtc: received.LockedUntil, extensions: extensions);
    }

    private static DateTimeOffset ParseCreatedAt(string? value, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
            ? parsed : fallback.ToUniversalTime();

    private static bool TryConvertApplicationProperty(object value, out string? text)
    {
        text = value switch
        {
            string v => v,
            bool v => v.ToString(CultureInfo.InvariantCulture),
            byte v => v.ToString(CultureInfo.InvariantCulture),
            sbyte v => v.ToString(CultureInfo.InvariantCulture),
            short v => v.ToString(CultureInfo.InvariantCulture),
            ushort v => v.ToString(CultureInfo.InvariantCulture),
            int v => v.ToString(CultureInfo.InvariantCulture),
            uint v => v.ToString(CultureInfo.InvariantCulture),
            long v => v.ToString(CultureInfo.InvariantCulture),
            ulong v => v.ToString(CultureInfo.InvariantCulture),
            float v => v.ToString(CultureInfo.InvariantCulture),
            double v => v.ToString(CultureInfo.InvariantCulture),
            decimal v => v.ToString(CultureInfo.InvariantCulture),
            Guid v => v.ToString("D"),
            DateTime v => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset v => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            _ => null
        };
        return text is not null && text.Length <= 2048 && !text.Any(char.IsControl);
    }
}
