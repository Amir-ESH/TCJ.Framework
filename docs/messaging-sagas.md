# Durable Saga and process-manager orchestration

TCJ Saga orchestration coordinates long-running workflows across multiple messages and local database transactions without introducing a distributed transaction. The feature is opt-in and is split into transport-neutral, EF Core, and SQL Server packages so applications only acquire the dependencies they use.

## Packages

| Package | Responsibility |
|---|---|
| `TCJ.Messaging.Sagas` | Transport-neutral Saga contracts, lifecycle, policies, context, diagnostics, timer/remediation abstractions, and explicit state-migration contracts. |
| `TCJ.Messaging.Sagas.EntityFrameworkCore` | Provider-neutral EF Core persistence, explicit definition registration, Inbox integration, state serialization, timer processing, health checks, cleanup, and remediation. |
| `TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer` | SQL Server `rowversion` optimistic concurrency, active-correlation uniqueness, and atomic lease-based timer claiming. |

The transport-neutral package does not depend on EF Core, SQL Server, ASP.NET Core, or a broker SDK. Existing TCJ packages do not depend on the Saga packages.

## Consistency and transaction ownership

Externally delivered Saga messages use the existing transactional Inbox. The Inbox remains the transaction owner and Saga message handlers use the same scoped application `TDbContext`; they do not begin or commit another database transaction.

The required durability boundary is:

```text
transport delivery
-> Inbox acquisition/deduplication
-> Saga definition and correlation resolution
-> Saga state load/create
-> Saga transition and application state changes
-> Saga timer mutations
-> existing Outbox-compatible event capture
-> DbContext.SaveChanges
-> Inbox completion
-> database commit
-> transport settlement
```

`Inbox + Saga + application/business state + Saga timers + Outbox` therefore commit or roll back together for an externally delivered message. Saga handlers must not use direct broker publication as a substitute for the transactional Outbox. Broker publication occurs later through the existing Outbox processing path.

TCJ does not provide a distributed ACID transaction, global exactly-once delivery, or automatic rollback of remote operations.

## Registration

Register transactional Inbox and Outbox for the same application DbContext, add SQL Server Saga persistence, configure the model, and then register each Saga definition explicitly.

```csharp
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using TCJ.Messaging.Sagas;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;
using TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer.Extensions;

services.AddTcjSqlServerInbox<AppDbContext>();
services.AddTcjSqlServerOutbox<AppDbContext>();
services.AddTcjSqlServerSagas<AppDbContext>();

services.AddTcjSaga<AppDbContext, OrderSaga, OrderSagaState>(
    sagaType: "orders.fulfillment",
    definitionVersion: 2,
    stateSchemaVersion: 3,
    stateJsonTypeInfo: AppJsonContext.Default.OrderSagaState,
    stateFactory: static () => new OrderSagaState(),
    configure: saga => saga
        .TerminalStates("Completed", "Failed", "Compensating", "Compensated")
        .StartsWith<OrderSubmitted>(
            messageType: "orders.submitted",
            messageVersion: 1,
            initialState: "AwaitingPayment",
            correlationName: "order-id",
            correlate: static message => SagaCorrelationKey.From(message.OrderId))
        .Handles<PaymentAuthorized>(
            messageType: "payments.authorized",
            messageVersion: 1,
            correlationName: "order-id",
            correlate: static message => SagaCorrelationKey.From(message.OrderId),
            allowedStates: ["AwaitingPayment"])
        .HandlesTimeout(
            timerName: "payment-deadline",
            timeoutType: "orders.payment-deadline.v1",
            allowedStates: ["AwaitingPayment"])
        .Compensates()
        .SupportsDefinitionVersion(1)
        .AddStateMigrator<OrderSagaStateV2ToV3Migrator>(2, 3));
```

The example uses source-generated `JsonTypeInfo<TState>` metadata. Runtime assembly scanning, arbitrary `Type.GetType` activation, and assembly-qualified CLR names are not persistence contracts.

