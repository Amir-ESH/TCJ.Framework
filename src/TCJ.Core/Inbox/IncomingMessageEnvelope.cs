using System.Collections.ObjectModel;

namespace TCJ.Core.Inbox;

/// <summary>Transport-neutral immutable representation of one externally delivered logical message.</summary>
public sealed class IncomingMessageEnvelope
{
    private const int MaximumIdentifierLength = 256;
    private const int MaximumMessageTypeLength = 128;
    private const int MaximumConsumerLength = 128;
    private const int MaximumHeaderNameLength = 128;
    private const int MaximumHeaderValueLength = 2048;

    /// <summary>Creates and validates an inbound message envelope.</summary>
    /// <param name="messageId">Stable logical message identifier. It must remain unchanged across redelivery.</param>
    /// <param name="messageType">Stable logical wire-contract name.</param>
    /// <param name="messageVersion">Positive schema version for <paramref name="messageType"/>.</param>
    /// <param name="consumer">Stable logical consumer boundary used for idempotency isolation.</param>
    /// <param name="payload">Serialized payload supplied by the transport adapter.</param>
    /// <param name="receivedAtUtc">UTC time at which the adapter received the delivery.</param>
    /// <param name="correlationId">Optional bounded correlation identifier.</param>
    /// <param name="causationId">Optional bounded causation identifier.</param>
    /// <param name="headers">Optional immutable copy of transport-neutral headers.</param>
    public IncomingMessageEnvelope(
        string messageId,
        string messageType,
        int messageVersion,
        string consumer,
        string payload,
        DateTimeOffset receivedAtUtc,
        string? correlationId = null,
        string? causationId = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        MessageId = ValidateIdentifier(messageId, nameof(messageId), MaximumIdentifierLength);
        MessageType = ValidateIdentifier(messageType, nameof(messageType), MaximumMessageTypeLength);
        if (messageVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(messageVersion), "Inbox message version must be greater than zero.");
        }
        MessageVersion = messageVersion;
        Consumer = ValidateIdentifier(consumer, nameof(consumer), MaximumConsumerLength);
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        ReceivedAtUtc = receivedAtUtc.ToUniversalTime();
        CorrelationId = ValidateOptionalIdentifier(correlationId, nameof(correlationId));
        CausationId = ValidateOptionalIdentifier(causationId, nameof(causationId));
        Headers = CopyHeaders(headers);
    }

    /// <summary>Gets the stable logical message identifier.</summary>
    public string MessageId { get; }
    /// <summary>Gets the stable logical message type.</summary>
    public string MessageType { get; }
    /// <summary>Gets the positive message schema version.</summary>
    public int MessageVersion { get; }
    /// <summary>Gets the logical consumer boundary.</summary>
    public string Consumer { get; }
    /// <summary>Gets the serialized transport payload.</summary>
    public string Payload { get; }
    /// <summary>Gets the adapter receive timestamp.</summary>
    public DateTimeOffset ReceivedAtUtc { get; }
    /// <summary>Gets the optional correlation identifier.</summary>
    public string? CorrelationId { get; }
    /// <summary>Gets the optional causation identifier.</summary>
    public string? CausationId { get; }
    /// <summary>Gets an immutable copy of supplied headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    private static string ValidateIdentifier(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"Inbox {parameterName} must be {maximumLength} characters or fewer.", parameterName);
        }
        if (value.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException($"Inbox {parameterName} cannot contain control characters.", parameterName);
        }
        return value;
    }

    private static string? ValidateOptionalIdentifier(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }
        return ValidateIdentifier(value, parameterName, MaximumIdentifierLength);
    }

    private static IReadOnlyDictionary<string, string> CopyHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in headers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            if (key.Length > MaximumHeaderNameLength || key.Any(static character => char.IsControl(character)))
            {
                throw new ArgumentException($"Inbox header names must be {MaximumHeaderNameLength} characters or fewer and cannot contain control characters.", nameof(headers));
            }
            if (value.Length > MaximumHeaderValueLength || value.Any(static character => char.IsControl(character) && character is not '\t'))
            {
                throw new ArgumentException($"Inbox header values must be {MaximumHeaderValueLength} characters or fewer and cannot contain control characters.", nameof(headers));
            }
            if (!copy.TryAdd(key, value))
            {
                throw new ArgumentException($"Inbox header '{key}' was supplied more than once using case-insensitive comparison.", nameof(headers));
            }
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }
}
