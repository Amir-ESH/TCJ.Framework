namespace TCJ.Core.Diagnostics;

/// <summary>
/// Defines the stable diagnostic names emitted by TCJ Framework packages.
/// These values are telemetry contracts and should only be changed with an
/// explicit compatibility review.
/// </summary>
public static class TcjDiagnosticNames
{
    /// <summary>Stable <see cref="System.Diagnostics.ActivitySource"/> and meter names.</summary>
    public static class Sources
    {
        /// <summary>Diagnostics emitted for TCJ core operations.</summary>
        public const string Core = "TCJ.Core";

        /// <summary>Diagnostics emitted while registering TCJ dependencies.</summary>
        public const string DependencyInjection = "TCJ.DependencyInjection";

        /// <summary>Diagnostics emitted for provider-independent Entity Framework Core operations.</summary>
        public const string EntityFrameworkCore = "TCJ.EntityFrameworkCore";

        /// <summary>Diagnostics emitted for SQL Server-specific integration.</summary>
        public const string EntityFrameworkCoreSqlServer = "TCJ.EntityFrameworkCore.SqlServer";

        /// <summary>Diagnostics emitted for TCJ ASP.NET Core integration.</summary>
        public const string AspNetCore = "TCJ.AspNetCore";
    }

    /// <summary>Stable activity names.</summary>
    public static class Activities
    {
        /// <summary>Domain-event dispatch operation.</summary>
        public const string DomainEventDispatch = "tcj.domain_event.dispatch";
        /// <summary>Single domain-event handler invocation.</summary>
        public const string DomainEventHandle = "tcj.domain_event.handle";
        /// <summary>Dependency-registration assembly scan.</summary>
        public const string DependencyInjectionScan = "tcj.di.scan";
        /// <summary>Dependency-registration operation.</summary>
        public const string DependencyInjectionRegister = "tcj.di.register";
        /// <summary>Logical repository query operation.</summary>
        public const string RepositoryQuery = "tcj.repository.query";
        /// <summary>Logical repository get operation.</summary>
        public const string RepositoryGet = "tcj.repository.get";
        /// <summary>Logical repository add operation.</summary>
        public const string RepositoryAdd = "tcj.repository.add";
        /// <summary>Logical repository update operation.</summary>
        public const string RepositoryUpdate = "tcj.repository.update";
        /// <summary>Logical repository delete operation.</summary>
        public const string RepositoryDelete = "tcj.repository.delete";
        /// <summary>Unit of Work commit operation.</summary>
        public const string UnitOfWorkCommit = "tcj.unit_of_work.commit";
        /// <summary>Database transaction begin operation.</summary>
        public const string TransactionBegin = "tcj.db.transaction.begin";
        /// <summary>Database transaction commit operation.</summary>
        public const string TransactionCommit = "tcj.db.transaction.commit";
        /// <summary>Database transaction rollback operation.</summary>
        public const string TransactionRollback = "tcj.db.transaction.rollback";
        /// <summary>SQL Server provider configuration operation.</summary>
        public const string SqlServerConfigure = "tcj.db.sqlserver.configure";
        /// <summary>ASP.NET Core exception handling operation.</summary>
        public const string AspNetCoreExceptionHandle = "tcj.aspnetcore.exception.handle";
        /// <summary>Explicit resilience policy execution.</summary>
        public const string ResilienceExecute = "tcj.resilience.execute";
        /// <summary>Retry attempt scheduled by an explicit resilience policy.</summary>
        public const string ResilienceRetry = "tcj.resilience.retry";
        /// <summary>Operation timeout produced by an explicit resilience policy.</summary>
        public const string ResilienceTimeout = "tcj.resilience.timeout";
        /// <summary>Circuit-breaker state transition or rejection.</summary>
        public const string ResilienceCircuitBreaker = "tcj.resilience.circuit_breaker";
        /// <summary>TCJ health-check execution.</summary>
        public const string HealthCheckExecute = "tcj.health_check.execute";
        /// <summary>Transactional-outbox persistence operation.</summary>
        public const string OutboxPersist = "tcj.outbox.persist";
        /// <summary>Transactional-outbox claim operation.</summary>
        public const string OutboxClaim = "tcj.outbox.claim";
        /// <summary>Transactional-outbox delivery operation.</summary>
        public const string OutboxProcess = "tcj.outbox.process";
        /// <summary>Transactional-outbox retry scheduling operation.</summary>
        public const string OutboxRetry = "tcj.outbox.retry";
        /// <summary>Transactional-outbox dead-letter operation.</summary>
        public const string OutboxDeadLetter = "tcj.outbox.dead_letter";
        /// <summary>Transactional-outbox explicit replay operation.</summary>
        public const string OutboxReplay = "tcj.outbox.replay";
        /// <summary>Transactional-outbox retention cleanup operation.</summary>
        public const string OutboxCleanup = "tcj.outbox.cleanup";
        /// <summary>Transactional Inbox receive/deduplication operation.</summary>
        public const string InboxReceive = "tcj.inbox.receive";
        /// <summary>Transactional Inbox deduplication operation.</summary>
        public const string InboxDeduplicate = "tcj.inbox.deduplicate";
        /// <summary>Transactional Inbox handler operation.</summary>
        public const string InboxProcess = "tcj.inbox.process";
        /// <summary>Transactional Inbox retry scheduling operation.</summary>
        public const string InboxRetry = "tcj.inbox.retry";
        /// <summary>Transactional Inbox dead-letter operation.</summary>
        public const string InboxDeadLetter = "tcj.inbox.dead_letter";
        /// <summary>Transactional Inbox explicit replay operation.</summary>
        public const string InboxReplay = "tcj.inbox.replay";
        /// <summary>Transactional Inbox retention cleanup operation.</summary>
        public const string InboxCleanup = "tcj.inbox.cleanup";
    }

