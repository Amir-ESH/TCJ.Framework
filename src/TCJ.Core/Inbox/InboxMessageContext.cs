namespace TCJ.Core.Inbox;

/// <summary>Safe metadata available to an Inbox handler while one logical message is processing.</summary>
/// <param name="MessageId">Stable logical inbound message identifier.</param>
/// <param name="ConsumerName">Stable consumer boundary.</param>
/// <param name="MessageType">Stable registered logical message type.</param>
/// <param name="MessageVersion">Registered schema version.</param>
/// <param name="Attempt">One-based processing attempt.</param>
/// <param name="CorrelationId">Optional correlation identifier.</param>
/// <param name="CausationId">Optional causation identifier.</param>
public sealed record InboxMessageContext(
    string MessageId,
    string ConsumerName,
    string MessageType,
    int MessageVersion,
    int Attempt,
    string? CorrelationId = null,
    string? CausationId = null);
