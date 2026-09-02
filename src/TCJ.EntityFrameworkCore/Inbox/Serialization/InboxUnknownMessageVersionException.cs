namespace TCJ.EntityFrameworkCore.Inbox.Serialization;

internal sealed class InboxUnknownMessageVersionException(string messageType, int version)
    : InvalidOperationException($"Inbox message type '{messageType}' does not support schema version {version}.");
