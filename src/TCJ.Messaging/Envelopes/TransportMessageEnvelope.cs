using System.Collections.ObjectModel;
using TCJ.Messaging.Configuration;

namespace TCJ.Messaging.Envelopes;

/// <summary>Immutable serialized transport-neutral message envelope.</summary>
public sealed class TransportMessageEnvelope
{
    /// <summary>Creates and validates a serialized envelope without copying <paramref name="body"/>.</summary>
    /// <param name="messageId">Stable transport-safe logical message identifier.</param>
    /// <param name="messageType">Stable logical message type independent from CLR names.</param>
    /// <param name="messageVersion">Positive schema version.</param>
    /// <param name="body">Serialized body memory; the envelope does not copy it.</param>
    /// <param name="contentType">Syntactically valid body content type.</param>
    /// <param name="createdAtUtc">UTC message creation time.</param>
    /// <param name="correlationId">Optional correlation identifier.</param>
    /// <param name="causationId">Optional causation identifier.</param>
    /// <param name="partitionKey">Optional partitioning hint.</param>
    /// <param name="orderingKey">Optional ordering hint.</param>
    /// <param name="headers">Optional transport-neutral headers copied into an immutable dictionary.</param>
    public TransportMessageEnvelope(
        string messageId,
        string messageType,
        int messageVersion,
        ReadOnlyMemory<byte> body,
        string contentType,
        DateTimeOffset createdAtUtc,
        string? correlationId = null,
        string? causationId = null,
        string? partitionKey = null,
        string? orderingKey = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        MessageId = MessagingValidation.ValidateIdentifier(messageId, nameof(messageId), 256);
        MessageType = MessagingValidation.ValidateMessageType(messageType, nameof(messageType), 128);
        MessageVersion = MessagingValidation.ValidateVersion(messageVersion, nameof(messageVersion));
        Body = body;
        ContentType = MessagingValidation.ValidateContentTypeSyntax(contentType, nameof(contentType));
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        CorrelationId = MessagingValidation.ValidateOptionalIdentifier(correlationId, nameof(correlationId), 256);
        CausationId = MessagingValidation.ValidateOptionalIdentifier(causationId, nameof(causationId), 256);
        PartitionKey = MessagingValidation.ValidateOptionalIdentifier(partitionKey, nameof(partitionKey), 256);
        OrderingKey = MessagingValidation.ValidateOptionalIdentifier(orderingKey, nameof(orderingKey), 256);
        Headers = CopyHeaders(headers);
    }

    /// <summary>Gets the stable logical message identifier.</summary>
    public string MessageId { get; }
    /// <summary>Gets the stable logical message type.</summary>
    public string MessageType { get; }
    /// <summary>Gets the positive schema version.</summary>
    public int MessageVersion { get; }
    /// <summary>Gets the serialized body memory without an additional envelope copy.</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>Gets the serialized body content type.</summary>
    public string ContentType { get; }
    /// <summary>Gets the UTC message creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }
    /// <summary>Gets the optional correlation identifier.</summary>
    public string? CorrelationId { get; }
    /// <summary>Gets the optional causation identifier.</summary>
    public string? CausationId { get; }
    /// <summary>Gets the optional partitioning hint.</summary>
    public string? PartitionKey { get; }
    /// <summary>Gets the optional ordering hint.</summary>
    public string? OrderingKey { get; }
    /// <summary>Gets copied immutable transport-neutral headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    internal TransportMessageEnvelope WithMetadata(string? correlationId, string? causationId, IReadOnlyDictionary<string, string> headers) =>
        new(MessageId, MessageType, MessageVersion, Body, ContentType, CreatedAtUtc, correlationId, causationId, PartitionKey, OrderingKey, headers);

    private static IReadOnlyDictionary<string, string> CopyHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
        {
            foreach ((string key, string value) in headers)
            {
                MessagingValidation.ValidateHeaderName(key, nameof(headers), 128);
                MessagingValidation.ValidateHeaderValue(value, nameof(headers), 2048);
                if (!copy.TryAdd(key, value))
                    throw new ArgumentException($"Header '{key}' was supplied more than once using case-insensitive comparison.", nameof(headers));
            }
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }
}
