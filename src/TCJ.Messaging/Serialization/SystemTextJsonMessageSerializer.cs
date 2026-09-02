using System.Diagnostics;
using System.Text.Json;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Diagnostics;
using TCJ.Messaging.Envelopes;

namespace TCJ.Messaging.Serialization;

/// <summary>Default System.Text.Json serializer using explicit JsonTypeInfo contracts only.</summary>
public sealed class SystemTextJsonMessageSerializer : IMessageSerializer
{
    private readonly TcjMessagingOptions _options;
    private readonly MessageUpcasterPipeline _upcasters;

    /// <summary>Creates the safe default serializer.</summary>
    /// <param name="options">Messaging payload bounds.</param>
    /// <param name="upcasters">Explicitly registered schema upcasters.</param>
    public SystemTextJsonMessageSerializer(TcjMessagingOptions options, IEnumerable<IMessageUpcaster> upcasters)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _upcasters = new MessageUpcasterPipeline(upcasters ?? throw new ArgumentNullException(nameof(upcasters)), options);
    }

    /// <inheritdoc />
    public TransportMessageEnvelope Serialize<TMessage>(MessageEnvelope<TMessage> envelope, MessagingMessageContract contract)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.ClrType != typeof(TMessage) || contract.MessageType != envelope.MessageType || contract.MessageVersion != envelope.MessageVersion)
            throw new InvalidOperationException("Typed envelope does not match the selected explicit messaging contract.");
        MessagingValidation.ValidateJsonContentType(envelope.ContentType, nameof(envelope.ContentType));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Message, contract.JsonTypeInfo);
        if (bytes.Length > _options.MaximumPayloadBytes)
            throw new ArgumentException($"Serialized message payload exceeds the configured {_options.MaximumPayloadBytes}-byte limit.", nameof(envelope));
        return new TransportMessageEnvelope(envelope.MessageId, envelope.MessageType, envelope.MessageVersion, bytes,
            envelope.ContentType, envelope.CreatedAtUtc, envelope.CorrelationId, envelope.CausationId,
            envelope.PartitionKey, envelope.OrderingKey, envelope.Headers);
    }

    /// <inheritdoc />
    public object Deserialize(TransportMessageEnvelope envelope, MessagingMessageContract contract)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(contract);
        MessagingValidation.ValidateJsonContentType(envelope.ContentType, nameof(envelope.ContentType));
        if (!string.Equals(envelope.MessageType, contract.MessageType, StringComparison.Ordinal))
            throw new InvalidOperationException("Transport message type does not match the selected contract.");
        if (envelope.Body.Length > _options.MaximumPayloadBytes)
            throw new ArgumentException($"Message payload exceeds the configured {_options.MaximumPayloadBytes}-byte limit.", nameof(envelope));
        using Activity? activity = MessagingDiagnostics.StartDeserialize(envelope);
        try
        {
            ReadOnlyMemory<byte> payload = _upcasters.Upcast(envelope.MessageType, envelope.MessageVersion, contract.MessageVersion, envelope.Body);
            object? value = JsonSerializer.Deserialize(payload.Span, contract.JsonTypeInfo);
            if (value is null)
                throw new JsonException("The messaging payload deserialized to null.");
            MessagingDiagnostics.CompleteDeserialize(activity);
            return value;
        }
        catch (Exception ex)
        {
            MessagingDiagnostics.CompleteDeserialize(activity, ex);
            throw;
        }
    }
}
