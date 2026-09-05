namespace TCJ.Messaging.AzureServiceBus.Diagnostics;

/// <summary>Stable Azure Service Bus adapter telemetry names.</summary>
public static class TcjAzureServiceBusDiagnosticNames
{
    /// <summary>Activity source name.</summary>
    public const string ActivitySourceName = "TCJ.Messaging.AzureServiceBus";
    /// <summary>Meter name.</summary>
    public const string MeterName = "TCJ.Messaging.AzureServiceBus";
    public const string ConnectActivity = "tcj.azure_service_bus.connect";
    public const string PublishActivity = "tcj.azure_service_bus.publish";
    public const string ScheduleActivity = "tcj.azure_service_bus.schedule";
    public const string ReceiveActivity = "tcj.azure_service_bus.receive";
    public const string CompleteActivity = "tcj.azure_service_bus.complete";
    public const string AbandonActivity = "tcj.azure_service_bus.abandon";
    public const string DeferActivity = "tcj.azure_service_bus.defer";
    public const string DeadLetterActivity = "tcj.azure_service_bus.dead_letter";
    public const string LockRenewActivity = "tcj.azure_service_bus.lock_renew";
    public const string SessionAcceptActivity = "tcj.azure_service_bus.session.accept";
    public const string TopologyValidateActivity = "tcj.azure_service_bus.topology.validate";

    /// <summary>Stable metric names.</summary>
    public static class Metrics
    {
        public const string MessagesPublished = "tcj.azure_service_bus.messages.published";
        public const string MessagesScheduled = "tcj.azure_service_bus.messages.scheduled";
        public const string MessagesReceived = "tcj.azure_service_bus.messages.received";
        public const string MessagesCompleted = "tcj.azure_service_bus.messages.completed";
        public const string MessagesAbandoned = "tcj.azure_service_bus.messages.abandoned";
        public const string MessagesDeferred = "tcj.azure_service_bus.messages.deferred";
        public const string MessagesDeadLettered = "tcj.azure_service_bus.messages.dead_lettered";
        public const string PublishDuration = "tcj.azure_service_bus.publish.duration";
        public const string ProcessingDuration = "tcj.azure_service_bus.processing.duration";
        public const string LockRenewals = "tcj.azure_service_bus.lock_renewals";
        public const string LockLosses = "tcj.azure_service_bus.lock_losses";
        public const string ProcessorErrors = "tcj.azure_service_bus.processor.errors";
        public const string ActiveSessions = "tcj.azure_service_bus.active_sessions";
    }

    /// <summary>Bounded tag names.</summary>
    public static class Tags
    {
        public const string MessagingSystem = "messaging.system";
        public const string Destination = "messaging.destination.name";
        public const string Operation = "messaging.operation.name";
        public const string MessageType = "tcj.messaging.message_type";
        public const string MessageVersion = "tcj.messaging.message_version";
        public const string Outcome = "tcj.messaging.outcome";
        public const string FailureType = "tcj.messaging.failure_type";
        public const string EntityType = "tcj.azure_service_bus.entity_type";
        public const string SessionEnabled = "tcj.azure_service_bus.session_enabled";
        public const string Settlement = "tcj.azure_service_bus.settlement";
    }
}
