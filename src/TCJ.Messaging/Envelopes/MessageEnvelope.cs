using System.Collections.ObjectModel;
using TCJ.Messaging.Configuration;

namespace TCJ.Messaging.Envelopes;

/// <summary>Immutable typed transport-neutral integration-message envelope.</summary>
/// <typeparam name="TMessage">Application message type.</typeparam>
public sealed class MessageEnvelope<TMessage>
{
    /// <summary>Creates and validates a typed envelope.</summary>
    /// <param name="messageId">Stable transport-safe logical message identifier.</param>
    /// <param name="messageType">Stable logical message type independent from CLR names.</param>
    /// <param name="messageVersion">Positive schema version.</param>
    /// <param name="message">Typed application message.</param>
    /// <param name="createdAtUtc">UTC message creation time.</param>
    /// <param name="correlationId">Optional correlation identifier.</param>
    /// <param name="causationId">Optional causation identifier.</param>
    /// <param name="partitionKey">Optional partitioning hint.</param>
    /// <param name="orderingKey">Optional ordering hint.</param>
    /// <param name="contentType">Body content type; the default serializer requires JSON.</param>
    /// <param name="headers">Optional application headers copied into an immutable dictionary.</param>
    public MessageEnvelope(
        string messageId,
        string messageType,
        int messageVersion,
        TMessage message,
        DateTimeOffset createdAtUtc,
        string? correlationId = null,
        string? causationId = null,
        string? partitionKey = null,
        string? orderingKey = null,
        string contentType = MessagingValidation.JsonContentType,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        MessageId = MessagingValidation.ValidateIdentifier(messageId, nameof(messageId), 256);
        MessageType = MessagingValidation.ValidateMessageType(messageType, nameof(messageType), 128);
        MessageVersion = MessagingValidation.ValidateVersion(messageVersion, nameof(messageVersion));
        Message = message;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        CorrelationId = MessagingValidation.ValidateOptionalIdentifier(correlationId, nameof(correlationId), 256);
        CausationId = MessagingValidation.ValidateOptionalIdentifier(causationId, nameof(causationId), 256);
        PartitionKey = MessagingValidation.ValidateOptionalIdentifier(partitionKey, nameof(partitionKey), 256);
        OrderingKey = MessagingValidation.ValidateOptionalIdentifier(orderingKey, nameof(orderingKey), 256);
        ContentType = MessagingValidation.ValidateJsonContentType(contentType, nameof(contentType));
        Headers = CopyHeaders(headers);
    }

    /// <summary>Gets the stable logical message identifier.</summary>
    public string MessageId { get; }
    /// <summary>Gets the stable logical message type independent from CLR type names.</summary>
    public string MessageType { get; }
    /// <summary>Gets the positive message schema version.</summary>
    public int MessageVersion { get; }
    /// <summary>Gets the typed application message.</summary>
    public TMessage Message { get; }
    /// <summary>Gets the UTC creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }
    /// <summary>Gets the optional correlation identifier.</summary>
    public string? CorrelationId { get; }
    /// <summary>Gets the optional causation identifier.</summary>
    public string? CausationId { get; }
    /// <summary>Gets the optional transport partitioning hint.</summary>
    public string? PartitionKey { get; }
    /// <summary>Gets the optional ordering hint.</summary>
    public string? OrderingKey { get; }
    /// <summary>Gets the stable body content type.</summary>
    public string ContentType { get; }
    /// <summary>Gets immutable transport-neutral application headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Creates an envelope with a framework-generated stable identifier.</summary>
    /// <param name="messageType">Stable logical message type.</param>
    /// <param name="messageVersion">Positive schema version.</param>
    /// <param name="message">Typed application message.</param>
    /// <param name="createdAtUtc">Optional UTC creation time; defaults to current UTC.</param>
    /// <param name="correlationId">Optional correlation identifier.</param>
    /// <param name="causationId">Optional causation identifier.</param>
    /// <param name="partitionKey">Optional partitioning hint.</param>
    /// <param name="orderingKey">Optional ordering hint.</param>
    /// <param name="headers">Optional application headers.</param>
    /// <returns>A validated immutable envelope with a version-7 GUID identifier.</returns>
    public static MessageEnvelope<TMessage> Create(
        string messageType,
        int messageVersion,
        TMessage message,
        DateTimeOffset? createdAtUtc = null,
        string? correlationId = null,
        string? causationId = null,
        string? partitionKey = null,
        string? orderingKey = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(Guid.CreateVersion7().ToString("N"), messageType, messageVersion, message,
            createdAtUtc ?? DateTimeOffset.UtcNow, correlationId, causationId, partitionKey, orderingKey,
            MessagingValidation.JsonContentType, headers);

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
