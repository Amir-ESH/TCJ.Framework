namespace TCJ.Messaging.Sagas.Diagnostics;

/// <summary>Stable, bounded diagnostic contract for TCJ Saga orchestration.</summary>
public static class TcjSagaDiagnosticNames
{
    /// <summary>ActivitySource and Meter name.</summary>
    public const string Source = "TCJ.Messaging.Sagas";

    /// <summary>Stable activity names.</summary>
    public static class Activities
    {
        /// <summary>Saga message handling activity name.</summary>
        public const string Handle = "tcj.saga.handle";
        /// <summary>Saga timeout handling activity name.</summary>
        public const string Timeout = "tcj.saga.timeout";
        /// <summary>Saga compensation activity name.</summary>
        public const string Compensate = "tcj.saga.compensate";
        /// <summary>Saga state migration activity name.</summary>
        public const string Migrate = "tcj.saga.migrate";
        /// <summary>Saga cleanup activity name.</summary>
        public const string Cleanup = "tcj.saga.cleanup";
    }

    /// <summary>Stable metric names.</summary>
    public static class Metrics
    {
        /// <summary>Saga transition counter name.</summary>
        public const string Transitions = "tcj.saga.transitions";
        /// <summary>Completed Saga counter name.</summary>
        public const string Completed = "tcj.saga.completed";
        /// <summary>Failed Saga counter name.</summary>
        public const string Failed = "tcj.saga.failed";
        /// <summary>Saga timer execution counter name.</summary>
        public const string TimerExecutions = "tcj.saga.timer.executions";
        /// <summary>Saga timer retry counter name.</summary>
        public const string TimerRetries = "tcj.saga.timer.retries";
        /// <summary>Saga compensation execution counter name.</summary>
        public const string CompensationExecutions = "tcj.saga.compensation.executions";
        /// <summary>Saga operation duration histogram name.</summary>
        public const string Duration = "tcj.saga.duration";
    }

    /// <summary>Only bounded, non-sensitive dimensions are emitted by default.</summary>
    public static class Tags
    {
        /// <summary>Bounded logical Saga type tag name.</summary>
        public const string SagaType = "tcj.saga.type";
        /// <summary>Saga definition version tag name.</summary>
        public const string DefinitionVersion = "tcj.saga.definition_version";
        /// <summary>Bounded operation tag name.</summary>
        public const string Operation = "tcj.saga.operation";
        /// <summary>Bounded outcome tag name.</summary>
        public const string Outcome = "tcj.saga.outcome";
        /// <summary>Bounded failure-classification tag name.</summary>
        public const string FailureType = "tcj.saga.failure_type";
        /// <summary>Stable logical message type tag name.</summary>
        public const string MessageType = "tcj.saga.message_type";
        /// <summary>Registered bounded timer name tag name.</summary>
        public const string TimerName = "tcj.saga.timer_name";
    }
}