    /// <summary>Stable metric instrument names.</summary>
    public static class Metrics
    {
        /// <summary>Completed domain-event dispatch attempts.</summary>
        public const string DomainEventsDispatched = "tcj.domain_events.dispatched";
        /// <summary>Successfully completed domain-event handler invocations.</summary>
        public const string DomainEventHandlersCompleted = "tcj.domain_event_handlers.completed";
        /// <summary>Failed domain-event handler invocations.</summary>
        public const string DomainEventHandlersFailed = "tcj.domain_event_handlers.failed";
        /// <summary>Domain-event dispatch duration.</summary>
        public const string DomainEventDispatchDuration = "tcj.domain_event.dispatch.duration";
        /// <summary>Domain-event handler duration.</summary>
        public const string DomainEventHandlerDuration = "tcj.domain_event.handler.duration";
        /// <summary>Logical repository operation attempts.</summary>
        public const string RepositoryOperations = "tcj.repository.operations";
        /// <summary>Logical repository operation duration.</summary>
        public const string RepositoryOperationDuration = "tcj.repository.operation.duration";
        /// <summary>Unit of Work commit attempts.</summary>
        public const string UnitOfWorkCommits = "tcj.unit_of_work.commits";
        /// <summary>Unit of Work rollback attempts.</summary>
        public const string UnitOfWorkRollbacks = "tcj.unit_of_work.rollbacks";
        /// <summary>Unit of Work commit duration.</summary>
        public const string UnitOfWorkCommitDuration = "tcj.unit_of_work.commit.duration";
        /// <summary>Exceptions handled by TCJ ASP.NET Core integration.</summary>
        public const string AspNetCoreExceptionsHandled = "tcj.aspnetcore.exceptions.handled";
        /// <summary>TCJ ASP.NET Core exception-handler duration.</summary>
        public const string AspNetCoreExceptionHandlerDuration = "tcj.aspnetcore.exception_handler.duration";
        /// <summary>Attempts executed by explicit resilience policies.</summary>
        public const string ResilienceAttempts = "tcj.resilience.attempts";
        /// <summary>Retries scheduled by explicit resilience policies.</summary>
        public const string ResilienceRetries = "tcj.resilience.retries";
        /// <summary>Operations canceled by explicit timeout policies.</summary>
        public const string ResilienceTimeouts = "tcj.resilience.timeouts";
        /// <summary>Circuit-open transitions recorded by circuit breakers.</summary>
        public const string ResilienceCircuitOpen = "tcj.resilience.circuit_open";
        /// <summary>Terminal failures observed by explicit resilience policies.</summary>
        public const string ResilienceFailures = "tcj.resilience.failures";
        /// <summary>Completed health-check executions.</summary>
        public const string HealthChecksExecuted = "tcj.health_checks.executed";
        /// <summary>Health-check execution duration.</summary>
        public const string HealthCheckDuration = "tcj.health_checks.duration";
        /// <summary>Unhealthy health-check executions.</summary>
        public const string HealthCheckFailures = "tcj.health_checks.failures";
        /// <summary>Health-check result counts by bounded status.</summary>
        public const string HealthCheckStatus = "tcj.health_checks.status";
        /// <summary>Outbox messages persisted in the business transaction.</summary>
        public const string OutboxMessagesPersisted = "tcj.outbox.messages.persisted";
        /// <summary>Outbox messages processed successfully.</summary>
        public const string OutboxMessagesProcessed = "tcj.outbox.messages.processed";
        /// <summary>Outbox processing failures.</summary>
        public const string OutboxMessagesFailed = "tcj.outbox.messages.failed";
        /// <summary>Outbox messages scheduled for retry.</summary>
        public const string OutboxMessagesRetried = "tcj.outbox.messages.retried";
        /// <summary>Outbox messages moved to dead-letter state.</summary>
        public const string OutboxMessagesDeadLettered = "tcj.outbox.messages.dead_lettered";
        /// <summary>Outbox message processing duration.</summary>
        public const string OutboxProcessingDuration = "tcj.outbox.processing.duration";
        /// <summary>Observed outbox pending-message count.</summary>
        public const string OutboxPendingCount = "tcj.outbox.pending.count";
        /// <summary>Observed age of the oldest pending outbox message.</summary>
        public const string OutboxOldestPendingAge = "tcj.outbox.oldest_pending.age";
        /// <summary>Inbox messages received durably.</summary>
        public const string InboxMessagesReceived = "tcj.inbox.messages.received";
        /// <summary>Inbox messages processed successfully.</summary>
        public const string InboxMessagesProcessed = "tcj.inbox.messages.processed";
        /// <summary>Inbox duplicate deliveries detected.</summary>
        public const string InboxMessagesDuplicates = "tcj.inbox.messages.duplicates";
        /// <summary>Inbox processing failures.</summary>
        public const string InboxMessagesFailed = "tcj.inbox.messages.failed";
        /// <summary>Inbox messages scheduled for retry.</summary>
        public const string InboxMessagesRetried = "tcj.inbox.messages.retried";
        /// <summary>Inbox messages moved to dead-letter state.</summary>
        public const string InboxMessagesDeadLettered = "tcj.inbox.messages.dead_lettered";
        /// <summary>Inbox handler processing duration.</summary>
        public const string InboxProcessingDuration = "tcj.inbox.processing.duration";
        /// <summary>Observed Inbox pending-message count.</summary>
        public const string InboxPendingCount = "tcj.inbox.pending.count";
        /// <summary>Observed age of the oldest pending Inbox message.</summary>
        public const string InboxOldestPendingAge = "tcj.inbox.oldest_pending.age";
    }

