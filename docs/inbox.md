# Transactional Inbox

TCJ's transactional Inbox is an opt-in, database-backed idempotency boundary for externally delivered messages. Brokers normally provide **at-least-once** delivery, so a transport may deliver the same logical message more than once after an acknowledgement timeout, process crash, network uncertainty, operator replay, or concurrent consumer race.

The Inbox guarantee is deliberately narrower than global exactly-once delivery:

> **effectively-once committed database side effects for a stable message ID inside one configured consumer boundary.**

TCJ does **not** claim global exactly-once delivery. A broker may redeliver, and non-transactional external side effects such as HTTP calls, email, payment gateways, files, and other databases still need their own idempotency strategy.

## Packages and architecture

Step 45 uses the existing package graph:

- `TCJ.Core` contains transport-neutral contracts and options.
- `TCJ.EntityFrameworkCore` contains the Inbox entity, EF mapping, serializer/registry, transaction coordinator, health checks, and provider-independent processing.
- `TCJ.EntityFrameworkCore.SqlServer` contains SQL Server insert/duplicate/claim/lease behavior.
- `TCJ.AspNetCore` contains the optional hosted deferred processor.

No broker SDK is required. Azure Service Bus, RabbitMQ, Kafka, SQS, NATS, MassTransit, Wolverine, Rebus, and CAP remain outside this package contract.

## Stable message identity and consumer boundaries

Every delivery must supply a stable `MessageId`. Generating a random ID for every redelivery defeats idempotency and is rejected as an integration design. Duplicate detection is scoped by the configured `ConsumerName` and the database enforces:

```text
UNIQUE (ConsumerName, MessageId)
```

Consumer names are case-sensitive TCJ contracts, limited to 128 characters, and permit letters, digits, `.`, `-`, and `_`. Renaming a consumer changes the idempotency boundary and can make previously processed messages appear new. Treat consumer-name changes as a migration/operational compatibility change.

Message types are explicit logical names with an explicit positive schema version. Wire contracts never use `AssemblyQualifiedName`. Breaking payload changes require a new version or an application-controlled migration/upcasting layer before registration.

## Registration

```csharp
builder.Services.AddTcjInboxMessage<OrderSubmitted>(
    "order.submitted",
    version: 1);

builder.Services.AddTcjInboxHandler<
    OrderSubmitted,
    OrderSubmittedHandler>();

builder.Services.AddTcjSqlServerInbox<AppDbContext>(options =>
{
    options.ConsumerName = "orders-api";
    options.ProcessingMode = InboxProcessingMode.Inline;
    options.MaxRetryAttempts = 10;
});
```

