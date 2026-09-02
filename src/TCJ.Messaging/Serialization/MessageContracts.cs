using System.Text.Json.Serialization.Metadata;
using TCJ.Messaging.Envelopes;

namespace TCJ.Messaging.Serialization;

/// <summary>Stable registered logical message contract.</summary>
public sealed class MessagingMessageContract
{
    internal MessagingMessageContract(string messageType, int messageVersion, Type clrType, JsonTypeInfo jsonTypeInfo)
    {
        MessageType = messageType;
        MessageVersion = messageVersion;
        ClrType = clrType;
        JsonTypeInfo = jsonTypeInfo;
    }
    /// <summary>Gets the stable logical wire message type.</summary>
    public string MessageType { get; }
    /// <summary>Gets the positive wire schema version.</summary>
    public int MessageVersion { get; }
    /// <summary>Gets the explicitly registered CLR message type.</summary>
    public Type ClrType { get; }
    /// <summary>Gets explicit System.Text.Json metadata.</summary>
    public JsonTypeInfo JsonTypeInfo { get; }
}

/// <summary>Resolves explicitly registered logical contracts without wire-driven CLR activation.</summary>
public interface IMessageContractRegistry
{
    /// <summary>Resolves by logical type and version.</summary>
    /// <param name="messageType">Stable logical type.</param>
    /// <param name="messageVersion">Positive version.</param>
    /// <returns>The registered contract.</returns>
    MessagingMessageContract Resolve(string messageType, int messageVersion);
    /// <summary>Resolves and verifies the expected CLR type.</summary>
    /// <param name="clrType">Expected CLR type.</param>
    /// <param name="messageType">Stable logical type.</param>
    /// <param name="messageVersion">Positive version.</param>
    /// <returns>The registered contract.</returns>
    MessagingMessageContract Resolve(Type clrType, string messageType, int messageVersion);
    /// <summary>Gets all registered contracts.</summary>
    IReadOnlyCollection<MessagingMessageContract> Contracts { get; }
}

/// <summary>Explicit serialized-payload upcaster for one logical message type.</summary>
public interface IMessageUpcaster
{
    /// <summary>Gets the logical type handled by this upcaster.</summary>
    string MessageType { get; }
    /// <summary>Gets the input version.</summary>
    int SourceVersion { get; }
    /// <summary>Gets the strictly higher output version.</summary>
    int TargetVersion { get; }
    /// <summary>Transforms one serialized payload.</summary>
    /// <param name="payload">Source-version payload.</param>
    /// <returns>Target-version payload.</returns>
    ReadOnlyMemory<byte> Upcast(ReadOnlyMemory<byte> payload);
}

/// <summary>Transport-neutral serializer contract.</summary>
public interface IMessageSerializer
{
    /// <summary>Serializes a typed envelope with explicit metadata.</summary>
    /// <typeparam name="TMessage">Application message type.</typeparam>
    /// <param name="envelope">Typed envelope.</param>
    /// <param name="contract">Registered contract.</param>
    /// <returns>Serialized transport envelope.</returns>
    TransportMessageEnvelope Serialize<TMessage>(MessageEnvelope<TMessage> envelope, MessagingMessageContract contract);
    /// <summary>Deserializes to an explicitly selected target contract.</summary>
    /// <param name="envelope">Serialized transport envelope.</param>
    /// <param name="contract">Target registered contract.</param>
    /// <returns>Deserialized application message.</returns>
    object Deserialize(TransportMessageEnvelope envelope, MessagingMessageContract contract);
}
