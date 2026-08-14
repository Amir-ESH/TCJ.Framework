namespace TCJ.EntityFrameworkCore.Outbox;

/// <summary>Maps CLR domain-event types to stable logical outbox names and back.</summary>
public interface IOutboxEventTypeResolver
{
    /// <summary>Gets the stable logical name for a domain-event CLR type.</summary>
    /// <param name="eventType">Domain-event CLR type.</param>
    /// <returns>Stable logical event name.</returns>
    string GetName(Type eventType);

    /// <summary>Resolves a previously stored stable logical event name.</summary>
    /// <param name="eventTypeName">Stable logical event name stored in the outbox.</param>
    /// <returns>Registered domain-event CLR type.</returns>
    Type Resolve(string eventTypeName);
}
