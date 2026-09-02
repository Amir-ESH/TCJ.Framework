namespace TCJ.EntityFrameworkCore.Inbox.Storage;

internal sealed record InboxAcquireResult(InboxAcquireKind Kind, InboxMessage? Message, int Attempt, bool IsDuplicate);
