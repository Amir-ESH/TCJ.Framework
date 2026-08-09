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
