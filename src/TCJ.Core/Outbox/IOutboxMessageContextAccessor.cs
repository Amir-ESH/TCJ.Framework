namespace TCJ.Core.Outbox;

/// <summary>
/// Exposes the currently delivered outbox message identity and attempt metadata to handlers.
/// </summary>
/// <remarks>
/// Handlers can use the stable <see cref="OutboxMessageContext.MessageId"/> as an idempotency key.
/// The accessor never exposes the serialized payload.
/// </remarks>
public interface IOutboxMessageContextAccessor
{
    /// <summary>Gets the current delivery context, or <see langword="null"/> outside outbox dispatch.</summary>
    OutboxMessageContext? Current { get; }
}
