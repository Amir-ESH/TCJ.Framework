namespace TCJ.EntityFrameworkCore.Inbox.Serialization;

internal sealed record InboxResolvedRegistration(Type MessageType, string MessageName, int Version, InboxHandlerRegistration Handler);
