namespace TCJ.Core.HealthChecks;

/// <summary>Configures bounded TCJ dependency health-check behavior.</summary>
public sealed class TcjHealthCheckOptions
{
    /// <summary>Gets or sets the maximum duration of one database health operation.</summary>
    public TimeSpan DatabaseTimeout { get; set; } = TcjHealthCheckDefaults.DatabaseTimeout;

    /// <summary>Gets or sets the maximum age of cached database health results.</summary>
    public TimeSpan CacheDuration { get; set; } = TcjHealthCheckDefaults.CacheDuration;

    /// <summary>Gets or sets the status returned when optional migration checks find pending migrations.</summary>
    public TcjPendingMigrationsStatus PendingMigrationsStatus { get; set; } = TcjPendingMigrationsStatus.Degraded;

    internal void Validate()
    {
        if (DatabaseTimeout <= TimeSpan.Zero || DatabaseTimeout > TcjHealthCheckDefaults.MaximumDatabaseTimeout)
        {
            throw new InvalidOperationException(
                $"{nameof(DatabaseTimeout)} must be greater than zero and no more than 10 seconds.");
        }

        if (CacheDuration < TimeSpan.Zero || CacheDuration > TcjHealthCheckDefaults.MaximumCacheDuration)
        {
            throw new InvalidOperationException(
                $"{nameof(CacheDuration)} must be between zero and 60 seconds.");
        }

        if (!Enum.IsDefined(PendingMigrationsStatus))
        {
            throw new InvalidOperationException($"{nameof(PendingMigrationsStatus)} is invalid.");
        }
    }
}

internal static class TcjHealthCheckDefaults
{
    internal static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumDatabaseTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumCacheDuration = TimeSpan.FromSeconds(60);
}
