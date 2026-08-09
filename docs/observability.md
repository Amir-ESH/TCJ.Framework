# Diagnostics and OpenTelemetry observability

TCJ Framework exposes tracing and metrics through the standard .NET `ActivitySource` and `Meter` APIs. The production packages do not select an exporter, collector, vendor, transport, or hosted service. Applications decide whether telemetry is observed and where it is exported.

The observability contract is versioned in:

- `eng/observability-policy.json`
- `eng/observability-contract.json`

Changing an activity name, metric name, metric type, or unit is a telemetry compatibility change and must be reviewed like any other consumer-facing contract.

## Design goals

TCJ instrumentation is designed around these rules:

- no exporter or collector is required by production packages;
- no production package performs telemetry network calls;
- no telemetry-only background thread is created;
- no request, entity, user, SQL, or connection-string values are emitted by default;
- activities preserve ambient `Activity.Current` parentage and baggage;
- expensive type formatting is avoided when an activity was not sampled;
- metric dimensions are bounded and operation-oriented;
- instrumentation never swallows or replaces the application exception;
- enabling telemetry does not change repository, transaction, dispatch, or exception-handling semantics.

## ActivitySource and Meter identities

The stable source and meter names are:

| Package area | ActivitySource | Meter |
| --- | --- | --- |
| Core/domain events | `TCJ.Core` | `TCJ.Core` |
| Dependency registration | `TCJ.DependencyInjection` | `TCJ.DependencyInjection` |
| EF Core logical operations | `TCJ.EntityFrameworkCore` | `TCJ.EntityFrameworkCore` |
| SQL Server integration | `TCJ.EntityFrameworkCore.SqlServer` | `TCJ.EntityFrameworkCore.SqlServer` |
| ASP.NET Core | `TCJ.AspNetCore` | `TCJ.AspNetCore` |

Each source and meter is created with the package version embedded by `eng/Packaging.props`. `TcjTelemetry.FrameworkVersion` exposes the `TCJ.Core` package version for resource enrichment in consumer OpenTelemetry configuration.

## Activity contract

The current activity names are:

| Activity | Meaning |
| --- | --- |
| `tcj.domain_event.dispatch` | One concrete domain-event dispatch operation |
| `tcj.domain_event.handle` | One invoked domain-event handler |
| `tcj.di.scan` | Convention-based assembly scanning |
| `tcj.di.register` | Convention-based service registration |
| `tcj.repository.query` | Logical repository query/count/exists/list operations |
| `tcj.repository.get` | Logical repository single-entity reads |
| `tcj.repository.add` | Repository add/add-range operations |
| `tcj.repository.update` | Repository update/update-range operations |
| `tcj.repository.delete` | Physical delete and explicit soft-delete/restore operations |
| `tcj.unit_of_work.commit` | `SaveChangesAsync` persistence boundary |
| `tcj.db.transaction.begin` | Explicit transaction begin |
| `tcj.db.transaction.commit` | Explicit transaction commit |
| `tcj.db.transaction.rollback` | Explicit transaction rollback |
| `tcj.db.sqlserver.configure` | TCJ SQL Server provider configuration |
| `tcj.aspnetcore.exception.handle` | TCJ unhandled-exception mapping |

TCJ activities use `ActivityKind.Internal`; they represent framework-level work, not a second HTTP request span or a database command span.

### Status and cancellation

Successful activities use `ActivityStatusCode.Ok`. Failures use `ActivityStatusCode.Error` and record the exception type. Expected cancellation records `tcj.canceled=true` and an outcome of `canceled`; it is not automatically classified as an internal framework error.

The original exception or `OperationCanceledException` continues to propagate unless the existing framework contract already handles it, as the ASP.NET Core exception handler does.

## Metric contract

Duration metrics use seconds consistently.

