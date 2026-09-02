# Transactional outbox

TCJ's transactional outbox closes the consistency gap between an Entity Framework Core business transaction and domain-event delivery. When enabled, pending domain events are serialized into `TCJ_OutboxMessages` during `SaveChanges` and are committed by the same database transaction as business state. A separate processor reads only committed records and dispatches them later.

## Delivery guarantee: at-least-once

The supported guarantee is **at-least-once delivery**. TCJ does **not** promise exactly-once delivery. A worker can successfully execute a handler and fail before it records `ProcessedAtUtc`; after the bounded lease expires, another worker can deliver the same message again. Handlers whose side effects cannot tolerate duplication must therefore be idempotent.

A practical idempotency pattern is a `ProcessedMessageId` table with a unique key on the outbox message ID. The handler writes that ID and its business side effect in the same local transaction. `IOutboxMessageContextAccessor.Current` exposes the stable message ID, stable logical event type, one-based attempt number, and optional persisted `CorrelationId`/`CausationId` while an outbox handler is running. TCJ intentionally does not mandate one idempotency store because the correct side-effect boundary belongs to the consuming application.

## Package and registration strategy

the transactional-outbox feature set preserves the existing five-runtime-package graph:

- `TCJ.Core` owns provider-neutral processor/replay/cleanup contracts, delivery metadata, state, and `TcjOutboxOptions`.
- `TCJ.EntityFrameworkCore` owns the outbox entity, EF mapping, safe JSON serialization, event-name resolution, SaveChanges/transaction interceptors, processor, startup validation, and health checks.
- `TCJ.EntityFrameworkCore.SqlServer` owns SQL Server claiming and status updates.
- `TCJ.AspNetCore` owns only the optional hosted polling loop and depends on Core contracts, not EF Core.

A typical SQL Server registration is:

```csharp
builder.Services.AddTcjDependencyInjection(typeof(Program).Assembly);

builder.Services.AddTcjSqlServer<AppDbContext>(connectionString);

builder.Services.AddTcjOutboxEvent<OrderCompleted>("order.completed.v1");

builder.Services.AddTcjSqlServerOutbox<AppDbContext>(options =>
{
    options.BatchSize = 100;
    options.PollingInterval = TimeSpan.FromSeconds(1);
    options.LockDuration = TimeSpan.FromSeconds(30);
    options.MaxRetryAttempts = 10;
});

// Optional. Omit this when a job/serverless host invokes IOutboxProcessor manually.
builder.Services.AddTcjOutboxProcessor();
```

