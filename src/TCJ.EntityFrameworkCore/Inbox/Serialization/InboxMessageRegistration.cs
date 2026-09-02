namespace TCJ.EntityFrameworkCore.Inbox.Serialization;

internal sealed record InboxMessageRegistration(Type MessageType, string MessageName, int Version);
