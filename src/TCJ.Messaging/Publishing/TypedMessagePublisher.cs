using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.Publishing;

/// <summary>Typed publisher for one explicitly registered logical message contract.</summary>
/// <typeparam name="TMessage">Application message CLR type.</typeparam>
public interface IMessagePublisher<TMessage>
{
    /// <summary>Serializes and publishes one immutable typed envelope.</summary>
    /// <param name="message">Typed immutable message envelope.</param><param name="context">Optional publish hints.</param><param name="cancellationToken">Caller token.</param>
    /// <returns>The stable publication result.</returns>
    Task<PublishResult> PublishAsync(MessageEnvelope<TMessage> message, PublishContext? context = null, CancellationToken cancellationToken = default);
}

internal sealed class TypedMessagePublisher<TMessage> : IMessagePublisher<TMessage>
{
    private readonly IMessagePublisher _publisher;
    private readonly IMessageSerializer _serializer;
    private readonly IMessageContractRegistry _registry;
    public TypedMessagePublisher(IMessagePublisher publisher, IMessageSerializer serializer, IMessageContractRegistry registry)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }
    public Task<PublishResult> PublishAsync(MessageEnvelope<TMessage> message, PublishContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        MessagingMessageContract contract = _registry.Resolve(typeof(TMessage), message.MessageType, message.MessageVersion);
        TransportMessageEnvelope transport = _serializer.Serialize(message, contract);
        return _publisher.PublishAsync(transport, context ?? new PublishContext(), cancellationToken);
    }
}
