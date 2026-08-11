namespace TCJ.EntityFrameworkCore.Outbox.Processing;

/// <summary>
/// Indicates that an outbox processing lease expired or was replaced before the current worker could persist an outcome.
/// </summary>
public sealed class OutboxLeaseLostException : InvalidOperationException
{
    /// <summary>Creates a lease-loss exception for the stable outbox message identifier.</summary>
    /// <param name="messageId">The stable identifier of the outbox message whose lease was lost.</param>
    public OutboxLeaseLostException(Guid messageId)
        : base($"The processing lease for outbox message '{messageId}' is no longer owned by this worker.")
    {
        MessageId = messageId;
    }

    /// <summary>Gets the stable outbox message identifier.</summary>
    public Guid MessageId { get; }
}
