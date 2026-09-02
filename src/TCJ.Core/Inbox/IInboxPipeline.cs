namespace TCJ.Core.Inbox;

/// <summary>Transport-neutral entry point for transactional Inbox processing.</summary>
public interface IInboxPipeline
{
    /// <summary>Processes or durably accepts one inbound message according to the configured mode.</summary>
    /// <param name="envelope">Validated transport-neutral incoming envelope.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>Transport-neutral acknowledgement recommendation.</returns>
    Task<InboxHandlingResult> ProcessAsync(IncomingMessageEnvelope envelope, CancellationToken cancellationToken = default);
}
