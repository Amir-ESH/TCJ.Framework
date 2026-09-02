namespace TCJ.EntityFrameworkCore.Inbox.Storage;

internal sealed record InboxStoreResult(InboxAcquireKind Kind, InboxMessage? Message, bool IsDuplicate);