    /// <summary>Stable activity-tag and metric-dimension names.</summary>
    public static class Tags
    {
        /// <summary>Stable telemetry tag for PackageName.</summary>
        public const string PackageName = "tcj.package.name";
        /// <summary>Stable telemetry tag for PackageVersion.</summary>
        public const string PackageVersion = "tcj.package.version";
        /// <summary>Stable telemetry tag for FrameworkVersion.</summary>
        public const string FrameworkVersion = "tcj.framework.version";
        /// <summary>Stable telemetry tag for OperationName.</summary>
        public const string OperationName = "tcj.operation.name";
        /// <summary>Stable telemetry tag for OperationOutcome.</summary>
        public const string OperationOutcome = "tcj.operation.outcome";
        /// <summary>Stable telemetry tag for DomainEventType.</summary>
        public const string DomainEventType = "tcj.domain_event.type";
        /// <summary>Stable telemetry tag for HandlerType.</summary>
        public const string HandlerType = "tcj.handler.type";
        /// <summary>Stable telemetry tag for HandlerCount.</summary>
        public const string HandlerCount = "tcj.handler.count";
        /// <summary>Stable telemetry tag for RepositoryType.</summary>
        public const string RepositoryType = "tcj.repository.type";
        /// <summary>Stable telemetry tag for EntityType.</summary>
        public const string EntityType = "tcj.entity.type";
        /// <summary>Stable telemetry tag for DatabaseProvider.</summary>
        public const string DatabaseProvider = "tcj.db.provider";
        /// <summary>Stable telemetry tag for TransactionOutcome.</summary>
        public const string TransactionOutcome = "tcj.transaction.outcome";
        /// <summary>Stable telemetry tag for ExceptionType.</summary>
        public const string ExceptionType = "tcj.exception.type";
        /// <summary>Stable telemetry tag for ExceptionMessage.</summary>
        public const string ExceptionMessage = "tcj.exception.message";
        /// <summary>Stable telemetry tag for ExceptionCategory.</summary>
        public const string ExceptionCategory = "tcj.exception.category";
        /// <summary>Stable telemetry tag for HttpStatusCode.</summary>
        public const string HttpStatusCode = "tcj.http.status_code";
        /// <summary>Stable telemetry tag for Canceled.</summary>
        public const string Canceled = "tcj.canceled";
        /// <summary>Stable telemetry tag for AffectedRows.</summary>
        public const string AffectedRows = "tcj.affected_rows";
        /// <summary>Stable telemetry tag for AssemblyCount.</summary>
        public const string AssemblyCount = "tcj.di.assembly_count";
        /// <summary>Stable telemetry tag for DiscoveredTypeCount.</summary>
        public const string DiscoveredTypeCount = "tcj.di.discovered_type_count";
        /// <summary>Stable telemetry tag for RegisteredServiceCount.</summary>
        public const string RegisteredServiceCount = "tcj.di.registered_service_count";
        /// <summary>Stable bounded resilience strategy name.</summary>
        public const string ResilienceStrategy = "tcj.resilience.strategy";
        /// <summary>Stable resilience operation outcome.</summary>
        public const string ResilienceOutcome = "tcj.resilience.outcome";
        /// <summary>Bounded retry-attempt number.</summary>
        public const string ResilienceAttempt = "tcj.resilience.attempt";
        /// <summary>Bounded resilience failure category.</summary>
        public const string ResilienceFailureType = "tcj.resilience.failure_type";
        /// <summary>Bounded circuit-breaker state.</summary>
        public const string ResilienceCircuitState = "tcj.resilience.circuit_state";
        /// <summary>Stable bounded health-check name.</summary>
        public const string HealthCheckName = "tcj.health_check.name";
        /// <summary>Stable bounded health-check category.</summary>
        public const string HealthCheckCategory = "tcj.health_check.category";
        /// <summary>Stable bounded health-check status.</summary>
        public const string HealthCheckStatus = "tcj.health_check.status";
        /// <summary>Stable logical outbox event type.</summary>
        public const string OutboxEventType = "tcj.outbox.event_type";
        /// <summary>One-based outbox delivery attempt.</summary>
        public const string OutboxAttempt = "tcj.outbox.attempt";
        /// <summary>Bounded outbox operation outcome.</summary>
        public const string OutboxOutcome = "tcj.outbox.outcome";
        /// <summary>Normalized outbox storage provider.</summary>
        public const string OutboxProvider = "tcj.outbox.provider";
        /// <summary>Stable bounded Inbox consumer name.</summary>
        public const string InboxConsumer = "tcj.inbox.consumer";
        /// <summary>Stable registered Inbox message type.</summary>
        public const string InboxMessageType = "tcj.inbox.message_type";
        /// <summary>Registered Inbox message schema version.</summary>
        public const string InboxMessageVersion = "tcj.inbox.message_version";
        /// <summary>One-based Inbox processing attempt.</summary>
        public const string InboxAttempt = "tcj.inbox.attempt";
        /// <summary>Bounded Inbox processing outcome.</summary>
        public const string InboxOutcome = "tcj.inbox.outcome";
        /// <summary>Bounded Inbox failure category.</summary>
        public const string InboxFailureType = "tcj.inbox.failure_type";
        /// <summary>Normalized Inbox storage provider.</summary>
        public const string InboxProvider = "tcj.inbox.provider";
    }

    /// <summary>Bounded operation outcome values.</summary>
    public static class Outcomes
    {
        /// <summary>Telemetry outcome value for success operations.</summary>
        public const string Success = "success";
        /// <summary>Telemetry outcome value for failure operations.</summary>
        public const string Failure = "failure";
        /// <summary>Telemetry outcome value for canceled operations.</summary>
        public const string Canceled = "canceled";
    }

    /// <summary>Normalized provider identities used by TCJ diagnostics.</summary>
    public static class Providers
    {
        /// <summary>Normalized provider identity for Unknown.</summary>
        public const string Unknown = "unknown";
        /// <summary>Normalized provider identity for SqlServer.</summary>
        public const string SqlServer = "Microsoft.EntityFrameworkCore.SqlServer";
    }
}