The application model must include the Saga model:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.AddTcjSqlServerSagas();
}
```

Startup validation fails when the Saga integration is configured for a different DbContext than the transactional Inbox/Outbox or when a provider-specific optimistic concurrency configuration is missing.

## Consumer-controlled migrations

TCJ does not ship application database migrations. After adding `AddTcjSqlServerSagas()` to the consumer model, generate and review a migration in the application repository and deploy it using the application's normal migration process.

```bash
dotnet ef migrations add AddTcjSagas --project <application-project>
dotnet ef database update --project <application-project>
```

The generated schema includes Saga instances, correlations, timers, SQL Server `rowversion` concurrency tokens, and a filtered unique index for active correlations. Migration names and deployment strategy remain consumer-owned.

## State and versioning

Application state implements the minimal `ISagaState` marker and remains strongly typed. Framework lifecycle metadata such as Saga ID, logical Saga type, definition version, state schema version, current transition state, status, timestamps, and concurrency token is stored separately from the serialized application state payload.

`TcjSagaOptions.MaximumStatePayloadBytes` bounds persisted state payloads. Payloads are not emitted to logs, metrics, activities, or health responses.

`SagaType`, state names, message logical names/versions, timer names, and correlation names are compatibility contracts. Do not rename them for active Saga instances without a migration plan.

Persisted `DefinitionVersion` and `StateSchemaVersion` are independent. Older active definition versions must be explicitly declared with `SupportsDefinitionVersion`. State schema evolution uses explicit forward migrators registered with `AddStateMigrator<TMigrator>`. The migration graph is validated at startup; unsupported versions or incomplete graphs fail safely. State migration executes in the same database transaction and does not change the Saga ID or its durable correlations.

## Correlation and identity

Each Saga receives one framework-generated `SagaId` for its complete lifetime. Retry, redelivery, timeout execution, compensation, remediation, and state migration do not replace it.

Correlation is explicit and deterministic. Supported correlation primitives are `Guid`, `string`, `long`, and `int`. String correlation is ordinal and case-sensitive; TCJ does not trim or change casing implicitly. Applications must normalize business keys deliberately before constructing `SagaCorrelationKey` when different normalization semantics are required.

Correlation values are bounded. The EF persistence layer stores a deterministic SHA-256 value hash instead of the raw correlation value, and the SQL Server package enforces uniqueness for active `(SagaType, CorrelationName, ValueHash)` tuples. This database constraint protects concurrent starts; an application-only pre-check is not used as the correctness boundary.

Raw correlation values are not default telemetry dimensions and `SagaCorrelationKey.ToString()` is intentionally redacted.

## Lifecycle and message policies

Definitions register legal start and continuation messages explicitly. `SagaContext` provides deterministic lifecycle operations for staying in the current state, transitioning, completion, failure, durable timer scheduling/canceling, Outbox-compatible event emission, and explicit compensation requests.

Missing-instance and invalid-transition policies are explicit. Retry behavior is bounded by the existing Inbox retry semantics; the Saga runtime does not run arbitrary application handlers repeatedly inside the same failed database transaction. A retryable transition rolls back, is classified through Inbox retry semantics, and reloads current durable Saga state on a later attempt.

Terminal Sagas reject ordinary continuation unless that handler was explicitly configured to allow a terminal instance. The initial implementation does not add a second pending/orphan message payload store.

## Optimistic concurrency

Optimistic concurrency is the default. SQL Server uses `rowversion` for Saga instances and timers. Concurrent transitions never rely on a process-local mutex or a global serialization lock.

An EF Core `DbUpdateConcurrencyException` from Saga persistence is classified as retryable `InboxFailureType.ConcurrencyConflict`. The failed Inbox/Saga/application/timer/Outbox transaction rolls back as one unit. A later Inbox attempt reloads the current Saga state before executing the transition again.

Different Saga instances remain independently processable.

## Durable Saga timers

Saga timers are durable Saga-owned deadlines, not a general-purpose scheduler. Scheduling and cancellation are persisted with the transition that requested them.

The transport-independent `ISagaTimerProcessor` obtains due timer claims through provider-specific storage. SQL Server claiming uses an atomic bounded batch with `UPDLOCK`, `READPAST`, `ROWLOCK`, and a lease. A valid active lease prevents two workers from executing the same claim concurrently; an expired lease can be reclaimed after a failed process.

A timeout transition and successful timer completion commit in one timer-processing transaction. Failure before commit leaves the durable timer retryable. Timer attempts are bounded by `TcjSagaOptions.MaxTimerAttempts`. Native broker scheduling is not required for Saga correctness.

## Compensation

Compensation is explicit application behavior. It represents a new business action that attempts to counter a previously completed remote effect; it is not a database rollback and TCJ does not infer compensation for arbitrary side effects.

A Saga definition opts in with `Compensates()` and implements `ISagaCompensates<TState>`. Compensation state is durable and bounded attempts use `TcjSagaOptions.MaxCompensationAttempts`. Any external compensation message should be emitted through the existing Outbox-compatible durable event path so publication occurs only after the database transaction commits.

Compensation handlers must be designed for idempotency because delivery and execution are at-least-once at system boundaries.

## Cleanup, retention, and remediation

Cleanup operates only on terminal retained Saga instances and in bounded batches. An active Saga is never cleanup-eligible. A terminal instance with active timers or pending compensation is not removed unsafely.

Saga retention must be configured together with Inbox retention. Retaining Inbox deduplication data for less time than the business can redeliver old Saga messages can permit an old delivery to be processed after its Inbox record has been removed. Choose both retention periods from the application's delivery and recovery requirements.

`ISagaRemediationService` works from current durable state and uses optimistic concurrency. It is not an event-sourcing replay engine and TCJ does not claim that a Saga can be reconstructed from an event history the framework does not own. No public unauthenticated Saga administration endpoint is added.

## Observability and health

Saga activities and metrics use stable Saga type, operation, and outcome dimensions with bounded cardinality. Raw Saga state, correlation values, Saga IDs, and timer payload data are not emitted as default telemetry dimensions.

Health checks are intentionally split:

- `tcj.saga.liveness` is independent of the database and business Saga outcomes.
- `tcj.saga.readiness` validates Saga infrastructure/configuration readiness without treating a failed business Saga as a readiness failure by default.

Health output is sanitized and does not include persisted Saga state or raw correlation values.

## Trimming and Native AOT

`TCJ.Messaging.Sagas` uses explicit definition registration and caller-provided `JsonTypeInfo<TState>` metadata and is recorded as **Conditional** in the repository AOT policy until packed-NuGet Native AOT evidence exists.

The EF Core and SQL Server Saga packages are recorded as **Experimental** for Native AOT because they inherit the existing EF Core/provider Native AOT boundary. They do not claim production Native AOT support. See [Native AOT and trimming](guides/native-aot-and-trimming.md).

## Transport compatibility

Saga orchestration is above broker adapters and the Inbox boundary. Broker-specific offset, lock, rebalance, retry-topic, and settlement correctness remains owned by the existing messaging adapter conformance suites.

The focused Saga transport scenario verifies the common semantic sequence for each supported transport where infrastructure is available:

```text
start Saga
-> commit Saga + Outbox
-> publish through existing Outbox/messaging path
-> receive continuation
-> Inbox deduplicates
-> transition completes
```

The expected logical Saga result is transport-independent; adapter-native mechanics are not duplicated in Saga tests.

## Guarantees and non-goals

The Saga feature provides local transactional consistency around the application database plus at-least-once integration through Inbox/Outbox boundaries. It does **not** claim:

- distributed ACID transactions across services;
- global exactly-once delivery;
- automatic rollback of remote operations;
- automatic compensation for unknown side effects;
- a general-purpose scheduler;
- BPMN or arbitrary workflow scripting;
- event-sourcing replay/reconstruction guarantees;
- transport-specific Saga APIs.
