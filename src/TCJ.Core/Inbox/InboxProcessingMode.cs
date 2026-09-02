namespace TCJ.Core.Inbox;

/// <summary>Defines how an inbound message enters the transactional Inbox pipeline.</summary>
public enum InboxProcessingMode
{
    /// <summary>The handler executes immediately and commits with Inbox state and business data.</summary>
    Inline = 0,
    /// <summary>The transport persists the message first and a deferred processor executes it later.</summary>
    Deferred = 1
}