Map the schema in the same consumer-owned `DbContext` that owns business state:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.AddTcjInbox();
    modelBuilder.AddTcjOutbox(); // when outbound messages are required
}
```

Generate and apply the migration in the host application. TCJ does not run automatic production migrations.

## Incoming envelope

`IncomingMessageEnvelope` carries only transport-neutral data: stable message ID/type/version, consumer, serialized payload, receive time, optional correlation/causation identifiers, and an immutable copy of headers. Envelope identifiers are bounded and control characters are rejected.

The default persisted header allowlist is:

```text
correlation-id
causation-id
content-type
traceparent
tracestate
```

Authorization, cookies, API keys, access tokens, refresh tokens, and other unapproved headers are not retained by default.

## Serialization and schema versions

The default serializer is `System.Text.Json`. The CLR target type comes only from the explicit `(MessageType, MessageVersion)` registry. The serializer uses `JsonTypeInfo` from configured `JsonSerializerOptions`; arbitrary assembly-qualified type activation and unsafe polymorphic metadata are not enabled by TCJ.

A custom `IInboxSerializer` can be registered before Inbox registration. Unknown message types and unknown versions fail safely and are not dispatched to an arbitrary CLR type.

## Inline processing

Inline mode provides the strongest database-side boundary:

1. validate the envelope and configured consumer;
2. begin the application database transaction;
3. insert/acquire the Inbox row;
4. let the database unique constraint resolve concurrent duplicates;
5. deserialize only the registered CLR contract;
6. invoke the handler;
7. save business changes;
8. capture Outbox records, when Outbox is enabled;
9. mark the Inbox record processed;
10. commit;
11. return a transport-neutral acknowledgement recommendation.

The transport acknowledgement is intentionally **outside** the database transaction and occurs only after the transaction outcome is known.

If handler execution fails, the business/Outbox/processing transaction is rolled back. A bounded safe retry/dead-letter record is then written separately. The handler scope and `DbContext` are disposed after the failed attempt, so tracked state from a rolled-back handler is not reused by the next delivery.

## Deferred processing

Deferred mode durably stores the inbound payload first. The transport may acknowledge according to its adapter policy after that receipt commit. An optional hosted processor can be enabled with:

```csharp
builder.Services.AddTcjInboxProcessor();
```

The SQL Server worker uses bounded leases with `UPDLOCK`, `READPAST`, and `READCOMMITTEDLOCK`. A claimed row is locked again inside the handler transaction before business work begins. If a process crashes, the lease expires and another worker can reclaim the message.

Deferred mode requires `StorePayload = true` because the handler runs after the transport delivery has been acknowledged.

## Duplicate handling

For the same `ConsumerName + MessageId`:

- a processed record returns `IgnoreDuplicate` and does not invoke the handler;
- a currently active record returns a retry recommendation when it cannot be safely acquired;
- a retryable record is processed only when its bounded retry time is due;
- a dead-lettered record is not processed automatically;
- a different SHA-256 payload hash is a contract/security conflict and returns a safe dead-letter recommendation without invoking the handler.

Application-side `SELECT` checks are not the idempotency guarantee. The database unique constraint is authoritative under races.

## Retry and dead letters

Retry delays use bounded exponential backoff with optional bounded jitter. The default maximum retry count is 10 and the hard configuration limit is 20. Cancellation, validation errors, unknown contracts/versions, permanent deserialization failures, and other permanent failures are not retried indefinitely.

Dead-lettering is a terminal Inbox state for automatic processing. Poison messages do not block later eligible rows because SQL Server claims use `READPAST` and bounded batches.

Persisted failure diagnostics contain a bounded failure category and a generic safe summary. Raw exception messages, stack traces, payloads, credentials, and authorization headers are not emitted to logs, telemetry, or health responses by default.

## Replay

Replay is explicit through `IInboxReplayService`. TCJ does not expose an HTTP replay endpoint automatically. The host must authorize any administrative endpoint or command that calls replay.

Replay:

- identifies the durable row by Inbox ID;
- preserves the original `MessageId` and consumer boundary;
- rejects non-dead-lettered records;
- rejects records with an active lease;
- requires a retained payload;
- increments `ReplayCount` and records `LastReplayedAtUtc`;
- resets retry/failure state so the message becomes eligible again.

Replay can repeat non-transactional external side effects. Authorization and business-specific idempotency remain host responsibilities.

## Cleanup and retention

`IInboxCleanupService` deletes only processed records older than `RetentionPeriod`, in batches capped by `CleanupBatchSize`. Active, retryable, received, processing, and dead-lettered records are preserved. Set retention to zero to disable cleanup.

The default retention is 14 days. Applications should choose retention according to replay needs, privacy requirements, database capacity, and backup policy.

## Inbox and Outbox transaction chain

When the transactional Outbox is enabled on the same `DbContext`, the preferred chain is:

```text
Inbound broker message
-> Inbox duplicate detection
-> Business handler
-> Business database changes
-> Outbox records
-> Inbox processed state
-> one database commit
-> broker acknowledgement
-> later Outbox publication
```

Inbox context propagates the inbound correlation ID into generated Outbox records and uses the inbound `MessageId` as outbound causation metadata. Duplicate inbound messages therefore do not create a second committed business result or a second Outbox record.

## Trace propagation and observability

Inbox diagnostics use the existing `TCJ.EntityFrameworkCore` `ActivitySource`/`Meter`. Stable activity contracts are:

```text
tcj.inbox.receive
tcj.inbox.deduplicate
tcj.inbox.process
tcj.inbox.retry
tcj.inbox.dead_letter
tcj.inbox.replay
tcj.inbox.cleanup
```

Stable metrics are:

```text
tcj.inbox.messages.received
tcj.inbox.messages.processed
tcj.inbox.messages.duplicates
tcj.inbox.messages.failed
tcj.inbox.messages.retried
tcj.inbox.messages.dead_lettered
tcj.inbox.processing.duration
tcj.inbox.pending.count
tcj.inbox.oldest_pending.age
```

Dimensions are bounded registered contracts such as consumer, registered message type/version, bounded outcome/failure category, attempt, and provider. Raw payloads, raw headers, message IDs, user IDs, aggregate IDs, credentials, and exception messages are not telemetry dimensions.

`traceparent` and `tracestate` are allowlisted for transport adapters; adapters remain responsible for validating/extracting remote trace context before creating or passing the envelope. TCJ never stores full baggage by default.

## Health and startup validation

The Inbox registers readiness checks:

```text
tcj.inbox.configuration
tcj.inbox.processor
tcj.inbox.backlog
tcj.inbox.dead_letters
```

Startup validation checks the consumer options, provider/storage match, mapped `TCJ_InboxMessages` entity, required columns, and database-enforced consumer/message unique index. Deferred readiness also reports whether the processor has started. Health data contains only aggregate counts/ages and bounded failure types.

Liveness is not made dependent on the broker or other transient downstream services.

## SQL Server schema

Default table: `TCJ_InboxMessages`.

Required identity constraint:

```text
UX_TCJ_InboxMessages_ConsumerName_MessageId
```

Operational indexes cover status/retry time, lease expiry, receive time, processed time, and message type. Raw SQL is provider-specific, parameterized, and contains no user-controlled SQL fragments.

## Security and payload retention

`StorePayload = false` provides metadata-only persistence for Inline mode. It reduces sensitive-data retention but prevents replay from the stored record. Deferred processing requires payload retention.

TCJ does not log stored payloads or include them in telemetry/health output. Applications remain responsible for encryption at rest, database permissions, key management, backup protection, data residency, and retention policy. Do not put authorization credentials into message payloads or persisted headers.

## Migration and upgrade guidance

Inbox is opt-in. Applications that do not call `AddTcjInbox`/`AddTcjSqlServerInbox` are unaffected. Enabling Inbox requires adding the `TCJ_InboxMessages` schema in a consumer-controlled migration.

Changing any of these requires explicit compatibility review and migration planning:

- `ConsumerName`;
- logical message type or schema version;
- the consumer/message unique key;
- status/storage columns;
- telemetry/health contracts;
- retention/payload policy.

When upgrading an existing Outbox-enabled application, also migrate the nullable Outbox `CorrelationId` and `CausationId` columns introduced for Inbox-to-Outbox propagation.

## Local validation

```bash
python3 eng/verify-inbox.py validate-config

dotnet test tests/TCJ.Inbox.Tests/TCJ.Inbox.Tests.csproj \
  --configuration Release \
  --logger "trx;LogFileName=inbox.trx" \
  --results-directory TestResults/Inbox

python3 eng/verify-inbox.py verify \
  --results TestResults/Inbox \
  --output artifacts/inbox
```

Generated `TestResults/Inbox/` and `artifacts/inbox/` evidence is not source and must not be committed.
