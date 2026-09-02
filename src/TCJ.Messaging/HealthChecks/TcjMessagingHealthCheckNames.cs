namespace TCJ.Messaging.HealthChecks;

/// <summary>Stable health-check registration names for TCJ.Messaging.</summary>
public static class TcjMessagingHealthCheckNames
{
    /// <summary>Transport readiness check name.</summary>
    public const string Transport = "tcj.messaging.transport";
    /// <summary>Publisher registration check name.</summary>
    public const string Publisher = "tcj.messaging.publisher";
    /// <summary>Consumer-state check name.</summary>
    public const string Consumer = "tcj.messaging.consumer";
    /// <summary>Topology/startup validation check name.</summary>
    public const string Topology = "tcj.messaging.topology";
}
