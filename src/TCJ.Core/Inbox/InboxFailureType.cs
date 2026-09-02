namespace TCJ.Core.Inbox;

/// <summary>Bounded failure categories used by Inbox retry, dead-letter, health, and telemetry decisions.</summary>
public enum InboxFailureType
{
    /// <summary>A transient infrastructure failure may succeed later.</summary>
    TransientInfrastructure = 0,
    /// <summary>A transient handler failure may succeed later.</summary>
    TransientHandler = 1,
    /// <summary>The envelope or message contract is permanently invalid.</summary>
    PermanentValidation = 2,
    /// <summary>The payload cannot be safely deserialized.</summary>
    PermanentDeserialization = 3,
    /// <summary>The logical message type is not registered.</summary>
    UnknownMessageType = 4,
    /// <summary>The logical message version is not registered.</summary>
    UnknownMessageVersion = 5,
    /// <summary>The caller canceled processing.</summary>
    Canceled = 6,
    /// <summary>The processing operation timed out.</summary>
    Timeout = 7,
    /// <summary>A concurrency conflict prevented safe processing.</summary>
    ConcurrencyConflict = 8,
    /// <summary>The same consumer/message identity was reused with a different payload.</summary>
    PayloadConflict = 9,
    /// <summary>An otherwise unclassified failure occurred.</summary>
    Unhandled = 10
}
