namespace TCJ.EntityFrameworkCore.Outbox.Serialization;

/// <summary>Represents an explicit stable logical name registered for a domain-event CLR type.</summary>
/// <param name="EventType">Domain-event CLR type.</param>
/// <param name="EventName">Stable versioned logical event name.</param>
internal sealed record OutboxEventRegistration(Type EventType, string EventName);
