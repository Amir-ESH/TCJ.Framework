namespace TCJ.Messaging.RabbitMQ.Diagnostics;

/// <summary>Stable RabbitMQ adapter telemetry names.</summary>
public static class TcjRabbitMqDiagnosticNames
{
    /// <summary>ActivitySource name.</summary>
    public const string ActivitySourceName = "TCJ.Messaging.RabbitMQ";
    /// <summary>Meter name.</summary>
    public const string MeterName = "TCJ.Messaging.RabbitMQ";
    /// <summary>Connection activity.</summary>
    public const string ConnectActivity = "tcj.rabbitmq.connect";
    /// <summary>Publish activity.</summary>
    public const string PublishActivity = "tcj.rabbitmq.publish";
    /// <summary>Publisher-confirm activity.</summary>
    public const string ConfirmActivity = "tcj.rabbitmq.confirm";
    /// <summary>Receive activity.</summary>
    public const string ReceiveActivity = "tcj.rabbitmq.receive";
    /// <summary>Settlement activity.</summary>
    public const string SettleActivity = "tcj.rabbitmq.settle";
    /// <summary>Topology activity.</summary>
    public const string TopologyDeclareActivity = "tcj.rabbitmq.topology.declare";
    /// <summary>Recovery activity.</summary>
    public const string RecoverActivity = "tcj.rabbitmq.recover";

    /// <summary>Stable metric names.</summary>
    public static class Metrics
    {
        public const string ConnectionsOpen = "tcj.rabbitmq.connections.open";
        public const string ChannelsOpen = "tcj.rabbitmq.channels.open";
        public const string MessagesPublished = "tcj.rabbitmq.messages.published";
        public const string MessagesConfirmed = "tcj.rabbitmq.messages.confirmed";
        public const string MessagesNacked = "tcj.rabbitmq.messages.nacked";
        public const string MessagesReturned = "tcj.rabbitmq.messages.returned";
        public const string MessagesReceived = "tcj.rabbitmq.messages.received";
        public const string MessagesAcked = "tcj.rabbitmq.messages.acked";
        public const string MessagesRequeued = "tcj.rabbitmq.messages.requeued";
        public const string MessagesDeadLettered = "tcj.rabbitmq.messages.dead_lettered";
        public const string PublishDuration = "tcj.rabbitmq.publish.duration";
        public const string ProcessingDuration = "tcj.rabbitmq.processing.duration";
        public const string Reconnects = "tcj.rabbitmq.reconnects";
    }

    /// <summary>Bounded telemetry tag names.</summary>
    public static class Tags
    {
        public const string MessagingSystem = "messaging.system";
        public const string Destination = "messaging.destination.name";
        public const string Operation = "messaging.operation.name";
        public const string MessageType = "tcj.messaging.message_type";
        public const string MessageVersion = "tcj.messaging.message_version";
        public const string Outcome = "tcj.messaging.outcome";
        public const string FailureType = "tcj.messaging.failure_type";
        public const string Exchange = "tcj.rabbitmq.exchange";
        public const string Queue = "tcj.rabbitmq.queue";
        public const string RoutingKey = "tcj.rabbitmq.routing_key";
    }
}
