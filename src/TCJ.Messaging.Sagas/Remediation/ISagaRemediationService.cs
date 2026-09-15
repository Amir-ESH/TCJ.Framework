namespace TCJ.Messaging.Sagas.Remediation;

/// <summary>Provides explicit bounded remediation for durable Saga state; it does not replay an event history.</summary>
public interface ISagaRemediationService
{
    /// <summary>Retries one pending application-defined compensation when policy permits it.</summary>
    /// <param name="sagaId">Stable identity of the Saga to remediate.</param>
    /// <param name="cancellationToken">Cancellation token for the remediation operation.</param>
    /// <returns>The bounded durable compensation outcome.</returns>
    Task<SagaCompensationResult> RetryCompensationAsync(Guid sagaId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a bounded batch of terminal retained Sagas that have no active timer or compensation work.</summary>
    /// <param name="cancellationToken">Cancellation token for the cleanup iteration.</param>
    /// <returns>The number deleted and whether cleanup is disabled by policy.</returns>
    Task<SagaCleanupResult> CleanupAsync(CancellationToken cancellationToken = default);
}

/// <summary>Result of one explicit compensation remediation request.</summary>
/// <param name="SagaId">Stable Saga identity.</param>
/// <param name="Executed">Whether a compensation attempt was executed.</param>
/// <param name="Completed">Whether compensation committed successfully.</param>
/// <param name="RetryScheduled">Whether bounded policy permits a later retry.</param>
/// <param name="Exhausted">Whether the compensation retry budget is exhausted.</param>
public readonly record struct SagaCompensationResult(Guid SagaId, bool Executed, bool Completed, bool RetryScheduled, bool Exhausted);

/// <summary>Result of one bounded terminal-Saga cleanup request.</summary>
/// <param name="DeletedCount">Number of terminal Saga instances deleted.</param>
/// <param name="CleanupDisabled">Whether retention policy disables cleanup.</param>
public readonly record struct SagaCleanupResult(int DeletedCount, bool CleanupDisabled);
