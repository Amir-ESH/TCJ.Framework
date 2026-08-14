namespace TCJ.Core.Outbox;

/// <summary>Result of one bounded outbox cleanup operation.</summary>
/// <param name="DeletedCount">Number of eligible processed records deleted.</param>
/// <param name="RetentionDisabled">Whether cleanup was skipped because retention is disabled.</param>
public sealed record OutboxCleanupResult(int DeletedCount, bool RetentionDisabled);
