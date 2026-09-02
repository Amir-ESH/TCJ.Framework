namespace TCJ.Core.Inbox;

/// <summary>Exposes safe metadata for the Inbox message currently executing in this asynchronous flow.</summary>
public interface IInboxMessageContextAccessor
{
    /// <summary>Gets the current Inbox context, or <see langword="null"/> outside Inbox handler execution.</summary>
    InboxMessageContext? Current { get; }
}
