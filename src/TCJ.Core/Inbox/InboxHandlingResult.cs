namespace TCJ.Core.Inbox;

/// <summary>Transport-neutral result of receiving or processing one inbound message.</summary>
/// <param name="Outcome">Recommended transport action.</param>
/// <param name="Attempt">One-based processing attempt when a handler was considered.</param>
/// <param name="FailureType">Bounded failure classification when processing did not succeed.</param>
/// <param name="IsDuplicate">Whether the consumer/message identity already existed.</param>
public sealed record InboxHandlingResult(
    InboxHandlingOutcome Outcome,
    int Attempt = 0,
    InboxFailureType? FailureType = null,
    bool IsDuplicate = false);