| Metric | Type | Unit |
| --- | --- | --- |
| `tcj.domain_events.dispatched` | Counter | `{event}` |
| `tcj.domain_event_handlers.completed` | Counter | `{event}` |
| `tcj.domain_event_handlers.failed` | Counter | `{event}` |
| `tcj.domain_event.dispatch.duration` | Histogram | `s` |
| `tcj.domain_event.handler.duration` | Histogram | `s` |
| `tcj.repository.operations` | Counter | `{operation}` |
| `tcj.repository.operation.duration` | Histogram | `s` |
| `tcj.unit_of_work.commits` | Counter | `{operation}` |
| `tcj.unit_of_work.rollbacks` | Counter | `{operation}` |
| `tcj.unit_of_work.commit.duration` | Histogram | `s` |
| `tcj.aspnetcore.exceptions.handled` | Counter | `{exception}` |
| `tcj.aspnetcore.exception_handler.duration` | Histogram | `s` |

Metric dimensions are intentionally smaller than activity tags. Bounded dimensions include operation name, outcome, provider, exception category, and HTTP status where relevant. Entity identifiers, user identifiers, request paths, query text, SQL text, tenant IDs, and exception messages are not metric dimensions.

## Tags and sensitive data

Common low-cardinality activity tags include:

- `tcj.package.name`
- `tcj.package.version`
- `tcj.operation.name`
- `tcj.operation.outcome`
- `tcj.domain_event.type`
- `tcj.handler.type`
- `tcj.repository.type`
- `tcj.entity.type`
- `tcj.db.provider`
- `tcj.transaction.outcome`
- `tcj.exception.type`
- `tcj.exception.category`
- `tcj.http.status_code`
- `tcj.canceled`

TCJ does not emit entity IDs, user IDs, raw SQL, connection strings, request bodies, tokens, passwords, tenant identifiers, or exception messages by default.

`RecordExceptionMessages` is an explicit diagnostic opt-in. Exception messages are application data and may contain secrets or personal information, so leave this option disabled in production unless the application has its own documented data-handling policy.

## Configure TCJ telemetry

Instrumentation is available without calling a registration method. `AddTcjTelemetry` exists as an idempotent configuration entry point and never registers an exporter or collector.

```csharp
builder.Services.AddTcjTelemetry(options =>
{
    options.EnableTracing = true;
    options.EnableMetrics = true;
    options.RecordExceptionMessages = false;
    options.RecordEntityTypeNames = true;
    options.RecordHandlerTypeNames = true;
});
```

Tracing still follows normal `ActivitySource` sampling behavior: when no listener is interested, `StartActivity` returns `null` and TCJ skips activity-only tag formatting. Metrics use normal `Meter` listener semantics and build metric tags only for enabled instruments.

## OpenTelemetry setup

The sample application demonstrates a consumer-owned OpenTelemetry SDK setup. TCJ production packages remain unaware of the SDK.

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TCJ.Core.Diagnostics;

builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService("MyService")
        .AddAttributes(
            [new KeyValuePair<string, object>(
                TcjDiagnosticNames.Tags.FrameworkVersion,
                TcjTelemetry.FrameworkVersion)]))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource(
            TcjDiagnosticNames.Sources.Core,
            TcjDiagnosticNames.Sources.DependencyInjection,
            TcjDiagnosticNames.Sources.EntityFrameworkCore,
            TcjDiagnosticNames.Sources.EntityFrameworkCoreSqlServer,
            TcjDiagnosticNames.Sources.AspNetCore)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(
            TcjDiagnosticNames.Sources.Core,
            TcjDiagnosticNames.Sources.DependencyInjection,
            TcjDiagnosticNames.Sources.EntityFrameworkCore,
            TcjDiagnosticNames.Sources.EntityFrameworkCoreSqlServer,
            TcjDiagnosticNames.Sources.AspNetCore)
        .AddOtlpExporter());
