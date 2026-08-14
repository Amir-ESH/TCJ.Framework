namespace TCJ.Core.HealthChecks;

/// <summary>Defines severity levels for startup diagnostics recorded by TCJ integrations.</summary>
public enum TcjStartupDiagnosticSeverity
{
    /// <summary>Informational diagnostic that does not affect readiness.</summary>
    Info = 0,
    /// <summary>Non-fatal diagnostic that degrades readiness.</summary>
    Warning = 1,
    /// <summary>Configuration error that makes readiness unhealthy.</summary>
    Error = 2,
    /// <summary>Fatal in-process startup error that may also make liveness unhealthy.</summary>
    Fatal = 3
}