The DbContext model must opt in explicitly:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.AddTcjOutbox();
}
```

TCJ uses **consumer-controlled migration** ownership. Generate and review an application migration after adding the mapping. The framework never applies outbox migrations automatically from startup, health checks, or the processor.

## Persistence schema

The default table is `TCJ_OutboxMessages`. `Id` is the primary key, so the database enforces uniqueness for the logical message. Required compatibility columns are:

```text
Id
OccurredAtUtc
EventType
Payload
AttemptCount
NextAttemptAtUtc
ProcessedAtUtc
LastErrorType
CreatedAtUtc
```

Operational columns include `LockedAtUtc`, `LockExpiresAtUtc`, `LockId`, `DeadLetteredAtUtc`, a bounded `LastError`, `UpdatedAtUtc`, `ReplayCount`, and `LastReplayedAtUtc`.

The mapping creates processing indexes for `(ProcessedAtUtc, NextAttemptAtUtc)`, `LockExpiresAtUtc`, `OccurredAtUtc`, and `EventType`. Schema or index changes are compatibility-sensitive and must update `eng/outbox-contract.json` plus migration guidance.

## SaveChanges and transaction boundary

The persistence sequence is:

1. an aggregate raises one or more `IDomainEvent` instances;
2. the outbox SaveChanges interceptor finds pending events before EF writes;
3. each event receives one stable GUID v7 message ID and is serialized once into an `OutboxMessage`;
4. business changes and the outbox rows are saved by the same DbContext/transaction;
5. domain events are cleared only after successful persistence; with an explicit transaction, clearing is deferred until commit;
6. rollback keeps the aggregate events available for retry and reuses the already assigned message IDs;
7. only after commit can a processor claim the durable row.

If `SaveChanges` fails inside an explicit transaction, TCJ requires that transaction to be rolled back before retry. A later commit is rejected instead of permitting ambiguous partial state. For an implicit failed `SaveChanges`, the captured message remains tracked with the same ID so a retry cannot generate a second logical row.

TCJ does not dispatch the domain event from the SaveChanges interceptor. Dispatch is deliberately post-commit through the outbox processor.

## Stable event names and schema versioning

Persisted event names are compatibility contracts. Prefer explicit logical names:

```csharp
services.AddTcjOutboxEvent<OrderCompleted>("order.completed.v1");
```

The fallback convention is `clr.<normalized-full-type-name>.v1`; it is useful for early development but explicit names are safer across namespace/type refactors. The default resolver never stores `AssemblyQualifiedName` because assembly versions make those names brittle.

A breaking payload change requires a new logical version such as `order.completed.v2`. Keep old CLR contracts/resolution available for as long as old persisted messages or deployments can still contain the earlier version.

## Serialization and sensitive data

`SystemTextJsonOutboxSerializer` is the default. It serializes the known event CLR type and deserializes only after `IOutboxEventTypeResolver` resolves a registered/logical name. It does not enable arbitrary runtime type activation or unsafe polymorphic type metadata. `TcjOutboxOptions.JsonSerializerOptions` can customize normal `System.Text.Json` behavior, and applications can replace `IOutboxSerializer` before outbox registration when they need redaction, encryption envelopes, or a different wire contract.

The payload is durable application data and may be sensitive. TCJ therefore follows these defaults:

- payloads are never added to logs, activity tags, metric dimensions, health responses, or workflow summaries;
- exception messages and stack traces are not persisted by default because third-party exceptions can echo request or payload data;
- only a bounded exception type and generic bounded failure text are retained;
- aggregate identifiers are not telemetry dimensions;
- the host owns database encryption at rest, key management, row/column protection, backups, and retention policy;
- access tokens, passwords, connection strings, or credentials should not be placed in domain-event payloads in the first place.

Custom serializers can perform field-level redaction or encryption, but their schema remains the application's compatibility responsibility.

## SQL Server claiming and bounded leases

SQL Server uses one short, parameterized atomic CTE update with `UPDLOCK`, `READPAST`, and `READCOMMITTEDLOCK`. `READCOMMITTEDLOCK` keeps the queue-reader pattern valid when a database enables `READ_COMMITTED_SNAPSHOT` (including common Azure SQL configurations). Eligible rows are ordered by:

```text
NextAttemptAtUtc
OccurredAtUtc
Id
```

The claim writes a unique `LockId`, `LockedAtUtc`, and `LockExpiresAtUtc`. The database lock is released as soon as the claim statement completes; TCJ does **not** hold a database transaction open around external handlers.

During a healthy lease, another worker skips the claimed row. If a worker crashes, the lease expires and a later worker can reclaim it. If a handler exceeds `LockDuration`, duplicate delivery becomes possible; configure the lease above the expected handler duration and keep side effects idempotent. Multiple application instances can safely share the table under these semantics.

## Batch processing

Manual processing is available in workers, jobs, tests, and serverless environments:

```csharp
IOutboxProcessor processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
OutboxProcessingResult result = await processor.ProcessBatchAsync(cancellationToken);
```

`BatchSize` is bounded to 1–1000. Each message records its own outcome, so one poison message does not permanently block later messages in the batch. Cancellation stops new work and leaves any unfinished claim recoverable by lease expiry.

The ASP.NET Core hosted service is opt-in through `AddTcjOutboxProcessor()`. It creates a new DI scope for each poll, never retains a DbContext, does not overlap its own loops, stops claiming on host cancellation, and uses the configured bounded polling delay when no immediate work is available.

## Retry and poison messages

Transient classification uses `ITransientFailureDetector`, the same bounded failure-classification boundary introduced by the resilience work. Transient delivery failures are scheduled using exponential backoff capped by `MaxRetryDelay`; deterministic bounded jitter can be enabled with `UseJitter`. Retry metadata is stored in the row rather than implemented as a hot in-process retry loop.

`MaxRetryAttempts` is the number of retries after the initial attempt. When a failure is permanent, or a transient message exhausts its retry budget, the row receives `DeadLetteredAtUtc` and stops automatic delivery. Dead-lettered messages stay queryable and later eligible messages continue processing.

## Replay

Replay is deliberately explicit:

```csharp
OutboxReplayResult replay = await replayService.ReplayAsync(messageId, cancellationToken);
```

Only a dead-lettered, non-active message can be replayed. The original message ID is preserved, attempt scheduling is reset, and replay audit metadata is incremented. Authorization belongs to the host application. TCJ does not create a public HTTP replay endpoint automatically and never exposes a payload as part of replay APIs. Replaying can repeat side effects; apply the same idempotency rules as normal duplicate delivery.

## Cleanup and retention

`IOutboxCleanupService.CleanupAsync` deletes only successfully processed rows older than `RetentionPeriod`, never pending, retryable, actively claimed, or dead-lettered rows. Cleanup is ordered and bounded by `CleanupBatchSize`. Set `RetentionPeriod = TimeSpan.Zero` to disable cleanup. The hosted service invokes cleanup at `CleanupInterval` only when retention is enabled; manual hosts can schedule it themselves.

If processed events must be archived, perform archival in host-owned operations before cleanup. TCJ does not silently copy payloads to another store.

## Observability

Outbox activities are stable contract names:

```text
tcj.outbox.persist
tcj.outbox.claim
tcj.outbox.process
tcj.outbox.retry
tcj.outbox.dead_letter
tcj.outbox.replay
tcj.outbox.cleanup
```

Metrics include persisted/processed/failed/retried/dead-lettered counts, processing duration, pending count, and oldest-pending age. Bounded tags are `tcj.outbox.outcome`, `tcj.outbox.event_type`, `tcj.outbox.attempt`, `tcj.outbox.provider`, and `tcj.canceled`. Event names should therefore remain stable and bounded. Payload and aggregate IDs are excluded.

## Health checks and startup validation

Outbox registration adds:

```text
tcj.outbox.processor
tcj.outbox.backlog
tcj.outbox.dead_letters
```

The checks expose only safe aggregate state. Backlog readiness compares oldest pending age with `BacklogUnhealthyAge`; dead-letter readiness uses `DeadLetterUnhealthyThreshold`. Temporary handler outages do not make process liveness depend on an external system.

Before processing, startup validation verifies the configured provider, outbox entity mapping, primary-key uniqueness, required properties, and required model indexes. Missing serializer/resolver/dispatcher/storage dependencies fail DI or validation instead of silently disabling reliability. SQL Server storage must match the SQL Server DbContext provider.

## Local testing

Docker must be available for the SQL Server Testcontainer tests:

```bash
python3 eng/verify-outbox.py validate-config

