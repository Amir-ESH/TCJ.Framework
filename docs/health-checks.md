# Health checks and startup diagnostics

TCJ health checks build on the standard ASP.NET Core and `Microsoft.Extensions.Diagnostics.HealthChecks` infrastructure. They are optional and composable: registering TCJ framework services does not perform hidden network probes, and adding a health check does not mutate application or database state.

## Liveness versus readiness

**Liveness** answers whether the process and essential in-process TCJ state are functioning. The default `/health/live` endpoint selects only checks tagged `live`. SQL Server, migrations, downstream services, and other external dependencies are deliberately excluded, so a temporary database outage must not trigger a process restart loop.

**Readiness** answers whether the instance can safely receive normal traffic. The default `/health/ready` endpoint selects checks tagged `ready`, so applications may opt in framework registration, Entity Framework Core, SQL Server connectivity, migration state, and their own required dependencies.

Stable TCJ checks are:

| Check | Category | Default tags |
| --- | --- | --- |
| `tcj.core` | Liveness | `tcj`, `live` |
| `tcj.startup` | Startup | `tcj`, `ready`, `startup`, `configuration` |
| `tcj.dependency_injection` | Configuration | `tcj`, `ready`, `dependency`, `configuration` |
| `tcj.domain_events` | Dependency | `tcj`, `ready`, `dependency` |
| `tcj.entity_framework_core` | Database | `tcj`, `ready`, `dependency`, `database`, `configuration` |
| `tcj.sqlserver` | SqlServer | `tcj`, `ready`, `dependency`, `database`, `sqlserver` |
| `tcj.sqlserver.migrations` | Configuration | `tcj`, `ready`, `dependency`, `database`, `sqlserver`, `configuration` |

Names, tags, endpoint defaults, public response fields, and telemetry names are compatibility contracts recorded in `eng/health-check-contract.json`.

## Registration

Use the standard health-check builder and opt into only the integrations the application actually requires:

```csharp
using TCJ.DependencyInjection.HealthChecks;
using TCJ.EntityFrameworkCore.HealthChecks;
using TCJ.EntityFrameworkCore.SqlServer.HealthChecks;

builder.Services
    .AddTcjHealthChecks(options =>
    {
        options.DatabaseTimeout = TimeSpan.FromSeconds(5);
        options.CacheDuration = TimeSpan.FromSeconds(10);
        options.PendingMigrationsStatus = TcjPendingMigrationsStatus.Degraded;
    })
    .AddTcjDependencyInjection()
    .AddTcjDomainEvents()
    .AddTcjEntityFrameworkCore<AppDbContext>()
    .AddTcjSqlServer<AppDbContext>(checkPendingMigrations: true);
```

Registration is idempotent. A repeated TCJ health-check registration does not add duplicate check names. The first `AddTcjHealthChecks` options instance is retained, making duplicate calls deterministic. Registration performs no database connection or migration operation.

`TcjStartupDiagnostics` records sanitized, actionable framework diagnostics. Invalid bounded health options and invalid SQL Server retry options fail early during registration. Missing framework registrations, invalid provider selection, and malformed SQL Server configuration are reported with stable diagnostic codes. Transient database outages and probe timeouts affect readiness but are not persisted as startup misconfiguration.

## Endpoint mapping

```csharp
app.MapTcjLivenessChecks();                 // /health/live
app.MapTcjReadinessChecks();                // /health/ready
app.MapTcjHealthChecks("/health");          // optional combined public endpoint
app.MapTcjHealthDetails();                  // /health/details, authorization required
```

All mapping helpers return `IEndpointConventionBuilder`, so the consumer can add endpoint metadata, authorization, rate limiting, or a named authorization policy. `MapTcjHealthDetails` calls `RequireAuthorization()` by default; TCJ does not invent a hard-coded policy name. Duplicate mapping of the same TCJ path/kind is idempotent.

A custom response writer can replace the default writer without TCJ appending duplicate output:

```csharp
app.MapTcjReadinessChecks("/probe/ready", options =>
{
    options.ResponseWriter = MyHealthWriter.WriteAsync;
});
```

## Status and HTTP mapping

TCJ preserves the standard health states:

- `Healthy`: required checks passed;
- `Degraded`: a non-critical condition is present, such as pending migrations when configured as degraded;
- `Unhealthy`: a required dependency or configuration contract prevents safe readiness.

Default HTTP mapping is `Healthy -> 200`, `Degraded -> 200`, and `Unhealthy -> 503`. Consumers may override the standard `HealthCheckOptions.ResultStatusCodes` mapping in the endpoint callback.

## Response format and sensitive data

Public liveness/readiness responses include only:

```json
{
  "status": "Healthy",
  "duration": "00:00:00.0120000",
  "version": "<package-version>"
}
```

