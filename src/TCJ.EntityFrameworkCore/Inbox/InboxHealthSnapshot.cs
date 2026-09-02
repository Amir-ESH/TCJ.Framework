namespace TCJ.EntityFrameworkCore.Inbox;

internal sealed record InboxHealthSnapshot(long PendingCount, long DeadLetterCount, TimeSpan OldestPendingAge);
