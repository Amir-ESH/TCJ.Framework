using TCJ.Core.Resilience;

namespace TCJ.DependencyInjection.DomainEvents;

/// <summary>
/// Configures opt-in retries for an individual failing domain-event handler.
/// </summary>
/// <remarks>
/// Retries are disabled by default. Enabling retries is safe only when a handler is
/// idempotent for the retried side effects or provides its own idempotency boundary.
/// Successful handlers earlier in the dispatch sequence are never replayed.
/// </remarks>
public sealed class TcjDomainEventResilienceOptions
{
    /// <summary>Initializes domain-event resilience with handler retries disabled.</summary>
    public TcjDomainEventResilienceOptions()
    {
        Retry = new TcjRetryOptions { MaxRetryAttempts = 2 };
    }

    /// <summary>Gets or sets whether transient handler failures may be retried.</summary>
    public bool RetryTransientHandlerFailures { get; set; }

    /// <summary>Gets the bounded retry configuration used for a failing handler.</summary>
    public TcjRetryOptions Retry { get; }

    internal void Validate() => Retry.Validate();
}