```

`AddOtlpExporter` can be configured entirely through standard OpenTelemetry environment variables. Keep endpoints, headers, API keys, and credentials outside source control. A hosted collector is not required for the sample to compile; if no reachable endpoint is configured, exporting is simply an application/runtime concern rather than a TCJ concern.

For local development, an application may replace OTLP with the OpenTelemetry console exporter. Do not add a console exporter to TCJ production packages.

## Relationship to ASP.NET Core and EF Core spans

TCJ spans describe logical framework operations:

- the ASP.NET Core request span remains owned by ASP.NET Core instrumentation;
- `tcj.aspnetcore.exception.handle` is a child of the current request activity;
- repository and Unit of Work spans represent TCJ operations;
- EF Core or database-client instrumentation remains responsible for SQL command/network spans;
- TCJ never records raw command text or connection strings to duplicate lower-level instrumentation.

This avoids duplicate request/database spans while retaining a useful framework-level layer between the request and provider commands.

## Logging correlation

TCJ relies on standard `Activity` correlation. Applications and logging providers can use the current trace ID and span ID without a TCJ-specific correlation identifier. TCJ does not copy baggage into telemetry tags automatically.

The default TCJ ASP.NET Core exception log records the exception type, HTTP method, and trace identifier without logging the exception object, path/query, request payload, or exception message. Applications that require richer diagnostics should add them at an explicitly reviewed boundary.

## Dashboard and alert guidance

Prefer bounded aggregations such as:

- failure ratio by `tcj.operation.name`;
- domain-handler failure count;
- repository duration percentiles by provider and operation;
- Unit of Work commit failure rate and latency;
- handled 5xx count from the TCJ exception-handler metric.

Do not create dashboards grouped by entity IDs, user IDs, tenant IDs, route values, exception messages, or SQL statements.

## Local validation

Validate static contracts first:

```bash
python3 eng/verify-observability.py validate-config
```

Run the dedicated runtime suite:

```bash
dotnet test tests/TCJ.Observability.Tests/TCJ.Observability.Tests.csproj \
  --configuration Release \
  --logger "trx;LogFileName=observability.trx" \
  --results-directory TestResults/Observability
```

Generate and validate evidence:

```bash
python3 eng/verify-observability.py verify \
  --results TestResults/Observability \
  --output artifacts/observability
```

Generated output includes:

- `OBSERVABILITY_SUMMARY.md`
- `observability-summary.json`
- `activities.json`
- `metrics.json`
- `sensitive-data-scan.json`

The dedicated performance workflow also benchmarks four domain-event paths: telemetry disabled, tracing listener enabled, metrics listener enabled, and tracing plus metrics enabled. These benchmarks report execution time and managed allocations. A hard relative threshold should only be introduced after a real baseline is collected and reviewed.

## CI and release enforcement

Normal CI, release preflight, and the official tag-based release all:

1. validate the observability policy and committed contract;
2. build the exact source under test;
3. run `TCJ.Observability.Tests`;
4. verify the TRX evidence and sensitive-marker scan;
5. publish the Markdown summary to the GitHub Actions summary;
6. upload observability evidence as workflow artifacts.

The verifier also rejects ignored policy/contract files, missing stable names, missing package-version metadata, missing required tests, missing benchmark variants, forbidden telemetry dependencies in production packages, and missing workflow integration.

## Compatibility rules

Treat telemetry names and units as consumer-facing contracts:

- renaming/removing an activity is breaking for trace queries and dashboards;
- renaming/removing a metric is breaking;
- changing a metric type or unit is breaking;
- removing a commonly used tag can break dashboards;
- adding a bounded optional tag is generally compatible;
- adding a high-cardinality dimension requires explicit design review;
- telemetry-contract changes must be called out in release notes.

Update `eng/observability-contract.json`, tests, documentation, and the changelog together when an intentional contract change is required.

## Resilience instrumentation

Step 42 extends the existing `TCJ.Core` source and meter; it does not add a second telemetry backend or exporter dependency.

Activities:

- `tcj.resilience.execute`
- `tcj.resilience.retry`
- `tcj.resilience.timeout`
- `tcj.resilience.circuit_breaker`

Metrics:

- `tcj.resilience.attempts`
- `tcj.resilience.retries`
- `tcj.resilience.timeouts`
- `tcj.resilience.circuit_open`
- `tcj.resilience.failures`

Resilience dimensions are bounded strategy, outcome, attempt number, failure category, and circuit state values. Unknown consumer strategy labels collapse to `custom`; raw exception messages, SQL, connection strings, endpoint identifiers, user identifiers, and tenant identifiers are not resilience metric dimensions. See [Resilience policies and fault injection](resilience.md) for policy boundaries and idempotency requirements.
