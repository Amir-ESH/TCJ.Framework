using System.Data.Common;

namespace TCJ.Core.Resilience;

/// <summary>
/// Default transient-failure detector used by TCJ resilience policies.
/// </summary>
/// <remarks>
/// Caller cancellation, argument/validation errors, authorization errors, and
/// deterministic programming failures are not treated as transient. Database
/// exceptions rely on the provider's <see cref="DbException.IsTransient"/>
/// signal instead of a TCJ-maintained SQL error-number list.
/// </remarks>
public sealed class TransientFailureDetector : ITransientFailureDetector
{
    private readonly IReadOnlyList<ITransientFailureClassifier> _classifiers;

    /// <summary>
    /// Creates a detector with optional additive classifiers.
    /// </summary>
    /// <param name="classifiers">Additional classifiers evaluated after built-in rules.</param>
    public TransientFailureDetector(IEnumerable<ITransientFailureClassifier>? classifiers = null)
    {
        _classifiers = classifiers?.ToArray() ?? [];
    }

    /// <inheritdoc />
    public bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (IsKnownPermanent(exception))
        {
            return false;
        }

        if (exception is TimeoutException)
        {
            return true;
        }

        if (exception is DbException databaseException && databaseException.IsTransient)
        {
            return true;
        }

        foreach (ITransientFailureClassifier classifier in _classifiers)
        {
            if (classifier.IsTransient(exception))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKnownPermanent(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        ArgumentException => true,
        UnauthorizedAccessException => true,
        System.Security.Authentication.AuthenticationException => true,
        System.Security.SecurityException => true,
        InvalidOperationException => true,
        NotSupportedException => true,
        NullReferenceException => true,
        IndexOutOfRangeException => true,
        InvalidCastException => true,
        FormatException => true,
        OverflowException => true,
        _ when exception.GetType().Name == "ValidationException" => true,
        _ => false
    };
}
