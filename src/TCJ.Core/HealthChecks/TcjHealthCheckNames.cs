namespace TCJ.Core.HealthChecks;

/// <summary>Defines stable names, categories, and tags used by TCJ health checks.</summary>
public static class TcjHealthCheckNames
{
    /// <summary>Stable health-check registration names.</summary>
    public static class Checks
    {
        /// <summary>The core process liveness check.</summary>
        public const string Core = "tcj.core";
        /// <summary>The startup-diagnostics readiness check.</summary>
        public const string Startup = "tcj.startup";
        /// <summary>The dependency-injection readiness check.</summary>
        public const string DependencyInjection = "tcj.dependency_injection";
        /// <summary>The domain-event dispatcher readiness check.</summary>
        public const string DomainEvents = "tcj.domain_events";
        /// <summary>The provider-independent Entity Framework Core readiness check.</summary>
        public const string EntityFrameworkCore = "tcj.entity_framework_core";
        /// <summary>The SQL Server connectivity readiness check.</summary>
        public const string SqlServer = "tcj.sqlserver";
        /// <summary>The optional SQL Server pending-migrations readiness check.</summary>
        public const string SqlServerMigrations = "tcj.sqlserver.migrations";
        /// <summary>The outbox processor-state readiness check.</summary>
        public const string OutboxProcessor = "tcj.outbox.processor";
        /// <summary>The outbox pending-backlog readiness check.</summary>
        public const string OutboxBacklog = "tcj.outbox.backlog";
        /// <summary>The outbox dead-letter readiness check.</summary>
        public const string OutboxDeadLetters = "tcj.outbox.dead_letters";
        /// <summary>The Inbox configuration readiness check.</summary>
        public const string InboxConfiguration = "tcj.inbox.configuration";
        /// <summary>The Inbox processor-state readiness check.</summary>
        public const string InboxProcessor = "tcj.inbox.processor";
        /// <summary>The Inbox pending-backlog readiness check.</summary>
        public const string InboxBacklog = "tcj.inbox.backlog";
        /// <summary>The Inbox dead-letter readiness check.</summary>
        public const string InboxDeadLetters = "tcj.inbox.dead_letters";
    }

    /// <summary>Stable tags used to select TCJ health checks.</summary>
    public static class Tags
    {
        /// <summary>Identifies checks owned by TCJ.</summary>
        public const string Tcj = "tcj";
        /// <summary>Selects liveness checks.</summary>
        public const string Live = "live";
        /// <summary>Selects readiness checks.</summary>
        public const string Ready = "ready";
        /// <summary>Identifies dependency checks.</summary>
        public const string Dependency = "dependency";
        /// <summary>Identifies startup checks.</summary>
        public const string Startup = "startup";
        /// <summary>Identifies database checks.</summary>
        public const string Database = "database";
        /// <summary>Identifies SQL Server checks.</summary>
        public const string SqlServer = "sqlserver";
        /// <summary>Identifies configuration checks.</summary>
        public const string Configuration = "configuration";
        /// <summary>Identifies transactional-outbox checks.</summary>
        public const string Outbox = "outbox";
        /// <summary>Identifies transactional-Inbox checks.</summary>
        public const string Inbox = "inbox";
    }

    /// <summary>Bounded category values used by health-check telemetry.</summary>
    public static class Categories
    {
        /// <summary>The liveness category.</summary>
        public const string Liveness = "liveness";
        /// <summary>The readiness category.</summary>
        public const string Readiness = "readiness";
        /// <summary>The dependency category.</summary>
        public const string Dependency = "dependency";
        /// <summary>The startup category.</summary>
        public const string Startup = "startup";
        /// <summary>The database category.</summary>
        public const string Database = "database";
        /// <summary>The SQL Server category.</summary>
        public const string SqlServer = "sqlserver";
        /// <summary>The configuration category.</summary>
        public const string Configuration = "configuration";
        /// <summary>The transactional-outbox category.</summary>
        public const string Outbox = "outbox";
        /// <summary>The transactional-Inbox category.</summary>
        public const string Inbox = "inbox";
    }
}
