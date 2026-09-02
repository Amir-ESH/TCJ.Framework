namespace TCJ.Core.Inbox;

/// <summary>Result of one bounded Inbox cleanup operation.</summary>
/// <param name="DeletedCount">Eligible terminal records deleted.</param>
/// <param name="RetentionDisabled">Whether cleanup was disabled by configuration.</param>
public sealed record InboxCleanupResult(int DeletedCount, bool RetentionDisabled);