dotnet test tests/TCJ.Outbox.Tests/TCJ.Outbox.Tests.csproj \
  --configuration Release \
  --logger "trx;LogFileName=outbox.trx" \
  --results-directory TestResults/Outbox

python3 eng/verify-outbox.py verify \
  --results TestResults/Outbox \
  --output artifacts/outbox
```

The verifier rejects missing/ignored policy and contract files, schema/index drift, missing required test scenarios, too few tests, unsafe event-name behavior, payload leakage patterns, missing health/telemetry declarations, and missing CI/release integration.

## Deployment and upgrade considerations

1. Deploy the consumer-controlled migration that creates `TCJ_OutboxMessages` before starting processors.
2. Register all event names that can exist in the database before changing or removing CLR event contracts.
3. Roll out processor-enabled instances only after the schema exists.
4. Run multiple instances safely, but size batch/lease values for actual handler latency and SQL capacity.
5. Keep old event versions resolvable until their persisted backlog is drained or migrated intentionally.
6. On shutdown, hosted processing stops polling, honors cancellation, and leaves incomplete leases recoverable.
7. Review `eng/outbox-contract.json` when changing schema, defaults, telemetry names, health names, or delivery semantics.

Adding outbox support is opt-in. Consumers that do not register it retain the previous SaveChanges behavior and do not acquire an outbox schema requirement.

### Inbox correlation and causation metadata

When an Outbox event is persisted while an Inbox handler is active, the optional Inbox `CorrelationId` is copied to the Outbox row and the stable inbound `MessageId` becomes the Outbox `CausationId`. These nullable metadata columns are not part of duplicate identity and are never emitted as metric dimensions. Existing Outbox-enabled consumers adopting the preview.5 mapping require a consumer-controlled migration that adds nullable `CorrelationId` and `CausationId` columns to `TCJ_OutboxMessages`.