The protected details writer adds check `name`, `status`, `duration`, and stable `tags`. TCJ-owned names and tags are allow-listed; consumer-provided names outside the contract are normalized to `custom` and arbitrary tags are omitted. Neither writer serializes exception objects, exception messages, stack traces, `HealthReportEntry.Data`, SQL text, connection strings, server/database names, credentials, environment variables, or file-system paths. The same safe public shape is used in Development and Production. Responses use `Cache-Control: no-store`; dependency-result caching happens inside the bounded check, not in HTTP intermediaries.

## SQL Server connectivity

`tcj.sqlserver` resolves a fresh scoped `DbContext`, verifies that it uses the SQL Server provider, and opens/closes the provider connection without executing application SQL or modifying data. The operation receives a linked cancellation token and is bounded by `DatabaseTimeout` (5 seconds by default, maximum 10 seconds).

TCJ does not add hidden health-specific retries to the connectivity probe. A probe performs one bounded connection attempt, so invalid configuration, authentication failures, and outages are surfaced promptly; no long-lived circuit state can hide recovery. Applications may still compose their own explicitly bounded dependency checks when retry behavior is operationally justified.

## Migration state

`tcj.sqlserver.migrations` is opt-in. It calls the provider-supported pending-migrations API and never calls `Migrate`, `MigrateAsync`, `EnsureCreated`, or schema-changing SQL. Pending migration names are not returned by the public endpoint. The default pending state is `Degraded`; applications can choose `Unhealthy` with `PendingMigrationsStatus`.

## Timeout, cancellation, cache, and concurrency

`DatabaseTimeout` must be greater than zero and no more than 10 seconds. `CacheDuration` must be between zero and 60 seconds. The defaults are 5 seconds and 10 seconds respectively.

Each expensive named check owns an independent single-flight cache. Concurrent probes wait for one in-progress execution instead of starting an unbounded thundering herd. Cache expiration uses `TimeProvider`; a canceled caller cannot commit a partial cache value, and a later healthy call can execute normally. A zero cache duration disables result caching while retaining bounded synchronization.

Caller cancellation is propagated rather than converted into an internal health failure. A TCJ-owned database timeout is reported as an unhealthy bounded result so the readiness endpoint can answer before an orchestrator-level timeout.

## Observability

Health execution extends the existing the observability contract telemetry contract with:

- activity: `tcj.health_check.execute`;
- metrics: `tcj.health_checks.executed`, `tcj.health_checks.duration`, `tcj.health_checks.failures`, `tcj.health_checks.status`;
- dimensions: `tcj.health_check.name`, `tcj.health_check.category`, `tcj.health_check.status`, `tcj.operation.outcome`.

Check names, categories, and statuses are normalized to bounded values before metric emission. Connection details and exception text are never metric dimensions. Exporters remain consumer-controlled.

## Kubernetes and containers

A typical container can point orchestrator probes at the stable defaults:

```yaml
livenessProbe:
  httpGet:
    path: /health/live
    port: 8080

readinessProbe:
  httpGet:
    path: /health/ready
    port: 8080
```

Choose `initialDelaySeconds`, `timeoutSeconds`, `periodSeconds`, and `failureThreshold` for the application's startup and dependency characteristics rather than copying one universal timing profile. Keep liveness independent of SQL Server so a database outage removes the instance from traffic through readiness without causing needless process restarts.

## Local testing

```bash
python3 eng/verify-health-checks.py validate-config

dotnet test \
  tests/TCJ.HealthChecks.Tests/TCJ.HealthChecks.Tests.csproj \
  --configuration Release \
  --logger "trx;LogFileName=health-checks.trx" \
  --results-directory TestResults/HealthChecks

python3 eng/verify-health-checks.py verify \
  --results TestResults/HealthChecks \
  --output artifacts/health-checks
```

SQL Server scenarios use the repository-pinned Testcontainers image. Generated `TestResults/HealthChecks/` and `artifacts/health-checks/` are ignored by Git.

## Security and compatibility

Do not put credentials, raw provider error messages, connection strings, SQL, user identifiers, tenant identifiers, or arbitrary application labels into health descriptions or telemetry tags. Public check names and endpoint defaults are operational APIs: changing them requires compatibility review. Detailed diagnostics should stay disabled or authorization-protected on publicly reachable networks.

## Transactional outbox readiness

When the outbox is registered, `tcj.outbox.processor`, `tcj.outbox.backlog`, and `tcj.outbox.dead_letters` participate in readiness. They expose processor state and aggregate counts/ages only—never payloads, exception messages, server/database identifiers, or connection strings. Backlog and dead-letter thresholds are configurable. Liveness remains independent of temporary handler/external-system availability.

## Transactional Inbox readiness

When transactional Inbox is registered, readiness also includes `tcj.inbox.configuration`, `tcj.inbox.processor`, `tcj.inbox.backlog`, and `tcj.inbox.dead_letters`. The checks use the bounded `inbox` tag and report only processor state, counts, ages, and configuration status. They never expose message payloads, raw transport headers, message IDs, exception messages, connection strings, server names, or database names. Inbox backlog or dead letters can make readiness unhealthy according to configured thresholds, but liveness remains dependency-independent.
