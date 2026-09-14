namespace TCJ.Messaging.Sagas;

/// <summary>Durable lifecycle status of a Saga instance.</summary>
public enum SagaStatus
{
    /// <summary>The Saga accepts normal registered transitions.</summary>
    Active = 0,
    /// <summary>The Saga completed successfully and is terminal.</summary>
    Completed = 1,
    /// <summary>The Saga failed and is terminal.</summary>
    Failed = 2,
    /// <summary>Explicit application-defined compensation is pending or in progress.</summary>
    Compensating = 3,
    /// <summary>Explicit application-defined compensation completed and the Saga is terminal.</summary>
    Compensated = 4
}
