namespace TCJ.Core.Resilience;

/// <summary>
/// Classifies failures that may succeed when an explicitly retryable operation
/// is attempted again.
/// </summary>
public interface ITransientFailureDetector
{
    /// <summary>
    /// Determines whether <paramref name="exception"/> represents a transient failure.
    /// </summary>
    /// <param name="exception">The failure to classify.</param>
    /// <returns><see langword="true"/> when the failure is transient; otherwise <see langword="false"/>.</returns>
    bool IsTransient(Exception exception);
}

/// <summary>
/// Contributes application- or provider-specific transient-failure classification
/// without replacing TCJ's built-in safe defaults.
/// </summary>
public interface ITransientFailureClassifier
{
    /// <summary>
    /// Determines whether the supplied exception is a transient failure known to this classifier.
    /// </summary>
    /// <param name="exception">The failure to inspect.</param>
    /// <returns><see langword="true"/> when the failure is known to be transient.</returns>
    bool IsTransient(Exception exception);
}
