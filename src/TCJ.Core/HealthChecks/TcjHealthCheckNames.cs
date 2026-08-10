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
    }
}
