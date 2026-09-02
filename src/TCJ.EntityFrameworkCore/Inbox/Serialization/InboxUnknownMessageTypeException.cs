namespace TCJ.EntityFrameworkCore.Inbox.Serialization;

internal sealed class InboxUnknownMessageTypeException(string messageType)
    : InvalidOperationException($"Inbox message type '{messageType}' is not registered.");
