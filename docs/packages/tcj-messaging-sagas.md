# TCJ.Messaging.Sagas

`TCJ.Messaging.Sagas` contains the transport-neutral contracts for durable Saga / Process Manager orchestration. It has no EF Core, SQL Server, ASP.NET Core, or broker SDK dependency.

## Install

```bash
dotnet add package TCJ.Messaging.Sagas --prerelease
```

- **Target framework:** `net10.0`
- **Primary TCJ dependencies:** `TCJ.Messaging` and Core domain-event contracts
- **Primary concepts:** `ISagaState`, Saga handlers, `SagaContext`, explicit policies, `ISagaTimerProcessor`, and `ISagaRemediationService`
- **Registration:** explicit; no assembly scanning is required or used as the registration contract
- **Serialization:** caller-provided `JsonTypeInfo<TState>` through the EF integration

Saga types, definition versions, state-schema versions, state names, timer names, and correlation semantics are durable compatibility contracts. Correlation is deterministic and bounded; raw correlation values are not the default telemetry dimension or framework persistence representation.

Saga handlers emit durable outbound work through the existing Outbox-compatible domain-event path. Direct broker publication from an in-flight Saga database transaction is not the supported durability boundary.

This package does not provide persistence. For SQL Server use [TCJ.Messaging.Sagas.EntityFrameworkCore](tcj-messaging-sagas-entityframeworkcore.md) and [TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer](tcj-messaging-sagas-entityframeworkcore-sqlserver.md).

See [Durable Saga orchestration](../messaging-sagas.md) for transaction ownership, timers, compensation, versioning, cleanup, observability, and migration guidance.
