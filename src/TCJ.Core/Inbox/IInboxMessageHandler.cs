namespace TCJ.Core.Inbox;

/// <summary>Handles one registered inbound message contract.</summary>
/// <typeparam name="TMessage">Registered CLR message contract.</typeparam>
public interface IInboxMessageHandler<in TMessage>
{
    /// <summary>Handles one message inside the Inbox-controlled transaction boundary.</summary>
    /// <param name="message">Safely deserialized message.</param>
    /// <param name="context">Stable inbound processing metadata.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    Task HandleAsync(TMessage message, InboxMessageContext context, CancellationToken cancellationToken);
}
