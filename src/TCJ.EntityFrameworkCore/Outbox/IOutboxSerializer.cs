using TCJ.Core.DomainEvents;

namespace TCJ.EntityFrameworkCore.Outbox;

/// <summary>Serializes and deserializes domain events stored in the transactional outbox.</summary>
public interface IOutboxSerializer
{
    /// <summary>Serializes a domain event without embedding unsafe runtime type metadata.</summary>
    /// <param name="domainEvent">Domain event to serialize.</param>
    /// <returns>Serialized payload for durable storage.</returns>
    string Serialize(IDomainEvent domainEvent);

    /// <summary>Deserializes an event payload into an already resolved domain-event type.</summary>
    /// <param name="eventType">Already resolved and trusted domain-event CLR type.</param>
    /// <param name="payload">Persisted serialized payload.</param>
    /// <returns>Deserialized domain event.</returns>
    IDomainEvent Deserialize(Type eventType, string payload);
}
