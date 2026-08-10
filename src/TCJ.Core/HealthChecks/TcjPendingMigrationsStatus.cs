namespace TCJ.Core.HealthChecks;

/// <summary>Controls readiness status when pending migrations are detected.</summary>
public enum TcjPendingMigrationsStatus
{
    /// <summary>Pending migrations report a degraded readiness result.</summary>
    Degraded = 0,
    /// <summary>Pending migrations report an unhealthy readiness result.</summary>
    Unhealthy = 1
}
