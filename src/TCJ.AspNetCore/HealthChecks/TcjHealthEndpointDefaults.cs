namespace TCJ.AspNetCore.HealthChecks;

/// <summary>Defines stable default endpoint paths for TCJ health probes.</summary>
public static class TcjHealthEndpointDefaults
{
    /// <summary>The default lightweight liveness endpoint path.</summary>
    public const string LivenessPath = "/health/live";
    /// <summary>The default readiness endpoint path.</summary>
    public const string ReadinessPath = "/health/ready";
    /// <summary>The default authorization-protected detailed diagnostics endpoint path.</summary>
    public const string DetailsPath = "/health/details";
}
