namespace TCJ.Core.Diagnostics;

/// <summary>
/// Controls TCJ Framework tracing and metrics behavior.
/// </summary>
/// <remarks>
/// Defaults are production-safe: tracing and metrics are enabled for standard
/// listener-based collection, while exception messages and other sensitive
/// values are not recorded.
/// </remarks>
public sealed class TcjTelemetryOptions
{
    /// <summary>Gets or sets whether TCJ activities may be created for interested listeners.</summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>Gets or sets whether TCJ metric instruments may record measurements.</summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Gets or sets whether exception messages may be attached to activities.
    /// Disabled by default because exception messages can contain sensitive data.
    /// </summary>
    public bool RecordExceptionMessages { get; set; }

    /// <summary>Gets or sets whether entity type names may be attached to activities.</summary>
    public bool RecordEntityTypeNames { get; set; } = true;

    /// <summary>Gets or sets whether domain-event handler type names may be attached to activities.</summary>
    public bool RecordHandlerTypeNames { get; set; } = true;
}
