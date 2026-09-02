namespace TCJ.Messaging.Diagnostics;

/// <summary>Stable diagnostic names emitted by TCJ.Messaging.</summary>
public static class TcjMessagingDiagnosticNames
{
    /// <summary>Gets the shared ActivitySource and Meter name.</summary>
    public const string Source = "TCJ.Messaging";
    /// <summary>Stable messaging activity names.</summary>
    public static class Activities
    {
        /// <summary>Producer publication activity.</summary>
        public const string Publish = "tcj.messaging.publish";
        /// <summary>Transport receive activity.</summary>
        public const string Receive = "tcj.messaging.receive";
        /// <summary>Transport settlement activity.</summary>
        public const string Settle = "tcj.messaging.settle";
        /// <summary>Payload deserialization activity.</summary>
        public const string Deserialize = "tcj.messaging.deserialize";
        /// <summary>Consumer execution activity.</summary>
        public const string ConsumerExecute = "tcj.messaging.consumer.execute";
    }
    /// <summary>Stable metric names.</summary>
    public static class Metrics
    {
        /// <summary>Published counter.</summary>
        public const string MessagesPublished = "tcj.messaging.messages.published";
        /// <summary>Received counter.</summary>
        public const string MessagesReceived = "tcj.messaging.messages.received";
        /// <summary>Completed counter.</summary>
        public const string MessagesCompleted = "tcj.messaging.messages.completed";
        /// <summary>Retry counter.</summary>
        public const string MessagesRetried = "tcj.messaging.messages.retried";
        /// <summary>Dead-letter counter.</summary>
        public const string MessagesDeadLettered = "tcj.messaging.messages.dead_lettered";
        /// <summary>Publish duration.</summary>
        public const string PublishDuration = "tcj.messaging.publish.duration";
        /// <summary>Processing duration.</summary>
        public const string ProcessingDuration = "tcj.messaging.processing.duration";
        /// <summary>Active consumers.</summary>
        public const string ActiveConsumers = "tcj.messaging.active_consumers";
    }
    /// <summary>OpenTelemetry messaging semantic-convention attributes.</summary>
    public static class StandardAttributes
    {
        /// <summary>Messaging system.</summary>
        public const string MessagingSystem = "messaging.system";
        /// <summary>Destination name.</summary>
        public const string DestinationName = "messaging.destination.name";
        /// <summary>Operation name.</summary>
        public const string OperationName = "messaging.operation.name";
    }
    /// <summary>Stable bounded TCJ messaging tags.</summary>
    public static class Tags
    {
        /// <summary>Transport.</summary>
        public const string Transport = "tcj.messaging.transport";
        /// <summary>Destination.</summary>
        public const string Destination = "tcj.messaging.destination";
        /// <summary>Message type.</summary>
        public const string MessageType = "tcj.messaging.message_type";
        /// <summary>Message version.</summary>
        public const string MessageVersion = "tcj.messaging.message_version";
        /// <summary>Operation.</summary>
        public const string Operation = "tcj.messaging.operation";
        /// <summary>Outcome.</summary>
        public const string Outcome = "tcj.messaging.outcome";
        /// <summary>Failure type.</summary>
        public const string FailureType = "tcj.messaging.failure_type";
        /// <summary>Settlement.</summary>
        public const string Settlement = "tcj.messaging.settlement";
        /// <summary>Cancellation marker.</summary>
        public const string Canceled = "tcj.canceled";
    }
}
