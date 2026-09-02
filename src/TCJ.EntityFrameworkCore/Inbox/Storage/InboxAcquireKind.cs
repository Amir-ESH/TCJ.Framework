namespace TCJ.EntityFrameworkCore.Inbox.Storage;

internal enum InboxAcquireKind
{
    Acquired,
    ProcessedDuplicate,
    DuplicateInProgress,
    RetryNotDue,
    DeadLettered,
    PayloadConflict
}
