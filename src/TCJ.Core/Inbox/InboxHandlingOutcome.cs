namespace TCJ.Core.Inbox;

/// <summary>Transport-neutral recommendation returned by the Inbox layer.</summary>
public enum InboxHandlingOutcome
{
    /// <summary>The transport may acknowledge the message.</summary>
    Acknowledge = 0,
    /// <summary>The transport should retry or redeliver according to its policy.</summary>
    Retry = 1,
    /// <summary>The transport should dead-letter or otherwise stop automatic redelivery.</summary>
    DeadLetter = 2,
    /// <summary>The logical message was already committed and should not invoke the handler again.</summary>
    IgnoreDuplicate = 3
}
